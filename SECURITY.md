# Security Policy

## Supported versions

Security fixes are applied to the latest published KeyClick release. Pre-release builds and unreleased branches receive best-effort fixes.

## Reporting a vulnerability

Use GitHub's private vulnerability reporting feature on the `Fallax-Vision/keyclick` repository. Do not open a public issue for suspected vulnerabilities involving input capture, named-pipe authorization, update integrity, path traversal, or local-data disclosure.

Include the affected version/architecture, reproduction steps, expected impact, and any relevant logs with sensitive paths removed. KeyClick never needs typed text or a keystroke transcript to investigate a report.

## Security boundaries

KeyClick runs without elevation. Its integration pipe is restricted to the current user and an explicit executable allow-list and cannot access statistics. Update checks are manual, user-triggered, isolated in `KeyClick.Updater`, limited to HTTPS GETs on fixed GitHub hosts, and release assets must match a published SHA-256 checksum before replacement.

The required **Privacy Boundary** check rejects production networking outside the updater, automatic/background update calls, telemetry, updater payload/body APIs, per-application details in export surfaces, and dependencies from the updater to input, statistics, wellness, typing-challenge, or profile types. Keyboard, mouse, per-application, and typing-challenge data are never transmitted. Changes to the guard, updater, statistics/challenge storage, privacy workflows, and privacy policy require CODEOWNERS approval from `@askasjeremy`.

Aggregate statistics never contain typed characters, typing order, per-event timestamps, raw application paths, app-specific physical-key counts, or UI content. Application totals use only a local source-salted ID and filename and are excluded from CSV/profile exports. Report any path that can weaken this boundary as a security vulnerability. Authenticode signing will be enabled only when protected certificate secrets are available.

Typing challenge responses exist only in process memory. Persisted challenge
results contain aggregate metrics and five-second samples only. Explicitly saved
source prompts are isolated from result records and require password protection
for local profile export. Challenge contracts must never be referenced by the
manual updater.
