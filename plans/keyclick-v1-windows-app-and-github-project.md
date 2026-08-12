# KeyClick v1 — Windows App and GitHub Project

## Summary

Build KeyClick as a native Windows 11 application using C#, WPF, [.NET 10 LTS](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview), SQLite, Win32 Raw Input, and XAudio2. This avoids Electron/WebView overhead while supporting modern Windows UI, low-latency sound effects, system tray operation, and global input.

Publish two architecture-specific single-file distributions—`KeyClick-Windows-x64.exe` and `KeyClick-Windows-arm64.exe`—because [.NET single-file applications are architecture-specific](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview). Linux and macOS implementations are deferred, but the repository will reserve native-platform app folders and share protocols, pack specifications, assets, and fixtures.

## Product and UX

- Use a restrained Windows 11 design derived from the reference: black/neutral card surfaces, compact rows, rounded corners, Segoe UI Variable, green accent, clear focus states, and no drop shadows or hover translations.
- Provide Home, Sound Packs, Keyboard & Pointer Mappings, Shortcuts, Settings, Integrations, and About/Updates views.
- Home exposes the enabled state, active pack, master volume, keyboard/pointer toggles, output device, and quick access to per-app muting.
- Support Light, Dark, and System themes, reacting immediately to Windows theme changes.
- Ship ten original royalty-free synthesized packs, plus three recorded packs:
  - Keyboard-focused: Clicky Switch, Tactile Switch, Linear Switch, Buckling Spring, Silent Switch.
  - Recorded: Cream Keys, Bright Mechanical, Compact Mech Tap.
  - Balanced: Crisp Mechanical, Soft Thock, Classic Typewriter, Minimal Tap, Digital Pulse.
- Each pack contains sample pools with no immediate repetition for letters, numbers/symbols, punctuation, modifiers, navigation, functions/media, numpad, locks, Space, Enter, editing/destructive keys, pointer buttons, wheels, and result cues.
- Built-in mappings remain immutable. A key-specific override is stored as a separate per-pack layer; disabling or removing it restores the built-in mapping and volume.
- Support Base, Shift, and AltGr variants using the active Windows keyboard layout. NumLock, CapsLock, and ScrollLock receive separate enabled/disabled samples.
- Enter may use a positive role cue and Delete/Backspace a negative role cue, but KeyClick will not claim to know another app’s outcome.
- Mouse and trackpad sounds occur on left/right/middle/X1/X2 release and accumulated vertical/horizontal wheel detents. Pointer movement never sounds.
- Trackpad and external mouse mappings are independently configurable by category and button. Unknown devices can be manually classified when a driver does not expose reliable device identity.
- Every key, button, category, or device can be enabled, muted, restored, previewed, or assigned imported WAV, MP3, or OGG samples.
- Effective volume is `master × category × pack × group × input override`, with lower layers supporting “inherit.” Sliders update playback immediately and persist to SQLite with a short debounce.
- Settings include:
  - Display name, defaulting to KeyClick. It changes the window/tray label, not the executable, AppData identity, or update channel.
  - Disable all sounds, keyboard sounds, pointer sounds, wheel sounds, and result sounds.
  - Launch at startup, start minimized, minimize/close to tray, and fullscreen pause.
  - Theme, output device, all volume layers, active pack, app exclusions, input-device classification, and shortcut sequence timeout.
  - Integration API permissions, manual update check, backup/restore, reset options, reduced motion, diagnostics, and log cleanup.
- Defaults: sounds enabled, System theme, 70% master volume, launch at startup off, close to tray on, startup runs hidden only when enabled, system-default audio output, no app exclusions, and no background update checks.

## Technical Implementation and Interfaces

- Use a message-only Win32 window registered with `RIDEV_INPUTSINK`. Raw Input supplies background input, key make/break state, physical scan codes, and source-device identity; key sounds trigger only on `RI_KEY_BREAK`. Mouse button-up flags and wheel deltas provide equivalent pointer events. [Microsoft Raw Input overview](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input), [RAWKEYBOARD](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawkeyboard), [RAWMOUSE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawmouse).
- Keep the input callback allocation-free and non-blocking. It writes compact events to a bounded audio queue; no key names, characters, or activity history are persisted.
- Use XAudio2 2.9 with predecoded 48 kHz PCM samples, a reusable 32-voice pool, and oldest-voice stealing only when saturated. XAudio2 provides low-latency dynamic-buffer playback and can stop processing when idle. [Microsoft XAudio2 overview](https://learn.microsoft.com/en-us/windows/win32/xaudio2/xaudio2-introduction).
- Load the active pack asynchronously and swap its memory cache atomically. Never read SQLite or decode media on the input/audio path.
- Imported files are limited to 20 MB and five seconds, validated by decoding rather than extension alone, deduplicated by SHA-256, optionally peak-normalized, previewed, then saved as internal PCM WAV under `%LOCALAPPDATA%\KeyClick\media\sounds`.
- Use SQLite WAL mode and idempotent migrations. Store settings, packs, sound pools, group mappings, per-input overrides, shortcuts, app rules, device profiles, integration clients, and migration state. Audio remains in files rather than database blobs.
- Resolve sound mappings in this order: master/category enabled state → app/device rule → exact per-pack input/variant override → per-pack group/variant mapping → built-in pack fallback.
- Support shortcut chords and two-step sequences:
  - Global chords use `RegisterHotKey`; registration failure retains the previous binding.
  - Global sequences use the existing Raw Input stream with a default 1.2-second timeout.
  - App shortcuts run only while the KeyClick window is focused.
  - Duplicate or overlapping app/global bindings are rejected, and shortcuts never suppress keystrokes reaching the foreground app.
  - Defaults: show/hide `Ctrl+Alt+K`, toggle sounds `Ctrl+Alt+M`, previous pack `Ctrl+Alt+PageUp`, next pack `Ctrl+Alt+PageDown`.
- Publish a versioned, current-user-only named pipe such as `KeyClick.ActionResult.<SID>.v1`. The public message is length-bounded JSON:
  - Request: `version`, `type: "action-result"`, `outcome: success|failure|authorized|blocked`, optional `inputId` and `actionId`, and `playResultSound`.
  - Response: `version`, `accepted`, and an enumerated error when rejected.
  - The physical key sound remains independent. A result cue plays only when an allow-listed client sends `playResultSound: true`.
  - Messages cannot reference arbitrary files or audio paths; clients select only semantic outcome slots. Limit messages to 4 KB and 20 accepted events per second.
- Keep shared pack manifests, integration JSON schemas, stable input identifiers, and fixtures platform-neutral under `shared/specs`; the initial native application lives under `apps/windows`.

## Portable Packaging, GitHub, and Safety

- Install the official x64 .NET 10 SDK, then pin its feature band in `global.json`. Use the existing Windows 11 SDK and Visual Studio installation.
- Build a console-free bootstrap executable per architecture. It extracts versioned application payloads to `%LOCALAPPDATA%\KeyClick\app-v<version>` while preserving `data`, `media`, `logs`, `backups`, and user state.
- First run creates SQLite data, bundled packs, Desktop/Start Menu shortcuts, and a stable AppData launcher. Startup registration points to the stable launcher rather than the originally downloaded file.
- Enforce single-instance behavior. Updates close only the matching KeyClick process, replace versioned code using version and payload hash, preserve user data, and reopen only when appropriate.
- Provide uninstall choices to either preserve or delete database, media, backups, and logs. Provide manual backup/restore before destructive resets.
- Manual update checks query public releases from `Fallax-Vision/keyclick` only after the user clicks the button, select the matching architecture, verify the release checksum, and require confirmation before replacement. Authenticode signing steps remain conditional until certificate secrets are supplied.
- Initialize a public MIT repository at `Fallax-Vision/keyclick` with `main`, README, LICENSE, SECURITY, CONTRIBUTING, issue/PR templates, `.editorconfig`, .NET `.gitignore`, Dependabot, CodeQL, and Windows CI.
- CI builds and tests natively on x64 and `windows-11-arm`; public repositories have free Windows ARM64 hosted runners. [GitHub-hosted runners](https://docs.github.com/en/actions/reference/runners/github-hosted-runners).
- Tag-based release workflow produces both single-file executables, checksums, SBOMs, and attestations, but releases remain draft until explicitly approved.
- Configure repository-level protection for `main` after the initial push: required x64/ARM64 CI, no force pushes, and linear history. Do not modify personal-account or `Fallax-Vision` organization settings.
- Before execution, separately confirm the exact public repository metadata, staged initial files, initial commit, push, and any release publication, as required for external/public GitHub actions.

## Verification and Assumptions

- Unit-test mapping precedence, reversible overrides, mute/inherit behavior, volume multiplication, shuffle pools, Shift/AltGr and lock variants, wheel accumulation, shortcut conflicts/timeouts, SQLite migrations, import validation, IPC validation/rate limits, and update asset selection.
- Integration-test theme switching, tray lifecycle, single instance, startup registration, output-device changes, app exclusions, backup/restore, same-version payload refresh, and uninstall preservation/deletion choices.
- Run native CI tests on both Windows x64 and ARM64. Perform clean-user smoke tests with no developer runtime installed.
- Verify key-down and repeat produce no sound; one sound is submitted on physical key-up. Target key-up-to-audio-submit p95 ≤5 ms and wired-output audible latency ≤30 ms under rapid typing; Bluetooth/device-driver latency is outside application control.
- Performance acceptance: no polling, no database writes per input, idle tray CPU below 0.2% on a typical Windows 11 PC, hidden working set below 120 MB, stable memory during a five-minute rapid-input test, and no lost events below the 32-voice limit.
- Verify 1000 Hz pointer movement causes no playback or database activity, while button releases and accumulated wheel detents remain responsive.
- Confirm logs, backups, SQLite, and diagnostics contain no typed characters or key-event history.
- Raw Input device separation is best effort: some touchpad drivers merge events into a synthetic mouse. Fn, secure-desktop, and certain OEM keys may not be exposed and will be documented rather than worked around through elevation.
- Integrations are disabled until the user enables the API and allow-lists a client. KeyClick never elevates itself, monitors UI content, infers third-party outcomes, sends telemetry, or accesses the network except for a user-triggered update check.
- Linux and macOS application implementations, cloud sync, automatic updates, UI-outcome inference, gestures, pointer-movement sounds, and a full distributable pack marketplace are outside Windows v1.

## Post-v1 Continuation: Local Statistics, Wellness, Rotation, and Distributions

The approved continuation is specified in
[`keyclick-statistics-wellness-rotation-privacy-and-distributions.md`](keyclick-statistics-wellness-rotation-privacy-and-distributions.md).
It extends the same native Raw Input, SQLite WAL, XAudio2, offline-first, and
current-user-only architecture with aggregate statistics and wellness features.

The original release-only keyboard playback rule is amended: users may choose
one sound on the first physical key-down or on key-up; typematic repeats never
play sounds. Pointer playback remains button-up/wheel-detent only. Statistics
may store aggregate physical-key and pointer counters in time buckets, but never
typed characters, ordered input history, individual event timestamps,
foreground-app activity, or UI content. Statistics never enter a network API.
All application network activity remains disabled except an explicit manual
update check and checksum-verified download initiated by the user.
Closing the application window or choosing Exit from the tray terminates the
process and therefore stops sound playback and all input statistics capture;
only minimizing may keep the running app in the tray.
