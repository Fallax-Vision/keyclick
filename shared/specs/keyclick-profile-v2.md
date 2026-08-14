# KeyClick profile v2

Version 2 keeps the `KCPROF1\0` local envelope so readers can identify both v1
and v2 files before opening the manifest. It adds two optional sections:

- `challenge-history` contains aggregate typing challenge results, five-second
  elapsed-time samples, and streak achievement snapshots. It never contains a
  typed response or ordered key events.
- `challenge-prompts` contains only source prompts that the user explicitly
  chose to save. Selecting this section requires AES-256-GCM password
  protection.

Both sections are disabled by default. Challenge history merges idempotently by
source/result ID and revision. Saved prompt conflicts keep the newer revision.
KeyClick imports v1 profiles unchanged and never uploads profile files.
