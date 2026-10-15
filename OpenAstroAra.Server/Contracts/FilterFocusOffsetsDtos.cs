#region "copyright"

/*
    Copyright (c) 2026 Open Astro and the OpenAstro Ara contributors

    This file is part of OpenAstro Ara (forked from N.I.N.A.).

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

using System;
using System.Collections.Generic;

namespace OpenAstroAra.Server.Contracts;

/// <summary>Signed focuser steps relative to the chosen reference filter (zero).</summary>
public sealed record FilterFocusOffsetDto(int Position, string Name, int OffsetSteps);

/// <summary>Daemon-owned measurements. Equipment identities, slot names and optics prevent stale reuse.</summary>
public sealed record FilterFocusOffsetsDto(
    string WheelId, string FocuserId, OpticsSettingsDto Optics,
    int ReferencePosition, DateTimeOffset CalibratedUtc, IReadOnlyList<FilterFocusOffsetDto> Filters);

public sealed record FilterOffsetCalibrationRequestDto(int ReferencePosition);

public sealed record FilterFocusOffsetStateDto(FilterFocusOffsetsDto? Calibration, BatchJobDto? Job);
