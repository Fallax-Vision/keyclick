# KeyClick Privacy Boundary

KeyClick is an offline-first local application. Its statistics feature exists to
show the user aggregate activity insights on their own device, not to inspect or
transmit what they do.

## What is collected locally

After a one-time disclosure is confirmed, KeyClick may store aggregate UTC
hourly counts for physical keyboard scan codes, pointer button releases, wheel
detents, device family, active duration, and compact peak-rate summaries.
Keyboard, pointer, and scrolling capture have independent controls. Aggregate
statistics remain local forever until the user explicitly deletes them.

KeyClick never stores typed characters, typing order, individual-event history,
individual-event timestamps, foreground applications, window titles, screen or
UI content, clipboard content, or telemetry. Physical scan-code labels are
resolved only while drawing the local UI.

Sounds and aggregate capture run only while the KeyClick process is running.
Closing the app window or choosing **Exit** from the tray disposes Raw Input and
audio, flushes pending local aggregates, and stops collection.

## Network boundary

Keyboard and mouse statistics are never transmitted to the internet, the
updater, the local named pipe, telemetry, crash reporting, advertisements,
cloud sync, wellness services, or any other destination. Profiles and CSV files
are created only at a local path selected by the user and are never uploaded by
KeyClick.

Network access is disabled by default. There are no automatic update checks or
background pings. A network client is created only after a manual update action,
permits HTTPS GET requests to fixed GitHub release hosts, and verifies the
downloaded artifact against its published SHA-256 checksum.

## Enforcement

The required **Privacy Boundary** CI check rejects network APIs outside the
isolated updater, update calls from startup/background services, updater APIs
that accept input/statistics payloads, telemetry dependencies, and removal of
these documented invariants. Privacy-critical files require approval from
`@askasjeremy` through CODEOWNERS.
