## Summary

Describe the user-visible change and why it is needed.

## Verification

- [ ] `dotnet build KeyClick.sln -c Release`
- [ ] `dotnet test KeyClick.sln -c Release`
- [ ] Relevant x64/ARM64 or clean-user smoke tests completed

## Safety and quality

- [ ] Keyboard playback obeys first-key-down/key-up mode without repeats; shortcuts stay release-based; pointer movement does nothing
- [ ] No typed characters, input order/history, per-event timestamps, foreground applications, or UI content are persisted/logged
- [ ] Keyboard/mouse statistics cannot reach updater or network APIs
- [ ] `./scripts/Test-PrivacyBoundary.ps1` passes; no polling, elevation, telemetry, or automatic network activity was added
- [ ] UI was checked in Light and Dark/System modes
- [ ] Database/schema changes and migration behavior are documented

## Privacy Boundary

- [ ] Networking remains isolated to manual HTTPS GET update operations in `KeyClick.Updater`
- [ ] The updater exposes no body/payload API and references no input/statistics/profile type
- [ ] Aggregate statistics remain local forever until explicit deletion
- [ ] Privacy-critical changes have `@askasjeremy` CODEOWNERS approval
