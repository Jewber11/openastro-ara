import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../models/filter_focus_offsets.dart';
import '../../services/filter_focus_offsets_api.dart';
import '../../util/friendly_error.dart';
import '../saved_server_state.dart';
import '../profile_management_state.dart';

final filterFocusOffsetsApiProvider = Provider<FilterFocusOffsetsApi?>((ref) {
  final server = ref.watch(activeServerProvider);
  if (server == null) return null;
  final api = DioFilterFocusOffsetsApi(server);
  ref.onDispose(api.close);
  return api;
});

class FilterFocusOffsetsState {
  final FilterFocusOffsetSnapshot snapshot;
  final bool busy;
  final String? error;
  const FilterFocusOffsetsState({
    this.snapshot = const FilterFocusOffsetSnapshot(),
    this.busy = false,
    this.error,
  });
}

class FilterFocusOffsetsNotifier extends Notifier<FilterFocusOffsetsState> {
  Timer? _poll;
  int _generation = 0;
  int _loadGeneration = 0;

  @override
  FilterFocusOffsetsState build() {
    _generation++;
    _poll?.cancel();
    ref.onDispose(() => _poll?.cancel());
    final api = ref.watch(filterFocusOffsetsApiProvider);
    // Switching profiles invalidates displayed measurements even on the same server.
    ref.watch(
      profileManagementProvider.select((s) => s.asData?.value.activeId),
    );
    if (api != null) Future<void>.microtask(refresh);
    return const FilterFocusOffsetsState();
  }

  Future<void> refresh() async {
    final api = ref.read(filterFocusOffsetsApiProvider);
    if (api == null) return;
    final generation = _generation;
    final load = ++_loadGeneration;
    _poll?.cancel();
    try {
      final snapshot = await api.load();
      if (!ref.mounted || generation != _generation || load != _loadGeneration) {
        return;
      }
      state = FilterFocusOffsetsState(
        snapshot: snapshot,
        error: snapshot.job?.state == 'failed'
            ? snapshot.job?.errorMessage
            : null,
      );
    } catch (e) {
      if (!ref.mounted || generation != _generation || load != _loadGeneration) {
        return;
      }
      state = FilterFocusOffsetsState(
        snapshot: state.snapshot,
        error: friendlyError(e, action: 'load filter offsets'),
      );
    }
    if (ref.mounted &&
        generation == _generation &&
        load == _loadGeneration &&
        state.snapshot.running) {
      _poll = Timer(const Duration(seconds: 1), () => unawaited(refresh()));
    }
  }

  Future<void> _perform(
    Future<void> Function(FilterFocusOffsetsApi api) action,
  ) async {
    final api = ref.read(filterFocusOffsetsApiProvider);
    if (api == null || state.busy) return;
    final generation = _generation;
    _loadGeneration++;
    _poll?.cancel();
    state = FilterFocusOffsetsState(snapshot: state.snapshot, busy: true);
    try {
      await action(api);
      if (!ref.mounted || generation != _generation) return;
      await refresh();
    } catch (e) {
      if (!ref.mounted || generation != _generation) return;
      state = FilterFocusOffsetsState(
        snapshot: state.snapshot,
        error: friendlyError(e, action: 'update filter offsets'),
      );
    }
  }

  Future<void> calibrate(int referencePosition) => _perform((api) async {
    await api.calibrate(referencePosition);
  });

  Future<void> cancel() => _perform((api) async {
    final id = state.snapshot.job?.jobId;
    if (id != null) await api.cancel(id);
  });

  Future<void> clear() => _perform((api) => api.clear());
}

final filterFocusOffsetsProvider =
    NotifierProvider<FilterFocusOffsetsNotifier, FilterFocusOffsetsState>(
      FilterFocusOffsetsNotifier.new,
    );
