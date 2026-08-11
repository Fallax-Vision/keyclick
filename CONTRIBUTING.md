# Contributing to KeyClick

Thank you for helping make KeyClick better. Keep changes focused, preserve the privacy and release-only input model, and avoid adding polling or database work to the input/audio paths.

## Development workflow

1. Create a focused branch from `main`.
2. Run `dotnet restore KeyClick.sln`.
3. Implement the smallest coherent change with two-space indentation.
4. Add or update tests, including shared-contract fixtures when behavior is platform-neutral.
5. Run `dotnet build KeyClick.sln -c Release` and `dotnet test KeyClick.sln -c Release`.
6. Explain observable behavior, privacy/performance impact, and verification in the pull request.

Do not commit imported user audio, local databases, logs, build artifacts, certificate material, or generated portable payloads.

## Non-negotiable properties

- Sound playback is triggered on physical release, never key-down or repeat.
- Pointer movement never causes playback or persistence.
- Typed characters and key-event history are never stored or logged.
- Integrations are disabled by default, current-user-only, allow-listed, bounded, and semantic.
- Built-in packs stay immutable; customization is a reversible override layer.
- The application never elevates itself.
