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
- KeyClick never stores typed characters, typing order, per-event timestamps, foreground applications, UI content, or key-event history.
- Aggregate keyboard and mouse statistics are never transmitted. Network access is manual update check/download only.
- Integrations are disabled by default, current-user-only, allow-listed, bounded, and semantic.
- Built-in packs stay immutable; customization is a reversible override layer.
- The application never elevates itself.

Every pull request must pass the required **Privacy Boundary** check. Pull requests that add networking outside `KeyClick.Updater`, automatic pings, telemetry, statistics transmission, updater body/payload APIs, or weakened privacy documentation must be rejected. Privacy-critical changes require `@askasjeremy` approval.
