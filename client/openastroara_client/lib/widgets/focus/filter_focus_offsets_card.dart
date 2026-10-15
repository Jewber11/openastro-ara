import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../state/equipment/filter_wheel_state.dart';
import '../../state/focus/filter_focus_offsets_state.dart';
import '../../widgets/settings/settings_row.dart';
import '../help_icon.dart';

/// Calibration uses the saved autofocus exposure/sweep settings on every filter.
class FilterFocusOffsetsCard extends ConsumerStatefulWidget {
  const FilterFocusOffsetsCard({super.key});
  @override
  ConsumerState<FilterFocusOffsetsCard> createState() =>
      _FilterFocusOffsetsCardState();
}

class _FilterFocusOffsetsCardState
    extends ConsumerState<FilterFocusOffsetsCard> {
  int? _reference;

  @override
  Widget build(BuildContext context) {
    final s = ref.watch(filterFocusOffsetsProvider);
    final notifier = ref.read(filterFocusOffsetsProvider.notifier);
    final wheel = ref.watch(filterWheelProvider).asData?.value;
    final slots = wheel?.slots ?? [];
    final reference = slots.any((f) => f.position == _reference)
        ? _reference
        : slots.firstOrNull?.position;
    final running = s.snapshot.running;
    final job = s.snapshot.job;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const SettingsSectionHeader(
          'Filter focus offsets',
          trailing: HelpIcon(helpKey: 'img.autofocus.filter_offsets'),
        ),
        const Text(
          'Save autofocus settings first. Point at a star field, stop sequences and Live View, and turn focuser temperature compensation off. Ara focuses each filter and measures offsets against a reference, correcting for gradual focus drift.',
        ),
        const SizedBox(height: 12),
        if (slots.isNotEmpty)
          DropdownButtonFormField<int>(
            key: ValueKey(reference),
            initialValue: reference,
            decoration: const InputDecoration(labelText: 'Reference filter'),
            items: slots
                .map(
                  (f) => DropdownMenuItem(
                    value: f.position,
                    child: Text('${f.name} (slot ${f.position})'),
                  ),
                )
                .toList(),
            onChanged: running || s.busy
                ? null
                : (v) => setState(() => _reference = v),
          ),
        const SizedBox(height: 12),
        Wrap(
          spacing: 8,
          runSpacing: 8,
          children: [
            FilledButton(
              onPressed:
                  !running &&
                      !s.busy &&
                      reference != null &&
                      slots.length >= 2 &&
                      wheel?.manual == false
                  ? () => notifier.calibrate(reference)
                  : null,
              child: const Text('Calculate offsets'),
            ),
            if (running)
              OutlinedButton(
                onPressed: s.busy ? null : notifier.cancel,
                child: const Text('Cancel calibration'),
              ),
            if (s.snapshot.calibration != null)
              OutlinedButton(
                onPressed: running || s.busy ? null : notifier.clear,
                child: const Text('Clear offsets'),
              ),
            TextButton(
              onPressed: s.busy ? null : notifier.refresh,
              child: const Text('Refresh offsets'),
            ),
          ],
        ),
        if (running) ...[
          const SizedBox(height: 8),
          LinearProgressIndicator(
            value: job!.total > 0 ? job.done / job.total : null,
          ),
          Text('Calibrating: ${job.done}/${job.total} autofocus runs'),
        ],
        if (job?.state == 'cancelled')
          const Text('Calibration cancelled. Previous offsets retained.'),
        if (s.snapshot.calibration case final calibration?) ...[
          const SizedBox(height: 8),
          Text(
            'Measured ${calibration.calibratedUtc.toLocal()}. Applied automatically on filter changes.',
          ),
          for (final filter in calibration.filters)
            Text(
              '${filter.name} (slot ${filter.position}): ${filter.offsetSteps > 0 ? '+' : ''}${filter.offsetSteps} steps${filter.position == calibration.referencePosition ? ' (reference)' : ''}',
            ),
        ] else if (!running)
          const Text(
            'No measured offsets. Connect a motorized wheel and absolute focuser to calibrate.',
          ),
        if (s.error != null)
          Text(
            s.error!,
            style: TextStyle(color: Theme.of(context).colorScheme.error),
          ),
        const SizedBox(height: 24),
      ],
    );
  }
}
