# M6 — ALGE hardware acceptance (in progress)

## Timy3 USB, 2026-09-27

- Operator installed the signed ALGE driver 2.80.0.0. Windows reports **Timy3 USB Device**, status OK, problem code 0 (`VID_0C4A`, `PID_088B`). PC Timer mode; firmware version has not yet been recorded.
- Native x64 helper connected to SDK device ID 1 and received clock messages plus real device-keyboard C0M/C1M pulses. The tested pair was 5.99 seconds apart with two source decimals. This verifies USB reception of manual button events, not the resolution/accuracy of external electrical inputs.
- Replayed 238 captured SDK messages through the application capture/decoder/SQLite path in a separate synthetic event, assigned the two impulses, closed/reopened and compared every stored packet byte-for-byte. Both manual flags and two-digit precision survived; the result correctly remains **Review**. No unknown input was produced. A separate live test uses the same application capture service and TimyUsbSource.
- Test logs, temporary acceptance harness and event files are outside Git under local `outputs/timy-usb-test`. Only synthetic SDK-contract fixtures are committed.

## Defects found and corrected

1. `TimyUsbDevice.Id` is a public **field**, not a property. The original reflection accessor failed with NullReferenceException on the first actual connection. The accessor now matches the SDK; the exact helper event code is compiled into a regression test with a matching synthetic contract.
2. The downloaded ALGE x64 SDK's `BytesReceived.Data` contains a defective array copy; its untrimmed `Text` field contains the received ASCII chunk. Inspection of that SDK's managed method body confirmed the faulty byte-copy implementation. SDK SHA-256: `027f4cc8a68a2e39a748735d279a3319b7583eb1b681f65ff9fe020f8510e4b3`.
3. The helper now sends a versioned envelope containing **both unchanged SDK fields**. `alge-timy-sdk/v1` is stored before interpretation; decoding uses the preserved ASCII text without trimming CRs or packet padding. Non-ASCII/malformed envelopes require review and break partial-line assembly. Trailing USB whitespace alone no longer creates a false incomplete-packet warning.

This records the SDK boundary, not a claim that the defective SDK exposes a trustworthy original binary USB buffer. For valid ASCII, the text field retains the native message characters; a future direct/native adapter must preserve its own original input boundary. Unknown content and the defective byte field remain available for inspection.

## Remaining acceptance

Physical unplug/reconnect with new impulses, start-gate/photocell electrical C0/C1 pulses, device firmware and tape comparison, sustained capture, fault/power recovery, Timy2, multiple devices and MT1 are still open. This partial rehearsal does not close M6 or establish FIS race readiness.

Rewrite Release build succeeds with zero warnings/errors. **69 tests pass** (59 core/integration, 10 desktop), including the SDK field contract, unchanged byte/text envelope, fragmented time messages, padding, and recovery from invalid chunks. No vendor binary or physical-device trace is committed.
