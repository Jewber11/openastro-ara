import '../services/autofocus_api.dart';

class FilterFocusOffset {
  final int position;
  final String name;
  final int offsetSteps;
  const FilterFocusOffset(this.position, this.name, this.offsetSteps);

  factory FilterFocusOffset.fromJson(Map<String, dynamic> json) =>
      FilterFocusOffset(
        (json['position'] as num).toInt(),
        json['name'] as String,
        (json['offset_steps'] as num).toInt(),
      );
}

class FilterFocusOffsets {
  final int referencePosition;
  final DateTime calibratedUtc;
  final List<FilterFocusOffset> filters;
  const FilterFocusOffsets(
    this.referencePosition,
    this.calibratedUtc,
    this.filters,
  );

  factory FilterFocusOffsets.fromJson(Map<String, dynamic> json) =>
      FilterFocusOffsets(
        (json['reference_position'] as num).toInt(),
        DateTime.parse(json['calibrated_utc'] as String),
        (json['filters'] as List)
            .map((f) => FilterFocusOffset.fromJson(f as Map<String, dynamic>))
            .toList(),
      );
}

class FilterFocusOffsetSnapshot {
  final FilterFocusOffsets? calibration;
  final AutofocusJob? job;
  const FilterFocusOffsetSnapshot({this.calibration, this.job});

  factory FilterFocusOffsetSnapshot.fromJson(Map<String, dynamic> json) =>
      FilterFocusOffsetSnapshot(
        calibration: json['calibration'] == null
            ? null
            : FilterFocusOffsets.fromJson(
                json['calibration'] as Map<String, dynamic>,
              ),
        job: json['job'] == null
            ? null
            : AutofocusJob.fromJson(json['job'] as Map<String, dynamic>),
      );

  bool get running => job != null && !job!.isTerminal;
}
