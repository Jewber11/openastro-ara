import 'package:dio/dio.dart';

import '../models/filter_focus_offsets.dart';
import '../models/server.dart';
import 'autofocus_api.dart';

abstract interface class FilterFocusOffsetsApi {
  Future<FilterFocusOffsetSnapshot> load();
  Future<AutofocusJob> calibrate(int referencePosition);
  Future<void> cancel(String jobId);
  Future<void> clear();
  void close();
}

class DioFilterFocusOffsetsApi implements FilterFocusOffsetsApi {
  final Dio _dio;
  DioFilterFocusOffsetsApi(AraServer server, {Dio? dio})
    : _dio =
          dio ??
          Dio(
            BaseOptions(
              baseUrl: server.baseUrl,
              connectTimeout: const Duration(seconds: 3),
              receiveTimeout: const Duration(seconds: 10),
            ),
          );
  static const _path = '/api/v1/autofocus/filter-offsets';

  @override
  Future<FilterFocusOffsetSnapshot> load() async =>
      FilterFocusOffsetSnapshot.fromJson(
        (await _dio.get<Map<String, dynamic>>('$_path/')).data!,
      );

  @override
  Future<AutofocusJob> calibrate(int referencePosition) async =>
      AutofocusJob.fromJson(
        (await _dio.post<Map<String, dynamic>>(
          '$_path/calibrate',
          data: {'reference_position': referencePosition},
        )).data!,
      );

  @override
  Future<void> cancel(String jobId) async =>
      _dio.delete<void>('/api/v1/jobs/$jobId');

  @override
  Future<void> clear() async => _dio.delete<void>('$_path/');

  @override
  void close() => _dio.close(force: true);
}
