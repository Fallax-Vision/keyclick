# KeyClick Privacy Boundary

KeyClick is an offline-first local application. Its statistics feature exists to
show the user aggregate activity insights on their own device, not to inspect or
transmit what they do.

## What is collected locally

After a one-time disclosure is confirmed, KeyClick may store aggregate UTC
hourly counts for physical keyboard scan codes, pointer button releases, wheel
detents, device family, active duration, and compact peak-rate summaries. An
optional local breakdown groups total keyboard, pointer, and scrolling counts by
application. It stores only a source-salted application ID and executable file
name—never the executable path or per-key activity within an application.
Keyboard, pointer, and scrolling capture have independent controls. Aggregate
statistics remain local forever until the user explicitly deletes them.
Existing installations are shown the revised disclosure once before the new
per-application grouping can collect anything.

KeyClick never stores typed characters, typing order, individual-event history,
individual-event timestamps, raw application paths in statistics, window titles,
screen or UI content, clipboard content, or telemetry. Physical scan-code labels
are resolved only while drawing the local UI.

Typing Challenges are explicitly started by the user. Challenge responses are never stored.
The active response exists only in memory and is discarded when
the run ends. Optional history contains aggregate metrics and five-second
elapsed-time samples, never characters or input order. A custom source prompt is
persisted only after the user selects **Save locally** and confirms a warning;
saved prompts are separate from responses and can be deleted independently.

Sounds and aggregate capture run only while the KeyClick process is running.
Closing the app window or choosing **Exit** from the tray disposes Raw Input and
audio, flushes pending local aggregates, and stops collection.

## Network boundary

Keyboard, mouse, and per-application aggregate statistics are never transmitted
to the internet, the updater, the local named pipe, telemetry, crash reporting,
advertisements, cloud sync, wellness services, or any other destination.
Per-application details are also excluded from CSV and profile exports so they
cannot leave through a KeyClick export surface. Other profiles and CSV files are
created only at a local path selected by the user and are never uploaded by
KeyClick.

Typing challenge responses, aggregate results, samples, streaks, prompt IDs,
and saved prompts are never transmitted by KeyClick. Aggregate challenge history
may be written only to a local CSV or explicitly selected local profile. Saved
prompts require password protection before local profile export. Neither surface
is uploaded by KeyClick.

Network access is disabled by default. There are no automatic update checks or
background pings. A network client is created only after a manual update action,
permits HTTPS GET requests to fixed GitHub release hosts, and verifies the
downloaded artifact against its published SHA-256 checksum.

## Enforcement

The required **Privacy Boundary** CI check rejects network APIs outside the
isolated updater, update calls from startup/background services, updater APIs
that accept input/statistics/challenge payloads, telemetry dependencies, and removal of
these documented invariants. Privacy-critical files require approval from
`@askasjeremy` through CODEOWNERS.
