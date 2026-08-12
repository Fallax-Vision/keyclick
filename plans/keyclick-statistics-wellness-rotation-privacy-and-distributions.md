# KeyClick Statistics, Wellness, Rotation, Privacy, and Distributions

## Summary

Continue the native Windows v1 application with local aggregate keyboard and
pointer statistics, wellness tools, automatic sound-pack rotation,
cross-platform profile transfer, strict manual-only networking, and separate
portable and per-user setup distributions.

Statistics remain independent from sound playback. Keyboard and pointer
statistics default on after a one-time disclosure; only aggregate physical
input counts are retained. KeyClick never stores typed characters, ordered
input history, individual input timestamps, foreground-app activity, or UI
content, and statistics never leave the device through any network path.
Sounds and statistics run only while the process is alive: choosing Exit in the
tray or closing the app window flushes pending aggregates, disposes Raw Input
and audio, and terminates KeyClick. Minimizing may keep the app running in the
tray when enabled.

## Product behavior

- Add a Statistics page with Overview, Pointer, Keyboard, and Wellness views.
  Support Today, 7 days, 30 days, This month, This year, All time, and custom
  periods, compared with the previous equivalent period or the same period in
  the previous year.
- Show colored summary cards, native WPF charts, pointer-button/device-family
  breakdowns, scroll detents, active time, average/peak KPM, estimated WPM,
  average/peak CPS, busiest periods, and a physical-key keyboard heatmap.
- Default keyboard, pointer, and scrolling statistics on. Keep their toggles
  independent from one another and from all sound toggles. Add separate live
  app exclusions, tray pause controls, and manual pointer-device classification.
- Retain aggregates until the user deletes a chosen period/category or all
  statistics. Offer a checked-by-default safety backup and CSV aggregate export.
- Add keyboard audio timing. New installs/resets use Key down; existing installs
  migrate to Key up. Ignore typematic repeats. Pointer audio stays release-only.
- Add sound-pack rotation, disabled by default, with 1/10/30 minutes, 1 hour,
  1 day, 1 week, per Windows boot, and 1..525600 custom minutes. Default its
  configuration to 30 minutes/all packs, require two packs, and never repeat
  the current pack immediately.
- New installs/resets use Cream Keys. Existing installs retain their pack.
- Add optional daily key/click/active-time goals and local break reminders,
  disabled by default. Suggested goals are 1000 keys, 500 clicks, 60 active
  minutes; reminder defaults are 60 active minutes and 10 minutes of rest.

## Architecture and persistence

- Emit compact input state transitions from Raw Input. Track physical key state,
  emit the first make plus break, keep shortcut matching on release, and move
  foreground/device discovery out of the hot callback into event-driven caches.
- Add a bounded non-blocking statistics queue and one background aggregator.
  Bucket by UTC hour, calculate durations/rates from monotonic timestamps, and
  flush dirty aggregates every 60 seconds, at rollover, on disable, and exit.
  Never access SQLite from the input callback or write once per input.
- Add idempotent migration 3 for a random local statistics source, per-input
  hourly buckets, hourly activity summaries, revisions, and wellness
  achievement snapshots. Keep the database in existing backups.
- Define a versioned `.keyclickprofile` contract under `shared/specs`. Export
  selected transferable preferences/mappings by default and optionally local
  packs/media, aggregates, and achievements. Never export machine paths,
  startup/output/update state, app exclusions, or integration allow-lists.
- Preview and merge imports. Replace only selected transferable preferences,
  default conflicts to Keep local, and merge statistics idempotently by source,
  bucket, and revision. Support optional AES-256-GCM protection using
  PBKDF2-HMAC-SHA256.

## Privacy and networking

- Isolate update networking in a manual-update project with no statistics
  dependency. Construct its client only after Check for updates, use HTTPS GET
  only against approved GitHub release hosts, and allow no telemetry, automatic
  checks, remote profiles, background pings, or other network clients.
- Add a required Privacy Boundary CI check for forbidden network APIs, updater
  signatures that accept input/statistics payloads, startup network calls,
  telemetry, or weakened privacy documentation.
- Protect the privacy guard, manual updater, statistics storage, workflows, and
  privacy policy through CODEOWNERS approval by `@askasjeremy`. Require Privacy
  Boundary, x64/ARM64 CI, code-owner review, stale-review dismissal, and no force
  pushes on `main`.
- Update README, PRIVACY, SECURITY, CONTRIBUTING, AGENTS guidance, PR template,
  and the original v1 plan with the aggregate-only/manual-network invariants.

## Distribution

- Produce portable and setup executables for x64 and ARM64. Publish the setup
  executables under the legacy names for one compatibility release.
- Portable mode uses `KeyClickData` beside the launcher, performs no installation
  by default, and may explicitly switch to the installed AppData store after a
  restart. If its folder is not writable, offer AppData mode or exit clearly.
- Setup mode is current-user-only and non-elevated, creates selected shortcuts,
  registers HKCU uninstall metadata, preserves data during upgrades, and keeps
  verified manual updates and preserve/delete uninstall choices.

## Verification

- Test key-down/key-up timing, repeat suppression, release shortcuts, independent
  capture/playback toggles, statistics accuracy, comparisons, deletion/export,
  time zones, wellness, rotation schedules, profile security/merging, privacy
  boundaries, and clean x64/ARM64 portable/setup lifecycles.
- QA Light/Dark/System UI, EN/FR parity, charts and heatmap at minimum size.
- Preserve the v1 performance gates: no input-path blocking/database work, idle
  tray CPU below 0.2%, hidden working set below 120 MB, input-to-audio submit p95
  at or below 5 ms, stable rapid-input operation, and no work from pointer motion.
