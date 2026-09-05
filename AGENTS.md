# KeyClick project guidance

## Scope and implementation

- Treat this file as durable KeyClick policy. Keep task-specific plans, temporary findings, and secrets out of it.
- Understand the request before editing, reuse existing patterns, and keep changes narrowly scoped. Prefer the smallest maintainable implementation.
- Preserve unrelated and uncommitted work. Ask only when a material product or safety choice cannot be inferred.
- Keep platform-neutral schemas, manifests, identifiers, catalogs, and fixtures under `shared/`; native code belongs under `apps/<platform>`.
- Use settings JSON for presentation preferences. Add transactional, idempotent SQLite migrations only for persisted application data that genuinely requires a schema change.
- Do not commit, push, tag, publish, migrate, deploy, or delete a published release unless the owner explicitly requests that action.

## Input, audio, and architecture invariants

- Track physical keyboard state from Raw Input. Suppress typematic repeats; clear state on break and device removal; count the first key-down once; match shortcuts on release.
- Pointer audio and click effects use completed button-release events; wheel behavior uses accumulated detents. Do not change sound, statistics, or shortcut semantics while adding visuals.
- Keep Raw Input callbacks non-blocking and allocation-free. Never decode media, query storage, create display names, log input, dispatch to WPF, or perform network/process work on that path.
- Predecode active-pack audio to 48 kHz mono 16-bit PCM and use the reusable 32-voice XAudio2 pool. Load media asynchronously and swap immutable caches.
- Built-in packs and catalogs are immutable. Store customization as validated, reversible overrides with stable IDs.
- Cursor assets must be original. Record provenance, validate every common role and hotspot, and regenerate compiled cursor assets whenever sources or manifests change.
- System cursor changes must be atomic and recoverable. Experimental overlay or suppression features require a visible failsafe, watchdog recovery, panic controls, and automatic restoration.

## Privacy and security

- Never store or log typed characters, typing order, event histories/timestamps, challenge responses, UI content, raw HID paths, raw application paths, secrets, or telemetry.
- Per-application statistics may persist only a source-salted ID, filename, and aggregate counts. Never persist app-specific physical-key counts.
- Network access is manual update checking/downloading through `KeyClick.Updater` only. Never transmit statistics or create a network client at startup.
- Verify release hashes immediately before execution. Keep integrations disabled by default, current-user-only, allow-listed, bounded, and semantic. Never elevate the running app.
- Validate and normalize imported JSON, profiles, archives, cursor catalogs, colors, IDs, enums, ranges, labels, and collection sizes. Prevent path traversal, archive bombs, CSV formula injection, and injected-input loops.
- Pin CI actions by full commit SHA, use least-privilege workflow permissions, lock dependencies, and run dependency-vulnerability checks.
- Reject changes that add automatic pings, telemetry, statistics transmission, arbitrary commands/paths, updater payload bodies, or weaker Privacy Boundary protections.
- Run a standard single-pass security scan when the owner requests a security review; do not substitute a deep scan.

## Async behavior and performance

- Keep disk, database, media, device-enumeration, update, backup, clipboard-rendering, and process-launch work asynchronous and cancellable where practical. Keep trivial in-memory work synchronous; do not use fake async wrappers.
- Never block the UI thread or Raw Input path. Disable or debounce duplicate actions and provide localized busy, success, and failure feedback.
- Prefer event-driven dirty invalidation and coalesce visible statistics refreshes to at most once per second. Do not poll unchanged data while pages are idle or hidden.
- Effects must remain off by default, render only while active, stop after settling, use bounded pools/caches, and perform no per-movement WPF dispatcher work.
- Cap effects at 60 FPS, reduce on battery, respect Reduced Motion, and pause or simplify for battery saver, fullscreen apps, remote sessions, lock, unsupported GPU composition, or sustained frame-time problems.
- Before releasing performance-sensitive changes, compare startup, idle CPU/GPU, input throughput, sound playback, statistics refresh, renderer activity, and device hotplug against the prior version. Default mode must show no statistically significant regression.

## UX, accessibility, and localization

- Follow the existing design language. Use two-space indentation; do not add drop shadows or translate-on-hover effects.
- Significant UI changes require a UX psychology review before release: simplify choices, group related controls, use familiar patterns, emphasize one primary action, and keep destructive actions distinct.
- Support Light, Dark, and System themes, high DPI, minimum window size, keyboard navigation, logical focus order, visible focus, automation names, accessible summaries, and Reduced Motion.
- Keep controls at least 40 device-independent pixels where practical, preferably 44 for primary targets. Align labels and fields consistently and let title-row controls wrap cleanly.
- Visual grids must fill available width and dynamically adjust column count and card width without clipped or orphaned items.
- Dialogs and popovers must be compact, scrollable when needed, focus-contained, Escape-dismissable when safe, and return focus to their trigger.
- Hover tooltips for chart data remain visible while the pointer is over the active datum, follow pointer movement, switch immediately to a new datum, and close on leaving the visualization.
- Use delayed explanatory tooltips only when the label alone is insufficient; never hide required instructions exclusively in hover content.
- Add every user-facing string to both English and French resources in the same change. Preserve user-entered labels exactly.

## Data, logs, backups, and storage retention

- Storage retention applies to app-generated operational files, not user-owned aggregate statistics, imported media, profiles, or manual exports. Never silently delete user data.
- Keep statistics aggregate-only and compact; never add raw event history. Perform database checkpoint/maintenance off the input and UI paths.
- Use bounded rolling logs: at most 7 files, no older than 14 days, no file over 5 MiB, and no more than 25 MiB total. Preserve the three newest useful crash/security diagnostics within that cap and redact private data.
- Keep at most five app-managed backups: the three newest automatic backups, the newest validated pre-update backup, and the newest validated pre-destructive-action backup. A file satisfying multiple roles counts once.
- Validate a newly created backup before pruning. Never remove the newest backup, the only valid backup, a user-pinned backup, or an external/manual export. Report cleanup failures without blocking normal startup.
- Preserve current user data across upgrades, then apply retention asynchronously. Never let an update mistake operational cleanup for uninstall data removal.
- Delete completed update downloads and abandoned staging folders after verified success or safe rollback; keep at most one verified pending update package.
- Installed and portable application payloads keep the current version plus one verified rollback version only. Remove older payloads only after the new version launches successfully.
- Local `artifacts/` keeps the current release and immediately preceding release only, including their Setup, Portable, checksum, and SBOM files. Treat those files as one version set.
- Ordinary GitHub Actions artifacts and test results use `retention-days: 7`; release-staging artifacts use at most 14 days. Workflows must not upload `bin/`, `obj/`, caches, raw logs, databases, or user data.
- GitHub release assets keep the current release plus at most two prior release version sets. Prune only attached binaries/checksums/SBOMs outside that window after verifying retained assets; preserve release notes, tags, and source history unless the owner explicitly authorizes their deletion.
- Never rewrite published Git history merely to reduce storage. Use shallow, single-worktree clones for testing/production and normal Git garbage collection for unreachable objects. Large-history surgery or Git LFS migration requires explicit, coordinated owner approval.
- Remove temporary package, test, coverage, and migration staging output after success or rollback. Any new cache, log, backup, or generated-history feature must define a size/count/age cap and cleanup tests before release.

## Build, test, and release

- After every source, XAML, project, script, localization, or cursor-asset change, immediately compile or validate the affected project/file. Documentation-only rule changes have no compilation target.
- Before handoff, run `dotnet build KeyClick.sln -c Release` and tests proportional to the change. Before release, run `dotnet test KeyClick.sln -c Release`, privacy-boundary checks, standard security checks, and dependency-vulnerability checks.
- Package with `./scripts/Build-Portable.ps1 -Version <semver>` and verify x64/ARM64 Setup and Portable editions, checksums, SBOMs, themes, accessibility, tray recreation, both keyboard timing modes, statistics independence, update/rollback, and uninstall recovery.
- Save every implementation plan created in Plan Mode under `plans/` before implementation.
- Follow SemVer. Bump PATCH for backward-compatible fixes, MINOR for significant backward-compatible features/public contracts, and MAJOR for incompatible changes. Never rewrite a published version.
- Keep the canonical version only in `Directory.Build.props`; use it in every setup, portable, checksum, SBOM, tag, and release filename.
- For owner-requested commits, work directly on `main`, review the final diff, and push only after required checks pass. Never create a project-owner feature branch.
- Production deployment or migration requires an explicit request, verified target and credentials, successful backups, a rollback path, and completed release checks. KeyClick currently has no server database; do not invent a production migration.
