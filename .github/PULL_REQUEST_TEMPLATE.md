## Summary

Describe the user-visible change and why it is needed.

## Verification

- [ ] `dotnet build KeyClick.sln -c Release`
- [ ] `dotnet test KeyClick.sln -c Release`
- [ ] Relevant x64/ARM64 or clean-user smoke tests completed

## Safety and quality

- [ ] Playback still occurs only on release; pointer movement does not play sounds
- [ ] No typed characters or key-event history are persisted/logged
- [ ] No new polling, elevation, telemetry, or automatic network activity
- [ ] UI was checked in Light and Dark/System modes
- [ ] Database/schema changes and migration behavior are documented
