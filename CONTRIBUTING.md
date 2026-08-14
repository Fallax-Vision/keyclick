# Contributing to KeyClick

Thank you for helping make KeyClick better. Keep changes focused, preserve the privacy boundary and configurable first-key-down/key-up input model, and avoid adding polling or database work to the input/audio paths.

## Development workflow

1. Create a focused branch from `main`.
2. Run `dotnet restore KeyClick.sln`.
3. Implement the smallest coherent change with two-space indentation.
4. Add or update tests, including shared-contract fixtures when behavior is platform-neutral.
5. Run `dotnet build KeyClick.sln -c Release` and `dotnet test KeyClick.sln -c Release`.
6. Run `./scripts/Test-PrivacyBoundary.ps1`.
7. Explain observable behavior, privacy/performance impact, and verification in the pull request.

Do not commit imported user audio, local databases, logs, build artifacts, certificate material, or generated portable payloads.

## Non-negotiable properties

- Keyboard audio follows the selected first-key-down/key-up mode and never plays on typematic repeat. Shortcuts remain release-based; pointer audio remains button-up/wheel-detent only.
- Pointer movement never causes playback or persistence.
- KeyClick never stores typed characters, typing order, per-event timestamps, raw application paths in statistics, UI content, or key-event history. Per-application aggregates contain only a source-salted ID, filename, and category totals—never app-specific physical-key counts.
- Aggregate keyboard, mouse, and per-application statistics are never transmitted. Per-application details must remain excluded from CSV/profile exports. Network access is manual update check/download only.
- Typing challenge responses are memory-only and must never be persisted, logged, exported, or transmitted. Result storage is aggregate-only. A source prompt may be stored only after explicit confirmation, and saved prompts require password protection for local profile export.
- Integrations are disabled by default, current-user-only, allow-listed, bounded, and semantic.
- Built-in packs stay immutable; customization is a reversible override layer.
- The application never elevates itself.

Every pull request must pass the required **Privacy Boundary** check. Pull requests that add networking outside `KeyClick.Updater`, automatic pings, telemetry, statistics or challenge transmission, persisted challenge responses, export of per-application details, updater body/payload APIs, or weakened privacy documentation must be rejected. Privacy-critical changes require `@askasjeremy` approval.
