import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/state/settings/camera_electronics_state.dart';
import 'package:openastroara/state/settings/filter_set_state.dart';
import 'package:openastroara/state/settings/optics_settings_state.dart';
import 'package:openastroara/state/settings/site_settings_state.dart';
import 'package:openastroara/util/optimal_sub_local.dart';

void main() {
  const optics = OpticsSettings(
    focalLengthMm: 447,
    reducerFactor: 1.0,
    sensorWidthPx: 6248,
    sensorHeightPx: 4176,
    pixelSizeUm: 3.76,
    apertureMm: 91,
  );
  const electronics = CameraElectronics(
    readNoiseE: 1.5,
    fullWellE: 50000,
    electronsPerAdu: 0.8,
    quantumEfficiencyPeak: 0.8,
  );
  const site = SiteSettings(bortleClass: 6);
  const filters = FilterSetSettings(
    filters: [
      PlanningFilter(name: 'L', kind: FilterKind.l),
      PlanningFilter(name: 'Ha', kind: FilterKind.ha),
    ],
  );

  LocalOptimalSubOutcome resolve(String name, FilterSetSettings filterSet) =>
      resolveOptimalSubLocal(
        optics: optics,
        electronics: electronics,
        site: site,
        filterSet: filterSet,
        filterName: name,
        skyCalibrationK: const {'ha': 0.5, 'l': 0.8},
      );

  for (final names in [
    ('Ha', 'Hα', FilterKind.ha, 4.0),
    ('Hα', 'Ha', FilterKind.ha, 4.0),
    ('Ha', 'H-alpha', FilterKind.ha, 4.0),
    ('Luminance', 'L', FilterKind.l, 0.0),
    ('L', 'Luminance', FilterKind.l, 80.0),
    ('Red', 'R', FilterKind.r, 80.0),
    ('Green', 'G', FilterKind.g, 80.0),
    ('Blue', 'B', FilterKind.b, 80.0),
  ]) {
    test(
      '${names.$2} resolves configured ${names.$1} passband and calibration',
      () {
        final set = FilterSetSettings(
          filters: [
            PlanningFilter(
              name: names.$1,
              kind: names.$3,
              bandwidthNm: names.$4,
            ),
          ],
        );
        final exact = (resolve(names.$1, set) as LocalOptimalSubSuccess).result;
        final alias = (resolve(
          ' ${names.$2.toUpperCase()} ',
          set,
        ) as LocalOptimalSubSuccess).result;
        expect(alias.skyFluxEPerSecPerPx, exact.skyFluxEPerSecPerPx);
        expect(alias.recommendedSec, exact.recommendedSec);
        expect(alias.assumedDefaults, isNull);
      },
    );
  }

  test('exact names win when multiple filters share an alias', () {
    const set = FilterSetSettings(
      filters: [
        PlanningFilter(name: 'Hα', kind: FilterKind.ha, bandwidthNm: 7),
        PlanningFilter(name: 'Ha', kind: FilterKind.ha, bandwidthNm: 4),
      ],
    );
    final result = (resolve(' ha ', set) as LocalOptimalSubSuccess).result;
    final expected = (resolve(
      'Ha',
      const FilterSetSettings(
        filters: [
          PlanningFilter(name: 'Ha', kind: FilterKind.ha, bandwidthNm: 4),
        ],
      ),
    ) as LocalOptimalSubSuccess).result;
    expect(result.skyFluxEPerSecPerPx, expected.skyFluxEPerSecPerPx);
    expect(resolve('H-alpha', set), isA<LocalOptimalSubUnavailable>());
  });

  test('unknown labels do not inherit a configured filter kind', () {
    expect(resolve('Chandra', filters), isA<LocalOptimalSubUnavailable>());
    expect(resolve('Ha 3nm', filters), isA<LocalOptimalSubUnavailable>());
    expect(
      resolve('Hα', const FilterSetSettings()),
      isA<LocalOptimalSubUnavailable>(),
    );
  });

  test(
    "#1140 P2 the matched filter's calibration k reaches the sky flux (dimmer sky = longer sub)",
    () {
      LocalOptimalSubOutcome run(Map<String, double> k) =>
          resolveOptimalSubLocal(
            optics: optics,
            electronics: electronics,
            site: site,
            filterSet: filters,
            filterName: 'L',
            skyCalibrationK: k,
          );
      final spec = (run(const {}) as LocalOptimalSubSuccess).result;
      final fitted = (run(const {'l': 0.5}) as LocalOptimalSubSuccess).result;
      final other = (run(const {'ha': 0.5}) as LocalOptimalSubSuccess).result;
      expect(
        fitted.skyFluxEPerSecPerPx,
        closeTo(spec.skyFluxEPerSecPerPx * 0.5, 1e-9),
      );
      expect(fitted.recommendedSec, greaterThan(spec.recommendedSec));
      expect(
        other.skyFluxEPerSecPerPx,
        closeTo(spec.skyFluxEPerSecPerPx, 1e-9),
        reason: "another kind's k is not applied to L",
      );
    },
  );
}
