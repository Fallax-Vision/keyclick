# KeyClick project guidance

## Architecture invariants

- Trigger keyboard audio only for Raw Input `RI_KEY_BREAK`; trigger pointer audio only for button-up and accumulated wheel detents. Pointer movement, key-down, and repeat never play sounds.
- Keep the Raw Input callback non-blocking and allocation-free. Never decode media, query/write SQLite, construct display names, log input, or access WPF dispatcher-owned state on that path.
- Predecode active-pack audio to 48 kHz mono 16-bit PCM and play through the reusable 32-voice XAudio2 pool. Load packs and custom media asynchronously, then swap caches.
- Built-in packs are immutable. Persist customization as reversible per-pack input/variant overrides.
- Keep platform-neutral schemas, manifests, identifiers, and fixtures under `shared/`; native platform apps stay under `apps/<platform>`.

## Privacy and safety

- Never store or log typed characters, key-event history, UI content, or telemetry.
- Integrations remain disabled by default, current-user-only, allow-listed, length/rate bounded, and semantic; never accept arbitrary audio paths.
- Network access is manual update-check/download only. Verify release SHA-256 checksums before replacement. Never elevate KeyClick.
- Preserve `%LOCALAPPDATA%\KeyClick\data`, `media`, `logs`, and `backups` across versioned app updates.

## Development and verification

- Use two-space indentation and keep the Windows UI free of drop shadows and translate-on-hover motion.
- Build with `dotnet build KeyClick.sln -c Release`.
- Test with `dotnet test KeyClick.sln -c Release`.
- Package with `./scripts/Build-Portable.ps1 -Version <semver>`.
- Verify x64 and ARM64, Light/Dark/System themes, tray recreation, key-up-only behavior, and dependency vulnerability status before release.

## Planning, release, security, and localization rules

- Every time a plan is created in Plan Mode, save it as a Markdown file in `plans/`, using the most appropriate filename based on the plan title, before implementing it.
- Do not commit or push unless the project owner explicitly requests that specific action.
- When the project owner asks for a full or partial security check, run a standard security scan, not a deep scan.
- Whenever a new string is created, make sure it is translated in both English and French.
