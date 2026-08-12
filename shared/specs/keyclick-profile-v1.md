# KeyClick profile v1

`.keyclickprofile` is a local-only binary envelope containing a ZIP payload. The
header is `KCPROF1\0`, followed by a one-byte encryption flag. Plain profiles
place the ZIP bytes immediately after the flag. Password-protected profiles use
AES-256-GCM with PBKDF2-HMAC-SHA256 (200,000 iterations), a random 16-byte salt,
12-byte nonce, and 16-byte authentication tag.

The ZIP contains `manifest.json` plus selected settings/mappings, custom media,
aggregate statistics, and wellness achievements. Every payload entry is SHA-256
hashed in the manifest. Paths, hashes, expanded sizes, entry counts, media types,
and schema version are validated before merge.

Machine-local fields are never transferred: startup registration, audio-device
IDs, sound/statistics executable exclusions, integration allow-lists, data-root
mode, update state, and device paths/classifications. Statistics merge
idempotently by source ID, UTC bucket key, and revision. Profiles are never
uploaded by KeyClick.
