# KeyClick Typing Challenges

## Summary

Add a dedicated offline Typing Challenge area with bundled English/French
passages, explicitly saved custom prompts, private free writing, timed and
untimed modes, strict and flow mistake handling, visual aggregate results,
period comparisons, and local streaks.

Challenge response text exists only in memory and is discarded when a run
ends. Only aggregate results and five-second speed samples may be persisted.
Challenge data never enters the updater or any network boundary.

## Product behavior

- Provide Setup, Active Challenge, Results, and History views from a dedicated
  main-menu item.
- Offer bundled passages with stable IDs, language/difficulty filters, random
  selection, favorites, and repeat. Support pasted custom prompts and free
  writing. Custom prompts are saved only after explicit confirmation.
- Support passage completion, single-passage time limits, continuous timed
  challenges, and free writing with 15/30-second and 1/3/5-minute presets plus
  a validated 10-second to 60-minute custom duration.
- Start timing on the first printable input, reject response-field paste,
  pause when the app loses focus, and require explicit resume.
- Provide Flow and Strict correction modes. Results show net/gross WPM, KPM,
  accuracy, errors, corrections, duration, characters, words, consistency, and
  an accessible five-second speed chart.
- Compare optionally with a previous similar run, personal best, a selected
  historical run, and normal typing statistics for an existing period.
- Track participation and performance-goal streaks. Performance defaults to
  40 WPM and 95% accuracy and snapshots the targets used.
- Exclude challenge input from ordinary keyboard statistics and keyboard
  wellness by default. A setting may opt in; sound behavior remains unchanged.
- Retain aggregate history until explicitly deleted. Support per-result and
  period deletion, local aggregate CSV export, and default-on safety backups.

## Architecture and persistence

- Add focused challenge contracts and an `ITypingChallengeStore`, implemented
  by the existing SQLite store through idempotent migration 5.
- Store aggregate session metrics, five-second elapsed-time samples, explicitly
  saved source prompts, favorites, and streak achievement snapshots. Never
  store response text, ordered input, or individual key-event timestamps.
- Keep matching and metrics on the WPF text-input path using Unicode text
  elements and a monotonic clock. Raw Input receives only a cheap atomic active
  challenge gate used for optional normal-statistics suppression.
- Extend local profile format v2 with optional, default-off challenge history
  and saved-prompt sections. Continue importing v1 profiles. Require password
  protection when saved prompts are exported.
- Extend the Privacy Boundary guard and documentation so challenge types and
  data cannot enter updater or network-facing APIs.

## Verification and release

- Test modes, timing, correction behavior, Unicode input, focus pause/resume,
  metrics, comparisons, streaks, deletion, profile compatibility, and privacy.
- Verify Light/Dark/System themes, keyboard navigation, reduced motion,
  English/French parity, and the existing performance limits.
- Treat this backward-compatible feature as version 1.4.0 when 1.3.0 is the
  release baseline, then run Release build/tests, Privacy Boundary, dependency
  audit, and x64/ARM64 packaging verification.
