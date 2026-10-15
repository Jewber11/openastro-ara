#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Endpoints;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Moq;
using NUnit.Framework;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
using OpenAstroAra.Equipment.Equipment.MyFocuser;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Image.ImageAnalysis;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Test;

[TestFixture]
public class FilterFocusOffsetsTest {
    private static readonly int[] ExpectedBrackets = [0, 1, 0, 2, 0];
    private static readonly int[] ExpectedOffsets = [0, 200, -100];
    private static readonly int[] ExpectedTicks = [1, 2, 3, 4, 5];
    private static readonly int[] RetryMoves = [10_300, 10_200];
    private sealed class Rig : IDisposable {
        public InMemoryProfileStore Store { get; } = new();
        public Mock<IFocuserMediator> Focuser { get; } = new();
        public Mock<IFocuserService> Focus { get; } = new();
        public Mock<IFilterWheelMediator> Wheel { get; } = new();
        public Mock<IFilterWheelService> WheelService { get; } = new();
        public ActiveRunSessionRegistry Runs { get; } = new();
        public AutofocusSweepService Sweep { get; }
        public FilterFocusOffsetService Offsets { get; }
        public int Position { get; set; } = 10_000;
        public int Slot { get; set; }
        public int FailStarsOnSlot { get; set; } = -1;
        public bool FailNextMove { get; set; }
        public bool FailAfterWheelRotation { get; set; }
        public bool TempComp { get; set; }
        public int MaxPosition { get; set; } = 20_000;
        public string FocuserId { get; set; } = "focuser";
        public List<int> WheelMoves { get; } = [];
        public List<int> FocusMoves { get; } = [];
        public FilterSlotDto[] Slots { get; } = [new(0, "L", 0), new(1, "Ha", 0), new(2, "OIII", 0)];

        public Rig() {
            Store.PutAutofocusSettings(Store.GetAutofocusSettings() with { Steps = 4, StepSize = 100, StepSizeAuto = false });
            Focuser.Setup(f => f.GetInfo()).Returns(() => new FocuserInfo {
                Connected = true, DeviceId = FocuserId, Position = Position
            });
            Focuser.Setup(f => f.MoveFocuser(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>((p, ct) => {
                    ct.ThrowIfCancellationRequested();
                    FocusMoves.Add(p);
                    if (FailNextMove) {
                        FailNextMove = false;
                        Position = p - 3;
                        throw new InvalidOperationException("simulated partial focuser move");
                    }
                    Position = p;
                    return Task.FromResult(p);
                });
            Focus.Setup(f => f.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => new FocuserDto(
                FocuserId, "Focuser", EquipmentConnectionState.Connected, new(0, MaxPosition, 1, true, true),
                new("idle", Position, 12, TempComp)));
            Wheel.Setup(w => w.GetInfo()).Returns(() => new FilterWheelInfo {
                Connected = true, DeviceId = "wheel", SelectedFilter = Filter(Slot)
            });
            Wheel.Setup(w => w.ChangeFilter(It.IsAny<FilterInfo>(), It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>()))
                .Returns<FilterInfo, IProgress<ApplicationStatus>?, CancellationToken>((f, _, ct) => {
                    ct.ThrowIfCancellationRequested();
                    Slot = f.Position;
                    WheelMoves.Add(Slot);
                    if (FailAfterWheelRotation) { FailAfterWheelRotation = false; throw new InvalidOperationException("simulated read failure after rotation"); }
                    return Task.FromResult(Filter(Slot));
                });
            WheelService.Setup(w => w.GetAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() =>
                new FilterWheelDto("wheel", "Wheel", EquipmentConnectionState.Connected, new("idle", Slot), Slots));
            var frames = new Mock<IAnalysisFrameSource>();
            frames.Setup(f => f.CaptureForAnalysisAsync(It.IsAny<double>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AnalysisFrame(new ushort[16], 4, 4, DateTimeOffset.UtcNow));
            Offsets = new(Store, Focus.Object, Focuser.Object);
            Sweep = new(Store, Focuser.Object, frames.Object, filterWheel: Wheel.Object, filterOffsets: Offsets,
                metric: (_, _) => new StarDetectionResult {
                    AverageHFR = 1.5 + 0.2 * Math.Pow((Position - (10_000 + (Slot == 1 ? 200 : Slot == 2 ? -100 : 0))) / 100.0, 2),
                    DetectedStars = Slot == FailStarsOnSlot ? 0 : 42,
                    StarList = []
                });
        }

        public FilterInfo Filter(int slot) => new(Slots[slot].Name, 0, checked((short)slot));
        public FilterFocusOffsetsDto Calibration() => new("wheel", "focuser", Store.GetOpticsSettings(), 0, DateTimeOffset.UtcNow,
            [new(0, "L", 0), new(1, "Ha", 200), new(2, "OIII", -100)]);
        public Task<FilterFocusOffsetsDto> Calibrate(Action<int>? tick = null, CancellationToken ct = default) =>
            Sweep.CalibrateFilterOffsetsAsync(0, Offsets, WheelService.Object, Focus.Object, Runs, null, tick ?? (_ => { }), ct);
        public void Dispose() { Sweep.Dispose(); Offsets.Dispose(); }
    }

    [TestCase(10_000, 10_220, 10_040, 0.5, 200)]
    [TestCase(10_040, 9_930, 10_000, 0.25, -100)]
    [TestCase(10_000, 10_000, 10_000, 0.5, 0)]
    public void Calculation_removes_time_interpolated_reference_drift(int before, int target, int after, double fraction, int expected) =>
        Assert.That(AutofocusSweepService.CalculateFilterOffset(before, target, after, fraction), Is.EqualTo(expected));

    [Test]
    public async Task Full_calibration_runs_reference_brackets_saves_all_filters_and_ends_focused_on_reference() {
        using var rig = new Rig();
        var ticks = new List<int>();
        var result = await rig.Calibrate(ticks.Add);
        using (Assert.EnterMultipleScope()) {
            Assert.That(rig.WheelMoves, Is.EqualTo(ExpectedBrackets));
            Assert.That(result.Filters.Select(f => f.OffsetSteps), Is.EqualTo(ExpectedOffsets));
            Assert.That(ticks, Is.EqualTo(ExpectedTicks));
            Assert.That(rig.Store.GetFilterFocusOffsets(), Is.SameAs(result));
            Assert.That(rig.Position, Is.EqualTo(10_000).Within(2));
            Assert.That(rig.Slot, Is.Zero);
            Assert.That(rig.Runs.MaintenanceActive, Is.False);
        }
    }

    [Test]
    public async Task Changes_apply_signed_differences_once_and_reverse_without_accumulating() {
        using var rig = new Rig();
        rig.Store.PutFilterFocusOffsets(rig.Calibration());
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(rig.Position, Is.EqualTo(10_200));
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(rig.FocusMoves, Has.Count.EqualTo(2));
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(2), CancellationToken.None);
        Assert.That(rig.Position, Is.EqualTo(9_900));
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(0), CancellationToken.None);
        Assert.That(rig.Position, Is.EqualTo(10_000));
    }

    [Test]
    public async Task Retry_after_partial_focus_failure_finishes_original_destination_once() {
        using var rig = new Rig();
        rig.Store.PutFilterFocusOffsets(rig.Calibration());
        rig.FailNextMove = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None));
        Assert.That(rig.Slot, Is.EqualTo(1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(2), CancellationToken.None));
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(rig.Position, Is.EqualTo(10_200));
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(rig.FocusMoves, Is.EqualTo(RetryMoves));
    }

    [TestCase("equipment")]
    [TestCase("optics")]
    [TestCase("range")]
    [TestCase("tempcomp")]
    [TestCase("slots")]
    public async Task Invalid_context_refuses_before_rotating(string reason) {
        using var rig = new Rig();
        rig.Store.PutFilterFocusOffsets(rig.Calibration());
        switch (reason) {
            case "equipment": rig.FocuserId = "other"; break;
            case "optics": rig.Store.PutOpticsSettings(rig.Store.GetOpticsSettings() with { FocalLengthMm = 900 }); break;
            case "range": rig.MaxPosition = 10_100; break;
            case "tempcomp": rig.TempComp = true; break;
            case "slots": rig.Slots[1] = new(1, "SII", 0); break;
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None));
        Assert.That(rig.WheelMoves, Is.Empty);
    }

    [Test]
    public async Task Failed_sweep_restores_start_and_retains_previous_offsets() {
        using var rig = new Rig { FailStarsOnSlot = 1, Position = 10_050 };
        var previous = rig.Calibration();
        rig.Store.PutFilterFocusOffsets(previous);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Calibrate());
        using (Assert.EnterMultipleScope()) {
            Assert.That(rig.Store.GetFilterFocusOffsets(), Is.SameAs(previous));
            Assert.That(rig.Position, Is.EqualTo(10_050));
            Assert.That(rig.Slot, Is.Zero);
            Assert.That(rig.Offsets.IsCalibrating, Is.False);
            Assert.That(rig.Runs.MaintenanceActive, Is.False);
        }
    }

    [Test]
    public async Task Cancel_restores_even_with_cancelled_token_and_releases_all_gates() {
        using var rig = new Rig { Position = 10_050 };
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Calibrate(_ => cancellation.Cancel(), cancellation.Token));
        Assert.That(rig.Position, Is.EqualTo(10_050));
        Assert.That(rig.Store.GetFilterFocusOffsets(), Is.Null);
        Assert.That(rig.Runs.MaintenanceActive, Is.False);
    }

    [Test]
    public void Maintenance_and_live_sequence_exclude_each_other() {
        var runs = new ActiveRunSessionRegistry();
        var id = Guid.NewGuid();
        using (runs.BeginMaintenance()) Assert.Throws<InvalidOperationException>(() => runs.Enter(id));
        runs.Enter(id);
        Assert.Throws<InvalidOperationException>(() => runs.BeginMaintenance());
        runs.Exit(id);
        using var reservation = runs.BeginMaintenance();
        Assert.That(runs.MaintenanceActive, Is.True);
    }

    [Test]
    public async Task Clear_removes_compensation() {
        using var rig = new Rig();
        rig.Store.PutFilterFocusOffsets(rig.Calibration());
        await rig.Offsets.ClearAsync(CancellationToken.None);
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(rig.Position, Is.EqualTo(10_000));
    }

    [Test]
    [Category("IO")]
    public void Measurements_survive_restart_profile_selection_and_are_stripped_from_shares() {
        using var rig = new Rig();
        var directory = Path.Combine(Path.GetTempPath(), "ara-offsets-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try {
            var store = new FileProfileStore(directory);
            store.PutFilterFocusOffsets(rig.Calibration());
            var restarted = new FileProfileStore(directory);
            Assert.That(restarted.GetFilterFocusOffsets()!.Filters[1].OffsetSteps, Is.EqualTo(200));
            ProfileStoreSnapshot.Apply(rig.Store, ProfileStoreSnapshot.Capture(restarted));
            Assert.That(rig.Store.GetFilterFocusOffsets()!.Filters[2].OffsetSteps, Is.EqualTo(-100));
            Assert.That(ProfileShareService.StripForShare(ProfileStoreSnapshot.Capture(restarted)).FilterFocusOffsets, Is.Null);
            ProfileStoreSnapshot.Apply(rig.Store, ProfileSnapshotNormalizer.Defaults);
            Assert.That(rig.Store.GetFilterFocusOffsets(), Is.Null);
        } finally { Directory.Delete(directory, recursive: true); }
    }
    [Test]
    [Category("IO")]
    public async Task Router_rest_jobs_and_sequence_mediator_both_wait_for_filter_then_compensate() {
        using var rig = new Rig();
        rig.Store.PutFilterWheelLabels(new FilterWheelLabelsDto(["L", "Ha"]));
        using var alpaca = new FilterWheelService();
        using var manual = new ManualFilterWheelService(profileStore: rig.Store);
        var jobs = new InMemoryBatchJobService(null);
        using var router = new FilterWheelRouter(alpaca, manual, rig.Offsets, jobs);
        await router.ConnectAsync(new ConnectRequestDto(ManualFilterWheelService.Descriptor), null, CancellationToken.None);
        await manual.ReportInstalledAsync(0, CancellationToken.None);
        rig.Store.PutFilterFocusOffsets(rig.Calibration() with {
            WheelId = manual.GetInfo().DeviceId, Filters = [new(0, "L", 0), new(1, "Ha", 200)]
        });
        Assert.That((await router.GetAsync(CancellationToken.None))!.Slots[1].FocusOffset, Is.EqualTo(200));
        var operation = await router.ChangeFilterAsync(new(1), null, CancellationToken.None);
        for (var i = 0; i < 100 && (await manual.GetAsync(CancellationToken.None))!.Runtime.PendingSlot != 1; i++)
            await Task.Delay(10);
        Assert.That((await manual.GetAsync(CancellationToken.None))!.Runtime.PendingSlot, Is.EqualTo(1));
        Assert.That(rig.Position, Is.EqualTo(10_000), "never move focus before the filter is confirmed");
        await manual.ReportInstalledAsync(1, CancellationToken.None);
        for (var i = 0; i < 100 && jobs.GetJob(operation.OperationId)!.State is "queued" or "running"; i++)
            await Task.Delay(10);
        Assert.That(jobs.GetJob(operation.OperationId)!.State, Is.EqualTo("complete"));
        Assert.That(rig.Position, Is.EqualTo(10_200));
        var sequenceChange = router.ChangeFilter(new FilterInfo("L", 0, 0));
        for (var i = 0; i < 100 && (await manual.GetAsync(CancellationToken.None))!.Runtime.PendingSlot != 0; i++)
            await Task.Delay(10);
        await manual.ReportInstalledAsync(0, CancellationToken.None);
        await sequenceChange.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(rig.Position, Is.EqualTo(10_000));
    }

    [Test]
    public async Task Wheel_changes_wait_for_the_shared_autofocus_transaction() {
        using var rig = new Rig();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = rig.Offsets.ExclusiveAsync(async () => { entered.SetResult(); await release.Task; return true; }, CancellationToken.None);
        await entered.Task;
        var change = rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(change.IsCompleted, Is.False);
        Assert.That(rig.WheelMoves, Is.Empty);
        release.SetResult();
        await held;
        await change;
        Assert.That(rig.Slot, Is.EqualTo(1));
    }

    [Test]
    public async Task Retry_after_wheel_rotated_but_confirmation_failed_still_compensates() {
        using var rig = new Rig { FailAfterWheelRotation = true };
        rig.Store.PutFilterFocusOffsets(rig.Calibration());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None));
        Assert.That(rig.Slot, Is.EqualTo(1));
        Assert.That(rig.Position, Is.EqualTo(10_000));
        await rig.Offsets.ChangeAsync(rig.Wheel.Object, rig.Filter(1), CancellationToken.None);
        Assert.That(rig.Position, Is.EqualTo(10_200));
    }

    [Test]
    public void Sequence_reservation_blocks_calibration_without_a_catalog_session() {
        var runs = new ActiveRunSessionRegistry();
        using (runs.BeginSequence()) {
            Assert.That(runs.HasAny, Is.True);
            Assert.That(runs.Current, Is.Null);
            Assert.Throws<InvalidOperationException>(() => runs.BeginMaintenance());
        }
        using (runs.BeginMaintenance()) Assert.Throws<InvalidOperationException>(() => runs.BeginSequence());
        Assert.That(runs.HasAny, Is.False);
    }

    [Test]
    [Category("IO")]
    public async Task Http_calibration_job_publishes_complete_measurements_and_clear() {
        using var rig = new Rig();
        var jobs = new InMemoryBatchJobService(null);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        builder.Services.AddSingleton<IProfileStore>(rig.Store);
        builder.Services.AddSingleton(rig.Offsets);
        builder.Services.AddSingleton(rig.Runs);
        builder.Services.AddSingleton<IAutofocusExecutor>(rig.Sweep);
        builder.Services.AddSingleton(rig.WheelService.Object);
        builder.Services.AddSingleton(rig.Focus.Object);
        builder.Services.AddSingleton<IBatchJobService>(jobs);
        builder.Services.AddSingleton(new Mock<IProfileRepository>().Object);
        builder.Services.AddSingleton(new Mock<IBahtinovFocusService>().Object);
        var camera = new Mock<ICameraService>();
        camera.Setup(c => c.GetLiveViewStatus()).Returns(new LiveViewStatusDto(false, 0, 0, null, null, null, null, null));
        camera.Setup(c => c.IsFreeToCapture(It.IsAny<object>())).Returns(true);
        builder.Services.AddSingleton(camera.Object);
        await using var app = builder.Build();
        app.MapFilterFocusOffsetEndpoints();
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        using (var invalid = await client.PostAsJsonAsync("/api/v1/autofocus/filter-offsets/calibrate", new { reference_position = 99 }))
            Assert.That(invalid.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        using (rig.Runs.BeginSequence()) {
            using var busy = await client.PostAsJsonAsync("/api/v1/autofocus/filter-offsets/calibrate", new { reference_position = 0 });
            Assert.That(busy.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        }
        using var accepted = await client.PostAsJsonAsync("/api/v1/autofocus/filter-offsets/calibrate", new { reference_position = 0 });
        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var body = await accepted.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("job_id").GetGuid();
        for (var i = 0; i < 100 && jobs.GetJob(id)!.State is "queued" or "running"; i++) await Task.Delay(10);
        Assert.That(jobs.GetJob(id)!.State, Is.EqualTo("complete"), jobs.GetJob(id)!.ErrorMessage);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/v1/autofocus/filter-offsets/");
        Assert.That(state.GetProperty("calibration").GetProperty("filters")[1].GetProperty("offset_steps").GetInt32(), Is.EqualTo(200));
        using var cleared = await client.DeleteAsync(new Uri("/api/v1/autofocus/filter-offsets/", UriKind.Relative));
        Assert.That(cleared.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        var empty = await client.GetFromJsonAsync<JsonElement>("/api/v1/autofocus/filter-offsets/");
        Assert.That(empty.GetProperty("calibration").ValueKind, Is.EqualTo(JsonValueKind.Null));
        await app.StopAsync();
    }

}
