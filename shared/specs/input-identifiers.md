# Stable input identifiers

KeyClick v1 serializes an input as `kind:device-family:code:extended`, for example `KeyboardKey:Keyboard:30:0` or `PointerButton:Trackpad:1:0`.

- `kind`: `KeyboardKey`, `PointerButton`, or `Wheel`.
- `device-family`: `Keyboard`, `ExternalMouse`, `Trackpad`, or `UnknownPointer`.
- `code`: the physical scan code for keyboard input; semantic button/wheel code for pointer input.
- `extended`: `1` for an E0/E1 extended keyboard scan code, otherwise `0`.

Device-instance hashes may be stored separately for per-device profiles. They are deliberately not part of the cross-platform stable identifier because hardware paths are platform-specific and can change after driver reinstall.
