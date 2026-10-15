#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// The device-event members satisfy the equipment mediator interface but are never raised server-side.
#pragma warning disable CS0067

using OpenAstroAra.Core.Model;
using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Equipment.MyFilterWheel;
using OpenAstroAra.Equipment.Interfaces;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace OpenAstroAra.Server.Services;

/// <summary>
/// #1298 — the one filter wheel the daemon serves, in front of two implementations: the Alpaca
/// <see cref="FilterWheelService"/> and the driverless <see cref="ManualFilterWheelService"/>. It is
/// what DI hands out as <see cref="IFilterWheelService"/> and <see cref="IFilterWheelMediator"/>, so
/// the endpoints, the reconnector and <c>SwitchFilter</c> need no knowledge of which is selected.
///
/// <para>The selected one is whichever was connected last. Connecting the other kind first
/// disconnects and forgets the current one, so the card never shows two wheels and the sequencer
/// never resolves filters against a wheel the user moved away from.</para>
/// </summary>
public sealed class FilterWheelRouter : IFilterWheelService, IFilterWheelMediator, IRetainedDeviceSource, IDisposable {

    private readonly FilterWheelService _alpaca;
    private readonly ManualFilterWheelService _manual;
    private readonly FilterFocusOffsetService? _offsets;
    private readonly IBatchJobService? _jobs;
    private BatchJobDto? _changeJob;
    private readonly SemaphoreSlim _switch = new(1, 1);
    // In memory only: false (Alpaca) on every boot. The boot reconnect restores the manual wheel by
    // connecting the remembered selection through ConnectAsync, which sets it; with nothing
    // remembered, RetainedDevice is the Alpaca wheel's.
    private volatile bool _manualSelected;

    public FilterWheelRouter(FilterWheelService alpaca, ManualFilterWheelService manual,
        FilterFocusOffsetService? offsets = null, IBatchJobService? jobs = null) {
        _alpaca = alpaca ?? throw new ArgumentNullException(nameof(alpaca));
        _manual = manual ?? throw new ArgumentNullException(nameof(manual));
        _offsets = offsets;
        _jobs = jobs;
    }

    /// <summary>The manual wheel, for its own endpoints (installed / cancel).</summary>
    public ManualFilterWheelService Manual => _manual;

    /// <summary>Whether the manual wheel is the selected one.</summary>
    public bool ManualSelected => _manualSelected;

    private IFilterWheelService Rest => _manualSelected ? _manual : _alpaca;
    private IFilterWheelMediator Mediator => _manualSelected ? _manual : _alpaca;

    public DiscoveredDeviceDto? RetainedDevice =>
        _manualSelected ? _manual.RetainedDevice : _alpaca.RetainedDevice;

    public async Task<FilterWheelDto?> GetAsync(CancellationToken ct) {
        var status = await Rest.GetAsync(ct).ConfigureAwait(false);
        if (status is not null && _offsets is not null) status = _offsets.WithMeasuredOffsets(status);
        if (status is null || _changeJob is null || _jobs?.GetJob(_changeJob.JobId) is not { } job) return status;
        if (job.State is "queued" or "running" && status.Runtime.State != "awaiting_user")
            return status with { Runtime = status.Runtime with { State = "moving" } };
        if (job.State == "failed")
            return status with { Runtime = status.Runtime with { ErrorMessage = job.ErrorMessage } };
        return status;
    }

    public Task<OperationAcceptedDto> ConnectAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) =>
        _offsets is null ? ConnectCoreAsync(request, idempotencyKey, ct)
            : _offsets.ExclusiveAsync(() => ConnectCoreAsync(request, idempotencyKey, ct), ct);

    private async Task<OperationAcceptedDto> ConnectCoreAsync(ConnectRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        var wantManual = ManualFilterWheelService.IsManual(request.Device);
        await _switch.WaitAsync(ct).ConfigureAwait(false);
        try {
            if (wantManual != _manualSelected) {
                // Leave the other kind cleanly: disconnect, then drop its retained device so a GET
                // and the reconnector only ever see the selected wheel.
                IFilterWheelService previous = _manualSelected ? _manual : _alpaca;
                await previous.DisconnectAsync(null, ct).ConfigureAwait(false);
                try {
                    await previous.ForgetAsync(ct).ConfigureAwait(false);
                } catch (InvalidOperationException) {
                    // A connect raced back in on the old kind; the switch still proceeds.
                }
                _manualSelected = wantManual;
            }
            return await Rest.ConnectAsync(request, idempotencyKey, ct).ConfigureAwait(false);
        } finally {
            _switch.Release();
        }
    }

    public Task<OperationAcceptedDto> DisconnectAsync(string? idempotencyKey, CancellationToken ct) => Rest.DisconnectAsync(idempotencyKey, ct);

    public Task<bool> ForgetAsync(CancellationToken ct) => Rest.ForgetAsync(ct);

    public async Task<OperationAcceptedDto> ChangeFilterAsync(FilterChangeRequestDto request, string? idempotencyKey, CancellationToken ct) {
        ArgumentNullException.ThrowIfNull(request);
        if (_offsets is null || _jobs is null) return await Rest.ChangeFilterAsync(request, idempotencyKey, ct).ConfigureAwait(false);
        if (_offsets.IsCalibrating) throw new InvalidOperationException("Filter-offset calibration owns the wheel.");
        var status = await Rest.GetAsync(ct).ConfigureAwait(false);
        if (status is not { State: EquipmentConnectionState.Connected }) throw new InvalidOperationException("Filter wheel is not connected.");
        var slot = System.Linq.Enumerable.SingleOrDefault(status.Slots, s => s.Position == request.Position)
            ?? throw new ArgumentOutOfRangeException(nameof(request), "Unknown filter slot.");
        var job = _jobs.Enqueue("filter-wheel.change", 1, async (tick, token) => {
            await ChangeBoundAsync(new FilterInfo(slot.Name, 0, checked((short)slot.Position)), null, status.DeviceId, token).ConfigureAwait(false);
            tick(1);
        }, identity: request.Position.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _changeJob = job;
        return new OperationAcceptedDto(job.JobId, "filter-wheel.change", DateTimeOffset.UtcNow, idempotencyKey);
    }

    // ─── IFilterWheelMediator ───

    public FilterWheelInfo GetInfo() => Mediator.GetInfo();

    public Task<FilterInfo> ChangeFilter(FilterInfo inputFilter, IProgress<ApplicationStatus>? progress = null, CancellationToken token = default) =>
        ChangeBoundAsync(inputFilter, progress, null, token);

    private async Task<FilterInfo> ChangeBoundAsync(FilterInfo inputFilter, IProgress<ApplicationStatus>? progress, string? expectedWheelId, CancellationToken token) {
        if (_offsets is null) return await Mediator.ChangeFilter(inputFilter, progress, token).ConfigureAwait(false);
        var status = await Rest.GetAsync(token).ConfigureAwait(false);
        if (expectedWheelId is not null && status?.DeviceId != expectedWheelId)
            throw new InvalidOperationException("Filter wheel changed before the queued change started.");
        var slot = status is null ? null : System.Linq.Enumerable.SingleOrDefault(status.Slots, s => s.Position == inputFilter.Position);
        var target = slot is null ? inputFilter : new FilterInfo(slot.Name, 0, inputFilter.Position);
        var result = await _offsets.ChangeAsync(Mediator, target, token, status?.Slots, expectedWheelId ?? status?.DeviceId).ConfigureAwait(false);
        _changeJob = null;
        return result;
    }

    // Used only inside the offset service's exclusive calibration transaction.
    internal Task<FilterInfo> ChangeUncompensatedAsync(FilterInfo filter, CancellationToken ct) =>
        Mediator.ChangeFilter(filter, token: ct);

    public Task<bool> Connect() => Mediator.Connect();
    public Task Disconnect() => Mediator.Disconnect();
    public Task<IList<string>> Rescan() => Mediator.Rescan();
    public void RegisterHandler(object handler) => Mediator.RegisterHandler(handler);
    public void RegisterConsumer(IFilterWheelConsumer consumer) => Mediator.RegisterConsumer(consumer);
    public void RemoveConsumer(IFilterWheelConsumer consumer) => Mediator.RemoveConsumer(consumer);
    public void Broadcast(FilterWheelInfo deviceInfo) => Mediator.Broadcast(deviceInfo);
    public string Action(string actionName, string actionParameters) => Mediator.Action(actionName, actionParameters);
    public string SendCommandString(string command, bool raw = true) => Mediator.SendCommandString(command, raw);
    public bool SendCommandBool(string command, bool raw = true) => Mediator.SendCommandBool(command, raw);
    public void SendCommandBlind(string command, bool raw = true) => Mediator.SendCommandBlind(command, raw);
    public IDevice GetDevice() => Mediator.GetDevice();

    // The two wheels are DI singletons the container disposes; only the switch lock is ours.
    public void Dispose() => _switch.Dispose();

    public event Func<object, EventArgs, Task>? Connected;
    public event Func<object, EventArgs, Task>? Disconnected;
    public event Func<object, FilterChangedEventArgs, Task>? FilterChanged;
}
