## Summary

Describe the user-visible change and why it is needed.

## Verification

- [ ] `dotnet build KeyClick.sln -c Release`
- [ ] `dotnet test KeyClick.sln -c Release`
- [ ] Relevant x64/ARM64 or clean-user smoke tests completed

## Safety and quality

- [ ] Keyboard playback obeys first-key-down/key-up mode without repeats; shortcuts stay release-based; pointer movement does nothing
- [ ] No typed characters, input order/history, per-event timestamps, raw statistic application paths, app-specific key counts, or UI content are persisted/logged
- [ ] Keyboard/mouse/per-application statistics cannot reach updater, CSV/profile export, or network APIs
- [ ] Typing challenge responses are memory-only; aggregate challenge data cannot reach updater/network APIs; saved prompt profile export requires password protection
- [ ] `./scripts/Test-PrivacyBoundary.ps1` passes; no polling, elevation, telemetry, or automatic network activity was added
- [ ] UI was checked in Light and Dark/System modes
- [ ] Database/schema changes and migration behavior are documented

## Privacy Boundary

- [ ] Networking remains isolated to manual HTTPS GET update operations in `KeyClick.Updater`
- [ ] The updater exposes no body/payload API and references no input/statistics/profile type
- [ ] The updater references no typing-challenge type and accepts no challenge payload
- [ ] Aggregate statistics remain local forever until explicit deletion
- [ ] Privacy-critical changes have `@askasjeremy` CODEOWNERS approval
