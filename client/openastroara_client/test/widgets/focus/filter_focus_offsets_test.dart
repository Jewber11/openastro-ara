import 'dart:async';
import 'dart:convert';
import 'dart:typed_data';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:openastroara/models/filter_focus_offsets.dart';
import 'package:openastroara/models/filter_wheel_status.dart';
import 'package:openastroara/models/profile_list.dart';
import 'package:openastroara/models/server.dart';
import 'package:openastroara/services/autofocus_api.dart';
import 'package:openastroara/services/filter_focus_offsets_api.dart';
import 'package:openastroara/state/equipment/filter_wheel_state.dart';
import 'package:openastroara/state/settings/settings_search.dart';
import 'package:openastroara/state/focus/filter_focus_offsets_state.dart';
import 'package:openastroara/state/profile_management_state.dart';
import 'package:openastroara/widgets/focus/filter_focus_offsets_card.dart';

final _calibration = FilterFocusOffsets.fromJson({
  'reference_position': 0,
  'calibrated_utc': '2026-10-15T02:00:00Z',
  'filters': [
    {'position': 0, 'name': 'L', 'offset_steps': 0},
    {'position': 1, 'name': 'Ha', 'offset_steps': 200},
  ],
});
const _running = AutofocusJob(
  jobId: 'job',
  state: 'running',
  done: 1,
  total: 3,
);

class _Profiles extends ProfileManagementNotifier {
  @override
  Future<ProfileList> build() async =>
      const ProfileList(activeId: 'profile', profiles: []);
}

class _Wheel extends FilterWheelNotifier {
  @override
  Future<FilterWheelStatus?> build() async => FilterWheelStatus.fromJson({
    'device_id': 'wheel',
    'state': 'connected',
    'runtime': {'state': 'idle', 'current_slot': 0},
    'slots': [
      {'position': 0, 'name': 'L', 'focus_offset': 0},
      {'position': 1, 'name': 'Ha', 'focus_offset': 0},
    ],
  });
}

class _Api implements FilterFocusOffsetsApi {
  FilterFocusOffsetSnapshot snapshot = const FilterFocusOffsetSnapshot();
  Future<FilterFocusOffsetSnapshot>? pendingLoad;
  int? reference;
  String? cancelled;
  bool fail = false;
  @override
  Future<FilterFocusOffsetSnapshot> load() async {
    if (fail) throw StateError('offline');
    return pendingLoad == null ? snapshot : await pendingLoad!;
  }

  @override
  Future<AutofocusJob> calibrate(int referencePosition) async {
    reference = referencePosition;
    snapshot = FilterFocusOffsetSnapshot(
      calibration: snapshot.calibration,
      job: _running,
    );
    return _running;
  }

  @override
  Future<void> cancel(String jobId) async {
    cancelled = jobId;
    snapshot = FilterFocusOffsetSnapshot(
      calibration: snapshot.calibration,
      job: const AutofocusJob(jobId: 'job', state: 'cancelled'),
    );
  }

  @override
  Future<void> clear() async => snapshot = const FilterFocusOffsetSnapshot();
  @override
  void close() {}
}

class _Daemon implements HttpClientAdapter {
  final List<RequestOptions> requests = [];
  int status = 200;
  Object body = {'calibration': null, 'job': null};
  @override
  void close({bool force = false}) {}
  @override
  Future<ResponseBody> fetch(
    RequestOptions options,
    Stream<Uint8List>? requestStream,
    Future<void>? cancelFuture,
  ) async {
    requests.add(options);
    return ResponseBody.fromString(
      jsonEncode(body),
      status,
      headers: {
        Headers.contentTypeHeader: [Headers.jsonContentType],
      },
    );
  }
}

void main() {
  test(
    'filter offsets are searchable and navigate to the autofocus settings',
    () {
      final hits = searchSettings(buildSearchIndex(), 'filter offsets');
      final setting = hits.firstWhere(
        (h) => h.settingId == 'img.autofocus.filter_offsets',
      );
      expect(setting.panelId, isNotNull);
    },
  );

  test(
    'API uses calibration route, reference slot, job cancellation and clear',
    () async {
      final daemon = _Daemon();
      final api = DioFilterFocusOffsetsApi(
        const AraServer(hostname: 'x', port: 80),
        dio: Dio()..httpClientAdapter = daemon,
      );
      addTearDown(api.close);
      expect((await api.load()).calibration, isNull);
      daemon.status = 202;
      daemon.body = {'job_id': 'job', 'state': 'queued', 'done': 0, 'total': 3};
      expect((await api.calibrate(1)).jobId, 'job');
      expect(daemon.requests.last.data, {'reference_position': 1});
      daemon.status = 204;
      await api.cancel('job');
      await api.clear();
      expect(daemon.requests.map((r) => '${r.method} ${r.path}'), [
        'GET /api/v1/autofocus/filter-offsets/',
        'POST /api/v1/autofocus/filter-offsets/calibrate',
        'DELETE /api/v1/jobs/job',
        'DELETE /api/v1/autofocus/filter-offsets/',
      ]);
      daemon.status = 409;
      await expectLater(api.calibrate(0), throwsA(isA<DioException>()));
    },
  );

  test(
    'notifier resumes existing job, retains measurements on errors and cancels',
    () async {
      final api = _Api()
        ..snapshot = FilterFocusOffsetSnapshot(
          calibration: _calibration,
          job: _running,
        );
      final container = ProviderContainer(
        overrides: [
          filterFocusOffsetsApiProvider.overrideWithValue(api),
          profileManagementProvider.overrideWith(_Profiles.new),
        ],
      );
      addTearDown(container.dispose);
      container.read(filterFocusOffsetsProvider);
      await Future<void>.delayed(Duration.zero);
      final notifier = container.read(filterFocusOffsetsProvider.notifier);
      expect(
        container.read(filterFocusOffsetsProvider).snapshot.running,
        isTrue,
      );
      api.fail = true;
      await notifier.refresh();
      expect(container.read(filterFocusOffsetsProvider).error, isNotNull);
      expect(
        container.read(filterFocusOffsetsProvider).snapshot.calibration,
        same(_calibration),
      );
      api.fail = false;
      await notifier.cancel();
      expect(api.cancelled, 'job');
      expect(
        container.read(filterFocusOffsetsProvider).snapshot.job!.state,
        'cancelled',
      );
      await notifier.clear();
      expect(
        container.read(filterFocusOffsetsProvider).snapshot.calibration,
        isNull,
      );
    },
  );

  test(
    'late response from previous server cannot replace new server measurements',
    () async {
      final pending = Completer<FilterFocusOffsetSnapshot>();
      final oldApi = _Api()..pendingLoad = pending.future;
      final newApi = _Api()
        ..snapshot = FilterFocusOffsetSnapshot(calibration: _calibration);
      final container = ProviderContainer(
        overrides: [
          filterFocusOffsetsApiProvider.overrideWithValue(oldApi),
          profileManagementProvider.overrideWith(_Profiles.new),
        ],
      );
      addTearDown(container.dispose);
      container.read(filterFocusOffsetsProvider);
      await Future<void>.delayed(Duration.zero);
      container.updateOverrides([
        filterFocusOffsetsApiProvider.overrideWithValue(newApi),
        profileManagementProvider.overrideWith(_Profiles.new),
      ]);
      container.read(filterFocusOffsetsProvider);
      await Future<void>.delayed(Duration.zero);
      pending.complete(const FilterFocusOffsetSnapshot());
      await Future<void>.delayed(Duration.zero);
      expect(
        container.read(filterFocusOffsetsProvider).snapshot.calibration,
        same(_calibration),
      );
    },
  );

  testWidgets(
    'calculate, progress, cancel and measured signed offsets render',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(1000, 900));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      final api = _Api()
        ..snapshot = FilterFocusOffsetSnapshot(calibration: _calibration);
      await tester.pumpWidget(
        ProviderScope(
          overrides: [
            filterFocusOffsetsApiProvider.overrideWithValue(api),
            profileManagementProvider.overrideWith(_Profiles.new),
            filterWheelProvider.overrideWith(_Wheel.new),
          ],
          child: const MaterialApp(
            home: Scaffold(
              body: SingleChildScrollView(child: FilterFocusOffsetsCard()),
            ),
          ),
        ),
      );
      await tester.pumpAndSettle();
      expect(find.textContaining('Ha (slot 1): +200 steps'), findsOneWidget);
      await tester.tap(find.text('Calculate offsets'));
      await tester.pumpAndSettle();
      expect(api.reference, 0);
      expect(find.text('Calibrating: 1/3 autofocus runs'), findsOneWidget);
      expect(
        tester
            .widget<FilledButton>(
              find.widgetWithText(FilledButton, 'Calculate offsets'),
            )
            .onPressed,
        isNull,
      );
      await tester.tap(find.text('Cancel calibration'));
      await tester.pumpAndSettle();
      expect(api.cancelled, 'job');
      expect(
        find.text('Calibration cancelled. Previous offsets retained.'),
        findsOneWidget,
      );
    },
  );
}
