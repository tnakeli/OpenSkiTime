# M6 — ALGE hardware acceptance (in progress)

## Timy3 USB, 2026-09-27

- Operator installed the signed ALGE driver 2.80.0.0. Windows reports **Timy3 USB Device**, status OK, problem code 0 (`VID_0C4A`, `PID_088B`). PC Timer mode; firmware version has not yet been recorded.
- Native x64 helper connected to SDK device ID 1 and received clock messages plus real device-keyboard C0M/C1M pulses. The tested pair was 5.99 seconds apart with two source decimals. This verifies USB reception of manual button events, not the resolution/accuracy of external electrical inputs.
- Replayed 238 captured SDK messages through the application capture/decoder/SQLite path in a separate synthetic event, assigned the two impulses, closed/reopened and compared every stored packet byte-for-byte. Both manual flags and two-digit precision survived; the result correctly remains **Review**. No unknown input was produced.
- After fixing reconnect selection, exercised a live `TimyUsbSource` → application capture → SQLite session (18:02:11–18:03:13 UTC). Operator unplugged USB for approximately five seconds, reconnected the same port and pressed START/STOP. Reception resumed from SDK ID 1 to ID 2 with distinct streams. C0M `21:02:17.54` and C1M `21:02:22.26` were received after reconnect: **4.72 seconds** apart. All 61 packets (60 unchanged SDK envelopes plus one disconnect notice) survived close/reopen byte-for-byte; SQLite integrity check returned `ok`, with a clean capture stop. Both impulses retained manual flags/two-decimal precision; the result remains **Review**. The sole invalid/review observation is the expected USB interruption notice. No desktop interaction was exercised by this harness.
- Test logs, temporary acceptance harness and event files are outside Git under local `outputs/timy-usb-test`. Only synthetic SDK-contract fixtures are committed.

## Defects found and corrected

1. `TimyUsbDevice.Id` is a public **field**, not a property. The original reflection accessor failed with NullReferenceException on the first actual connection. The accessor now matches the SDK; the exact helper event code is compiled into a regression test with a matching synthetic contract.
2. The downloaded ALGE x64 SDK's `BytesReceived.Data` contains a defective array copy; its untrimmed `Text` field contains the received ASCII chunk. Inspection of that SDK's managed method body confirmed the faulty byte-copy implementation. SDK SHA-256: `027f4cc8a68a2e39a748735d279a3319b7583eb1b681f65ff9fe020f8510e4b3`.
3. The helper now sends a versioned envelope containing **both unchanged SDK fields**. `alge-timy-sdk/v1` is stored before interpretation; decoding uses the preserved ASCII text without trimming CRs or packet padding. Non-ASCII/malformed envelopes require review and break partial-line assembly. Trailing USB whitespace alone no longer creates a false incomplete-packet warning.
4. Physical unplug/replug changed SDK ID 1 to a new ID. The original helper kept ID 1 selected and discarded the reconnected device's input as an additional device. Automatic selection now releases its ID on the selected device's disconnect and accepts the next newly connected device; every connection starts a separate clock context. Explicit ID selection remains strict. Synthetic tests cover both modes and ensure an unrelated disconnect does not release the active selection. The SDK does not expose a persistent physical identity through this public contract.

This records the SDK boundary, not a claim that the defective SDK exposes a trustworthy original binary USB buffer. For valid ASCII, the text field retains the native message characters; a future direct/native adapter must preserve its own original input boundary. Unknown content and the defective byte field remain available for inspection.

## Remaining acceptance

Start-gate/photocell electrical C0/C1 pulses, device firmware and tape comparison, sustained capture, fault/power recovery, Timy2, multiple devices and MT1 are still open. One Timy3 unplug/reconnect with subsequent manual impulses is verified; recovery of impulses occurring while disconnected is not. This partial rehearsal does not close M6 or establish FIS race readiness.

Rewrite Release build succeeds with zero warnings/errors. **71 tests pass** (61 core/integration, 10 desktop), including reconnect selection, the SDK field contract, unchanged byte/text envelope, fragmented time messages, padding, and recovery from invalid chunks. No vendor binary or physical-device trace is committed.
