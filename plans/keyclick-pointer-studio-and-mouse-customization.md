# KeyClick Pointer Studio and Mouse Customization

## Summary

- Add **Pointer Studio** as a new top-level left-navigation page after Keyboard & Pointer.
- Preserve the current uncommitted tray, installer, Fun Stats, and visualization changes.
- Release the significant backward-compatible feature as version **1.6.0**.
- Keep the feature offline, per-user, non-elevated, and settings-JSON-only.

## Pointer Studio experience

- Organize the page into Designs, Motion & Clicks, Devices & Buttons, and Performance & Safety tabs with a live cursor-role preview.
- Default the apply scope to system-wide while supporting KeyClick-only and temporary preview modes.
- Ship at least ten original cursor families with four sizes, accurate hotspots, and light/dark/high-contrast variants. The Flaticon gallery is visual direction only; no third-party assets are copied or redistributed.
- Add automatic light/dark variants, favorites, tray quick-switching, and Restore Previous Scheme/Restore Windows Defaults.
- Separate Windows-native speed, acceleration, trails, and native shadow from GPU-rendered visual scale, shadow, smoothing, spring, damping, trail, shake-to-enlarge, and find-pointer effects.
- Support safe companion effects and an explicitly Experimental guarded full-replacement mode.
- Add independently configurable left, right, wheel/middle, and auxiliary click indicators with ring, ripple, tiny explosion, sparkles, radial ticks, pulse, and none styles.
- Enumerate connected mice and exposed trackpads without persisting raw HID paths; move pointer classification into Pointer Studio.
- Add safe per-device auxiliary actions that preserve original clicks and guarded global Experimental remap/suppression with panic recovery.

## Architecture and persistence

- Add versioned settings/presentation models for themes, scope, sizes, native controls, motion/click effects, device actions, suppression, adaptive pauses, and recovery.
- Isolate Windows cursor settings, device enumeration, effect rendering, action dispatch, suppression, and health/recovery behind testable services.
- Keep the immutable cursor catalog, original source definitions, compiled cursor roles, hotspots, and provenance under `shared/`, with deterministic recompilation after asset changes.
- Snapshot and atomically restore prior per-user Windows cursor settings, roll back failed applies, and detect external cursor changes.
- Keep effect work event-driven and outside the Raw Input callback and WPF dispatcher; use hardware composition when available and render no frames at rest.
- Guard full replacement with a visible failsafe cursor, startup/crash recovery, a watchdog, tray disable action, and `Ctrl+Alt+Shift+F12` panic control.
- Persist transferable preferences through profiles while excluding device IDs and never re-enabling Experimental modes after import.
- Use settings JSON only; no SQLite schema or SQL changes.

## Performance and safety

- Leave the current Windows cursor unchanged by default; keep motion, click indicators, and suppression off until enabled.
- Preserve the allocation-free Raw Input hot path, use bounded cached rendering resources, cap rendering at 60 FPS/30 FPS on battery, and sleep after settling.
- Adaptively pause or simplify effects for battery saver, fullscreen applications, remote sessions, Reduced Motion, unsupported GPUs, session lock, or sustained frame-time problems.
- Replace unconditional one-second visible-statistics queries with dirty, event-driven invalidation coalesced to at most once per second.
- Profile startup, idle, playback, Raw Input dispatch, statistics refresh, rendering, and hotplug before and after the work.

## Verification

- Test catalog/settings validation, cursor application and rollback, recovery, panic controls, device enumeration, privacy sanitization, safe actions, suppression limitations, rendering states, click channels, profiles, themes, DPI, monitors, Reduced Motion, battery, RDP, fullscreen, and unavailable GPU acceleration.
- Recompile after every source or cursor-asset change.
- Run `dotnet build KeyClick.sln -c Release`, `dotnet test KeyClick.sln -c Release`, privacy and vulnerability checks, and x64/ARM64 Setup and Portable packaging for 1.6.0.
- Do not commit, push, publish, or create a GitHub release unless separately requested.

## Platform assumptions

- Native speed/acceleration/trails/shadow are per-user Windows settings, not per physical device; hardware DPI remains vendor-controlled.
- Raw Input may not list Remote Desktop pointer devices.
- Safe actions can remain per-device, while global suppression cannot reliably identify a physical source device.
- Precision Touchpad multi-finger remapping remains deferred because the available API is experimental and foreground-limited.
- Uninstall restores the pre-KeyClick cursor scheme before managed cursor assets are removed.
