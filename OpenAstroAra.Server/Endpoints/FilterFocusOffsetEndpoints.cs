#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using OpenAstroAra.Sequencer.SequenceItem.Autofocus;
using OpenAstroAra.Server.Contracts;
using OpenAstroAra.Server.Services;

namespace OpenAstroAra.Server.Endpoints;

public static class FilterFocusOffsetEndpoints {
    internal static async ValueTask<object?> GuardMaintenance(EndpointFilterInvocationContext context, EndpointFilterDelegate next) {
        var request = context.HttpContext.Request;
        var path = request.Path.Value ?? string.Empty;
        var stop = path.EndsWith("/abort", StringComparison.Ordinal) || path.EndsWith("/halt", StringComparison.Ordinal)
            || path.EndsWith("/cancel", StringComparison.Ordinal) || path.EndsWith("/stop", StringComparison.Ordinal) || path.EndsWith("/disconnect", StringComparison.Ordinal);
        if (!HttpMethods.IsGet(request.Method) && !stop
            && context.HttpContext.RequestServices.GetService<ActiveRunSessionRegistry>()?.MaintenanceActive == true)
            return Results.Problem(statusCode: 409, detail: "Filter-offset calibration owns the equipment. Cancel it before changing equipment or profiles.");
        return await next(context).ConfigureAwait(false);
    }

    public static IEndpointRouteBuilder MapFilterFocusOffsetEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/v1/autofocus/filter-offsets").WithTags("Autofocus");
        group.MapGet("/", (IProfileStore profiles, FilterFocusOffsetService offsets, IBatchJobService jobs) =>
            Results.Ok(new FilterFocusOffsetStateDto(profiles.GetFilterFocusOffsets(),
                offsets.CalibrationJobId is Guid id ? jobs.GetJob(id) : null)))
            .Produces<FilterFocusOffsetStateDto>().WithName("GetFilterFocusOffsets");

        group.MapPost("/calibrate", async (FilterOffsetCalibrationRequestDto request,
            IAutofocusExecutor autofocus, FilterFocusOffsetService offsets, IFilterWheelService wheel,
            IFocuserService focuser, IProfileStore profiles, IProfileRepository repository,
            ActiveRunSessionRegistry runs, IBatchJobService jobs, IBahtinovFocusService bahtinov,
            ICameraService camera, CancellationToken ct) => {
            if (BahtinovFocusEndpoints.MaskOnConflict(bahtinov, "calibrating filter offsets") is { } conflict) return conflict;
            var slots = await wheel.GetAsync(ct);
            if (runs.HasAny || camera.GetLiveViewStatus().Active || !camera.IsFreeToCapture(offsets))
                return Results.Problem(statusCode: 409, detail: "Stop sequences and Live View before calibrating filter offsets.");
            if (slots is null || slots.Manual || slots.Slots.Count < 2 || !slots.Slots.Any(s => s.Position == request.ReferencePosition))
                return Results.Problem(statusCode: 400, detail: "Choose a reference slot on a motorized wheel with at least two filters.");
            if (autofocus is not AutofocusSweepService sweep)
                return Results.Problem(statusCode: 409, detail: "Live autofocus service unavailable.");
            try {
                var profileId = repository.ActiveId;
                var job = jobs.Enqueue("autofocus", slots.Slots.Count * 2 - 1, async (tick, token) => {
                    if (repository.ActiveId != profileId) throw new InvalidOperationException("Active profile changed before calibration started.");
                    await sweep.CalibrateFilterOffsetsAsync(request.ReferencePosition, offsets, wheel, focuser,
                        runs, repository, tick, token, camera).ConfigureAwait(false);
                }, identity: $"filter-offsets:{profileId}:{request.ReferencePosition}");
                offsets.CalibrationJobId = job.JobId;
                return Results.Accepted($"/api/v1/jobs/{job.JobId}", job);
            } catch (BatchJobConflictException ex) {
                return Results.Problem(statusCode: 409, detail: ex.Message);
            }
        }).Produces<BatchJobDto>(202).ProducesProblem(400).ProducesProblem(409)
            .WithName("CalibrateFilterFocusOffsets");

        group.MapDelete("/", async (FilterFocusOffsetService offsets, IBatchJobService jobs, CancellationToken ct) => {
            if (offsets.CalibrationJobId is Guid id && jobs.GetJob(id)?.State is "queued" or "running")
                return Results.Problem(statusCode: 409, detail: "Cancel filter-offset calibration before clearing offsets.");
            await offsets.ClearAsync(ct);
            return Results.NoContent();
        }).Produces(204).ProducesProblem(409).WithName("ClearFilterFocusOffsets");
        return app;
    }
}
