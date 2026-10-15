#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

public sealed partial class AutofocusSweepService {
    /// <summary>Reference → filter → reference for every slot. Only an entirely successful run commits.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Calibration transaction boundary: recover physical wheel/focus state on any device fault before propagating it to the job.")]
    public Task<FilterFocusOffsetsDto> CalibrateFilterOffsetsAsync(int referencePosition,
        FilterFocusOffsetService offsets, IFilterWheelService wheelService, IFocuserService focuserService,
        ActiveRunSessionRegistry runs, IProfileRepository? repository, Action<int> tick, CancellationToken ct, ICameraService? camera = null) =>
        offsets.CalibrateAsync(async () => {
            using var reservation = runs.BeginMaintenance();
            await _sweepGate.WaitAsync(ct).ConfigureAwait(false);
            try {
                var wheel = await wheelService.GetAsync(ct).ConfigureAwait(false);
                var focus = await focuserService.GetAsync(ct).ConfigureAwait(false);
                if (wheel is not { State: EquipmentConnectionState.Connected, Runtime.CurrentSlot: not null }
                    || wheel.Runtime.State != "idle" || wheel.Manual || wheel.Slots.Count < 2
                    || !wheel.Slots.Any(s => s.Position == referencePosition)
                    || focus is not { State: EquipmentConnectionState.Connected, Capabilities.AbsoluteFocuser: true, Runtime.Position: not null }
                    || focus.Runtime.TempCompEnabled || focus.Runtime.State == "moving" || _filterWheel is null
                    || camera?.GetLiveViewStatus().Active == true || camera?.IsFreeToCapture(this) == false)
                    throw new InvalidOperationException("Calibration requires a settled motorized wheel with at least two filters, a valid reference slot, and an absolute focuser with temperature compensation off.");

                var startFilter = _filterWheel.GetInfo().SelectedFilter
                    ?? throw new InvalidOperationException("Current filter unknown.");
                var startPosition = focus.Runtime.Position.Value;
                var savedCalibration = _profiles.GetFocusCalibration();
                var settings = _profiles.GetAutofocusSettings();
                var optics = _profiles.GetOpticsSettings();
                var profileId = repository?.ActiveId;
                var done = 0;
                var progress = new Progress<ApplicationStatus>();

                void CheckContext() {
                    ct.ThrowIfCancellationRequested();
                    if (repository?.ActiveId != profileId || _profiles.GetAutofocusSettings() != settings
                        || _profiles.GetOpticsSettings() != optics || _filterWheel.GetInfo().DeviceId != wheel.DeviceId
                        || _focuser.GetInfo().DeviceId != focus.DeviceId)
                        throw new InvalidOperationException("Profile, settings or equipment changed during filter-offset calibration; offsets were not saved.");
                }

                async Task<FilterInfo> Change(FilterInfo filter, CancellationToken token) =>
                    _filterWheel is FilterWheelRouter router
                        ? await router.ChangeUncompensatedAsync(filter, token).ConfigureAwait(false)
                        : await _filterWheel.ChangeFilter(filter, token: token).ConfigureAwait(false);

                async Task<(int Position, long Time)> Measure(FilterSlotDto slot) {
                    CheckContext();
                    var filter = new FilterInfo(slot.Name, 0, checked((short)slot.Position));
                    var reached = await Change(filter, ct).ConfigureAwait(false);
                    var confirmed = _filterWheel.GetInfo();
                    if (reached.Position != slot.Position || !confirmed.Connected || confirmed.IsMoving || confirmed.SelectedFilter?.Position != slot.Position)
                        throw new InvalidOperationException("Calibration filter change was not confirmed.");
                    // Never use another filter's Smart Focus map to measure a physical offset.
                    if (!await RunGatedAsync(progress, ct, forceClassic: true).ConfigureAwait(false)) {
                        if (_tracker?.State == "cancelled") throw new OperationCanceledException("Filter-offset calibration cancelled.");
                        throw new InvalidOperationException($"Autofocus failed on {slot.Name}; previous offsets retained.");
                    }
                    CheckContext();
                    var info = _focuser.GetInfo();
                    if (!info.Connected || info.IsMoving) throw new InvalidOperationException("Focuser did not settle.");
                    tick(++done);
                    return (info.Position, Stopwatch.GetTimestamp());
                }

                try {
                    var referenceSlot = wheel.Slots.Single(s => s.Position == referencePosition);
                    var before = await Measure(referenceSlot).ConfigureAwait(false);
                    var measured = new List<FilterFocusOffsetDto> { new(referencePosition, referenceSlot.Name, 0) };
                    foreach (var slot in wheel.Slots.Where(s => s.Position != referencePosition)) {
                        var target = await Measure(slot).ConfigureAwait(false);
                        var after = await Measure(referenceSlot).ConfigureAwait(false);
                        var fraction = (double)(target.Time - before.Time) / (after.Time - before.Time);
                        measured.Add(new(slot.Position, slot.Name, CalculateFilterOffset(before.Position, target.Position, after.Position, fraction)));
                        before = after;
                    }
                    CheckContext();
                    var liveWheel = await wheelService.GetAsync(ct).ConfigureAwait(false);
                    if (liveWheel is null || !liveWheel.Slots.Select(s => (s.Position, s.Name))
                        .SequenceEqual(wheel.Slots.Select(s => (s.Position, s.Name))))
                        throw new InvalidOperationException("Filter slots changed during calibration.");
                    var result = new FilterFocusOffsetsDto(wheel.DeviceId, focus.DeviceId, optics,
                        referencePosition, DateTimeOffset.UtcNow, measured.ToArray());
                    _profiles.PutFilterFocusOffsets(result);
                    // Ends focused on reference. The next ordinary switch applies the measured delta.
                    return result;
                } catch (Exception error) {
                    var recoveryErrors = new List<Exception>();
                    using var recovery = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    // Both recovery attempts run even when one fails. Never command replacement equipment.
                    try {
                        if (_filterWheel.GetInfo().DeviceId != wheel.DeviceId) throw new InvalidOperationException("Original wheel unavailable for recovery.");
                        var restored = await Change(startFilter, recovery.Token).ConfigureAwait(false);
                        var current = _filterWheel.GetInfo();
                        if (restored.Position != startFilter.Position || !current.Connected || current.IsMoving || current.SelectedFilter?.Position != startFilter.Position)
                            throw new InvalidOperationException("Original filter was not restored.");
                    } catch (Exception ex) { recoveryErrors.Add(ex); }
                    try {
                        if (_focuser.GetInfo().DeviceId != focus.DeviceId) throw new InvalidOperationException("Original focuser unavailable for recovery.");
                        if (await _focuser.MoveFocuser(startPosition, recovery.Token).ConfigureAwait(false) != startPosition)
                            throw new InvalidOperationException("Original focus position was not restored.");
                    } catch (Exception ex) { recoveryErrors.Add(ex); }
                    // A profile switch must never write the old profile's calibration into the new one.
                    if (repository?.ActiveId == profileId) _profiles.PutFocusCalibration(savedCalibration);
                    if (recoveryErrors.Count > 0)
                        throw new InvalidOperationException("Filter-offset calibration stopped; recovery incomplete: "
                            + string.Join("; ", recoveryErrors.Select(e => e.Message)), new AggregateException([error, .. recoveryErrors]));
                    throw;
                }
            } finally { _sweepGate.Release(); }
        }, ct);

    /// <summary>Remove linear focus drift between the two reference measurements.</summary>
    public static int CalculateFilterOffset(int before, int target, int after, double fraction) {
        if (!double.IsFinite(fraction) || fraction is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(fraction));
        return checked((int)Math.Round(target - (before + ((double)after - before) * fraction)));
    }
}
