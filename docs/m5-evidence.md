# M5 — Timing workstation

Implemented on `rewrite/codex`, 2026-09-27. Scope was explicitly expanded to bring Timy 2/3 USB, MT1 USB/serial and ALGE Results adapters forward from M6. Software implementation is ready for operator testing; physical-device acceptance is still open. M7 publication has not started.

## Delivered

- Competition/run Timing dropdown, compact start-list/results and observations, F5/F6 arming, several athletes on course, assignments, ignored input, classifications, elapsed corrections and append-only undo/history. Active short name/codex/run remain visible. Live row updates retain selection and edited correction text.
- Independent transport → bounded queue → durable raw packet → decoder → deterministic timing projection. SQLite migration 7 adds captures, exact raw blobs and audit; triggers reject raw/audit UPDATE and DELETE. Capture options include date/channels/firmware/operator/calculation version. Corrections and their before/after audit are one transaction, not separate writes.
- A capture lease excludes competing capture and ordinary event/registration writes. Closing/switching waits for received packets; a write failure retains the current packet, exposes a storage alarm and prevents close. Retry handles an uncertain raw commit idempotently. Reopening an unclean session adds an unresolved recovery notice.
- Timy native USB uses the optional ALGE library in a small isolated x64 Framework 4.8 executable; the app/core remain .NET 10. This is required by the vendor's mixed-mode library. No script-policy or security-setting change, vendor binary redistribution, clock reset or device-memory clearing is used.
- MT1 serial retries connection loss. ALGE Results uses documented login/Timekeeper role, authenticated read-only GETs, 2-second polling, fixed-bound pagination/history recovery, periodic rechecks, rate-limit backoff and token reauthorization. It does not use STOMP. Credentials use Windows Credential Manager only when explicitly remembered; raw records contain no login responses/tokens.
- Run/combined times, hundredth truncation, ties and DNS/DNF/DSQ/NPS. Run 2 consumes classified captured results, preserves bibs and refuses stale source versions before starting. Earlier external-result start lists still work. No result publication, penalties, rerun rules or automatic backup-time conversion.

## Verified

- Both legacy and rewrite Release solutions build. Legacy suite: **241 passed**, zero failed/skipped. Rewrite suite: **53 passed** (43 core/integration, 10 desktop), zero failed/skipped at the final run recorded below. No existing assertion was weakened; migration-count expectations moved from 6 to 7.
- `TimingRulesTests`: fragmented/multiple ASCII lines, sequential versus explicit bibs, keyboard marker, unknown/incomplete lines, midnight plus late packets, clock resets, duplicate effect, missing/multiple/cross-clock impulses, subtract-before-truncate, tie ranks, statuses, integer MT1 Unix timestamps, semantic JSON duplicates/server edits and combined totals.
- `TimingStorageTests`: actual migrated SQLite capture, byte-exact backup/reopen/replay, immutable history, correction/undo, invalid correction rejected without a write, injected uncertain disk commit and retry, competing capture/writer rejection, 100-packet queued drain across a file switch, M4 upgrade backup, unclean-session recovery and stale Run 2 rejection.
- `AlgeNetworkTests`: synthetic HTTP login/role/token, paging with identical bounds, original response bytes, expired-token retry, malformed body preservation and sanitized rejected-login errors. These do not assert live service access or physical MT1 behavior.
- Rendered Avalonia workflow: actual menu/button/F5 interactions, simulation, two athletes on course, unmatched finish assignment, correction/undo, classifications, disconnect, Run 2 creation and timing, combined total, reopening. Rendered at 1280×800 and 980×680; inspected device settings, active timing, correction history and Run 2. Fixed compact numeric-header clipping. Rendered evidence and synthetic files are outside Git in the local `outputs/m5-visual` folder.
- Native Windows launch via `dotnet <desktop.dll>` succeeded. Opened a synthetic transferred event through the Windows file dialog, chose Timing → SL1 → Run 1, and inspected the actual 1280-wide timing view and collapsed device settings.
- Optional ALGE SDK was installed outside the repository. The compiled USB helper loaded the actual vendor x64 library, reported waiting for the device and exited cleanly (code 0) after STOP. The earlier script-based experiment was removed because local script execution is disabled; no policy was changed.

## Not verified / remaining acceptance

- Connected Timy reports `CM_PROB_FAILED_INSTALL` (missing USB driver). This session is not elevated. The signed official driver installer was identified/downloaded, but was not installed; the SDK alone does not replace it. No physical start/finish pulses, device clock/firmware agreement, unplug/reconnect or tape comparison has been verified.
- No MT1 hardware or authorized ALGE Results credentials were available. Live cloud behavior, serial ports and service edits/backfill require rehearsal. Only the documented contracts and synthetic responses were tested.
- Unclean restart is tested at the persisted boundary, and write failure is fault-injected. Actual power loss, physical disk-full/unplug, long soak, high DPI/Linux, second-machine transfer and sustained driver/UI stalls remain M6/M8 checks. The 100-packet test is a regression exercise, not a measured capacity guarantee.
- Data not yet delivered/durably committed still depends on hardware memory/independent backup. The queue is bounded; prolonged faults can exhaust upstream buffers. A driver acknowledgement-after-commit protocol is not available in this SDK and is not claimed.

Commands: `dotnet build rewrite/OpenSkiTime.Rewrite.slnx -c Release`; `dotnet test rewrite/OpenSkiTime.Rewrite.slnx -c Release`; equivalent build/test of `OpenSkiTime.slnx`. Set `OPENSKITIME_M5_VISUAL_DIR` outside the repository to retain rendered synthetic desktop evidence. See [operator instructions and primary sources](timing.md).

EF reports no pending model changes. NuGet's current vulnerability audit, including transitive dependencies, reports no vulnerable packages for the rewrite solution. The final diff contains no device binaries, credentials, downloaded athlete data or event files.
