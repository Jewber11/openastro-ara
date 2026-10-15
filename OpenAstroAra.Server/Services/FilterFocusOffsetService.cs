#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Core.Model.Equipment;
using OpenAstroAra.Equipment.Interfaces.Mediator;
using OpenAstroAra.Server.Contracts;

namespace OpenAstroAra.Server.Services;

/// <summary>One transaction at a time across autofocus, calibration and wheel/focus changes.</summary>
public sealed class FilterFocusOffsetService : IDisposable {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IProfileStore _profiles;
    private readonly IFocuserService _focus;
    private readonly IFocuserMediator _focuser;
    // A wheel may already have rotated when a focus move fails. Retain the absolute destination
    // so retrying that same filter completes compensation exactly once.
    private (string Wheel, string Focuser, int Slot, int Destination, FilterFocusOffsetsDto? Calibration)? _pending;
    private int _calibrating;

    public FilterFocusOffsetService(IProfileStore profiles, IFocuserService focus, IFocuserMediator focuser) {
        _profiles = profiles;
        _focus = focus;
        _focuser = focuser;
    }

    public bool HasPendingCompensation => _pending is not null;
    public bool IsCalibrating => Volatile.Read(ref _calibrating) != 0;
    private readonly object _jobGate = new();
    private Guid? _calibrationJobId;
    public Guid? CalibrationJobId {
        get { lock (_jobGate) { return _calibrationJobId; } }
        set { lock (_jobGate) { _calibrationJobId = value; } }
    }

    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> work, CancellationToken ct) {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await work().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<T> CalibrateAsync<T>(Func<Task<T>> work, CancellationToken ct) =>
        await ExclusiveAsync(async () => {
            Interlocked.Exchange(ref _calibrating, 1);
            try {
                var result = await work().ConfigureAwait(false);
                _pending = null;
                return result;
            } finally { Interlocked.Exchange(ref _calibrating, 0); }
        }, ct).ConfigureAwait(false);

    public Task<bool> ClearAsync(CancellationToken ct) => ExclusiveAsync(() => {
        _profiles.PutFilterFocusOffsets(null);
        _pending = null;
        CalibrationJobId = null;
        return Task.FromResult(true);
    }, ct);

    public Task<FilterInfo> ChangeAsync(IFilterWheelMediator wheel, FilterInfo target,
        CancellationToken ct, IReadOnlyList<FilterSlotDto>? slots = null, string? expectedWheelId = null) => ExclusiveAsync(async () => {
        var info = wheel.GetInfo();
        if (expectedWheelId is not null && info.DeviceId != expectedWheelId)
            throw new InvalidOperationException("Filter wheel changed before the filter-change transaction started.");
        if (!info.Connected || info.IsMoving)
            throw new InvalidOperationException("Wait for a connected, settled filter wheel.");
        var calibration = _profiles.GetFilterFocusOffsets();
        int? destination = null;
        FocuserDto? focus = null;
        if (_pending is { } pending) {
            focus = await _focus.GetAsync(ct).ConfigureAwait(false);
            if (pending.Wheel != info.DeviceId || pending.Slot != target.Position || pending.Focuser != focus?.DeviceId || pending.Calibration != calibration)
                throw new InvalidOperationException("Focus compensation incomplete. Retry the same filter or clear/recalibrate offsets.");
            destination = pending.Destination;
        } else if (calibration is not null) {
            focus = await _focus.GetAsync(ct).ConfigureAwait(false);
            if (calibration.WheelId != info.DeviceId || calibration.FocuserId != focus?.DeviceId
                    || calibration.Optics != _profiles.GetOpticsSettings())
                throw new InvalidOperationException("Filter offsets do not match this equipment. Clear or recalibrate offsets.");
            if (info.SelectedFilter is null) throw new InvalidOperationException("Current filter unknown. Clear or recalibrate offsets.");
            var source = info.SelectedFilter;
            if (slots is not null) {
                if (calibration.Filters.Count != slots.Count || calibration.Filters.Any(f => !slots.Any(s => s.Position == f.Position && s.Name == f.Name)))
                    throw new InvalidOperationException("Filter slots changed. Clear or recalibrate offsets.");
                var currentSlot = slots.Single(s => s.Position == source.Position);
                source = new FilterInfo(currentSlot.Name, 0, source.Position);
            }
            var delta = Delta(calibration, source, target);
            if (delta != 0) {
                if (focus.Runtime.Position is not int current)
                    throw new InvalidOperationException("Focuser position unknown.");
                destination = checked(current + delta);
            }
        }
        if (destination is int position && (focus is not {
                State: EquipmentConnectionState.Connected, Capabilities.AbsoluteFocuser: true
            } || focus.Runtime.TempCompEnabled || focus.Runtime.State == "moving" || position < focus.Capabilities.MinPosition || position > focus.Capabilities.MaxPosition))
            throw new InvalidOperationException("Filter focus target outside travel, focuser disconnected, or temperature compensation enabled.");

        int? approach = null;
        if (destination is int landing && focus!.Runtime.Position <= landing) {
            var span = Math.Max(1, _profiles.GetAutofocusSettings().StepSize);
            approach = checked(landing + span);
            if (approach > focus.Capabilities!.MaxPosition)
                throw new InvalidOperationException("Insufficient focuser travel to approach the filter focus target from above.");
        }
        // Reserve before rotation: cancellation/jam can happen AFTER the hardware reached the slot.
        // A retry must still finish focus compensation rather than calculate a zero delta.
        if (destination is int reserved) _pending = (info.DeviceId, focus!.DeviceId, target.Position, reserved, calibration);
        var reached = await wheel.ChangeFilter(target, token: ct).ConfigureAwait(false);
        var confirmed = wheel.GetInfo();
        if (reached.Position != target.Position || !confirmed.Connected || confirmed.IsMoving || confirmed.SelectedFilter?.Position != target.Position)
            throw new InvalidOperationException("Filter change was not confirmed.");
        if (destination is int goal) {
            // Match the calibration sweep's final approach to avoid reversing backlash at focus.
            if (approach is int above && await _focuser.MoveFocuser(above, ct).ConfigureAwait(false) != above)
                throw new InvalidOperationException("Filter focus approach did not reach its target.");
            if (await _focuser.MoveFocuser(goal, ct).ConfigureAwait(false) != goal)
                throw new InvalidOperationException("Filter focus offset move did not reach its target.");
            _pending = null;
        }
        return reached;
    }, ct);

    public FilterWheelDto WithMeasuredOffsets(FilterWheelDto wheel) {
        var calibration = _profiles.GetFilterFocusOffsets();
        if (calibration is null || calibration.WheelId != wheel.DeviceId || calibration.FocuserId != _focuser.GetInfo().DeviceId
            || calibration.Optics != _profiles.GetOpticsSettings() || calibration.Filters.Count != wheel.Slots.Count
            || calibration.Filters.Any(f => !wheel.Slots.Any(s => s.Position == f.Position && s.Name == f.Name))) return wheel;
        return wheel with { Slots = wheel.Slots.Select(s => s with {
            FocusOffset = calibration.Filters.Single(f => f.Position == s.Position).OffsetSteps
        }).ToArray() };
    }

    public static int Delta(FilterFocusOffsetsDto calibration, FilterInfo from, FilterInfo to) {
        var source = calibration.Filters.SingleOrDefault(f => f.Position == from.Position && f.Name == from.Name);
        var target = calibration.Filters.SingleOrDefault(f => f.Position == to.Position && f.Name == to.Name);
        if (source is null || target is null)
            throw new InvalidOperationException("Filter slots changed or are uncalibrated. Clear or recalibrate offsets.");
        return checked(target.OffsetSteps - source.OffsetSteps);
    }

    public void Dispose() => _gate.Dispose();
}
