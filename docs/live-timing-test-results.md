# Live timing verification — 2026-10-02

## Separate control panel verification — 2026-10-03

Windows and .NET 10 verification after moving publisher ownership into `OpenSkiTime.LiveTiming.ControlPanel.exe`:

- Desktop workflow, layout and connection tests: **42 passed**, zero failures or skips.
- Live timing process/protocol tests: **6 passed**, zero failures or skips.
- Desktop build: passed with zero warnings and errors. Desktop publish: passed; verified the panel executable/runtime configuration, its nested worker/server assemblies and server browser assets in the published package.
- Rendered the separate control panel at 940 × 790 and 760 × 560, including both columns scrolled to the bottom. Verified channel command bindings, readable TCP port, compact three-color timing strip, full hover information and no horizontal overflow. Synthetic rendered images remain outside Git.
- The desktop workflow starts the actual named panel executable and verifies its own window handle/title. Reopening the panel button retains its process ID. Terminating only the panel process closes both publisher workers and its local server; timing capture subsequently commits a fourth raw impulse. Reopening creates a new panel and restores the Cloud session with the current on-course state. Closing the panel's main window also exits the process while timing remains connected.
- A real private-pipe connection test exercises the panel's Start command requesting current settings and a fresh snapshot before Local publishing. A stale snapshot cannot overwrite the newer result. Parent-pipe disconnection stops the worker.

Linux forced-exit cleanup, physical-device acceptance and external cloud/FIS deployment were not repeated for this UI/process change. The earlier results below describe their original verification scope.

All race data used here was synthetic. No Azure resources or DNS were deployed. Supplied FIS passwords, downloaded PDFs, protocol logs and browser screenshots remain outside Git.

## Automated acceptance

Windows, .NET SDK 10.0.401 / .NET 10, independent server/worker processes, Avalonia headless UI and real Chromium. Final verification used the `LiveTimingVerification` project configuration to avoid overwriting the concurrently running Release desktop application's files.

| Check | Result |
|---|---|
| Domain/application/device/persistence regression tests | 141 passed; zero failures/skips. |
| Desktop workflow/layout tests | 32 passed; zero failures/skips. Includes live Local/Cloud controls and authoritative timing capture during a cloud worker crash. |
| Live timing process/protocol E2E tests | 6 passed; zero failures/skips. Real TCP sockets, real HTTPS test endpoint, real server processes, named pipes and SignalR WebSocket. |
| Chromium browser E2E | Passed at a 390-pixel phone viewport. External origins blocked; local assets, live start/intermediate/finish/DNS/DNF/DSQ, pause/resume, silent (half-open) socket detection, server restart/self-reconnect with last results retained until full resync, deletion, no page refreshes or browser errors. |
| Synthetic harness build | Passed, zero warnings/errors. |
| Desktop publish | Passed; packaged worker, server and `wwwroot/live.js` verified. |
| Linux container image | .NET SDK `PublishContainer` produced an image archive from `mcr.microsoft.com/dotnet/aspnet:10.0`. No Docker daemon was available to execute it. |
| Deployment template | Bicep compiled successfully to ARM JSON locally. No deployment executed. |
| Whitespace validation | `git diff --check` passed. |

The three .NET test projects total **179 passing tests**. They were executed individually with `dotnet test <project.csproj> -c LiveTimingVerification`; normal Debug/Release solution build/test commands remain documented in [operation instructions](live-timing.md).

Standalone process tests cover managed Local without Internet; Cloud on a separate local HTTP origin using the production REST/session/credential/server code; fifty synthetic racers; live event and full snapshot delivery; DNS/DNF/DSQ; Stop suppressing updates while retaining paused state; Start preserving a valid URL; Refresh overwriting divergent state; server process termination and automatic resync; an idle publisher restoring every run, competitor, result and source tick after a restart that empties server RAM, without Reconnecting or error backoff, with new viewers receiving the restored state; worker termination and explicit Start recovery; Delete/new session/Delete All Data; old public state missing after deletion; wrong-session, altered and expired tokens; fourteen-day expiration; full validation before mutation; version gaps; rate and session-capacity limits. Actual WebSocket subscriptions receive the updated state.

The desktop crash test runs the production simulator through timing capture and SQLite persistence. It terminates only the cloud worker while recording start, intermediate and finish impulses, verifies all three raw packets and seven-digit source precision were durably retained, checks the expected `1:00.87` elapsed time, confirms Local continues, then restarts Cloud and verifies the same public session receives the full authoritative result.

## FIS external acceptance

The supplied PDFs were reviewed directly: Live XML v53 and HTTPS sending v1.6. Their fingerprints and relevant references are in [the FIS review](fis-timing-review.md). The supplied **9754 test competition** was exercised with eighteen synthetic racers, two intermediates, finishes and DNS/DNF/DSQ.

Final implementation runs succeeded against both `https://livedata.fis-ski.com/al/` and `live.fis-ski.com:1550`, **175 positively acknowledged messages per transport**. HTTPS acknowledgements returned HTTP 200; TCP XML acknowledgements matched sequence numbers. Redacted wire logs are retained locally outside the repository. Earlier implementation runs also succeeded; redundant active-run commands were subsequently removed and both external tests repeated.

The public [FIS test race page](https://live.fis-ski.com/lv-al9754.htm) returned HTTP 200 in Chromium and displayed the synthetic competition, racers, intermediates, results and DNS/DNF/DSQ. Automated local FIS E2E additionally covers fragmented TCP acknowledgements, disconnect/reconnect/full replay, correction flags, all-run restoration, keepalive, HTTP rejection, invalid acknowledgement sequence and secret redaction. No clear/cancel or official result submission was sent.

## Explicit limits

Follow-up specification review repeated all 179 .NET tests and Chromium verification. The desktop status row now includes an inline recovery hint on error. The FIS TCP E2E additionally corrects run 1 while two racers have finished run 2, verifies both runs are restored, and checks that the run-2 finish retains its individual time while its cumulative rank changes to 2 and gap to 13.26 seconds. The supplied one-run FIS external rehearsal remains the evidence for external TCP/HTTPS acceptance; this multi-run correction scenario is tested against the real local TCP endpoint.

Azure infrastructure, live production DNS/TLS, Linux container execution, physical timing-device acceptance and production viewer load were not tested. Cloud acceptance is a full local process/protocol/browser rehearsal, not an Azure deployment claim.

RAM-only state and stateless restart-valid credentials cannot retain deletion revocations across a complete server restart. Desktop clears deleted credentials and cannot normally restore that session; an independently retained old token could restore data until expiration after that restart. Service-wide purge requires stopping publishers and rotating the signing key. This behavior and the future durable-revocation option are documented in [credential/deletion design](live-timing.md#credentials-expiry-and-deletion).

## Commit verification � 2026-10-02

An isolated export of the staged commit built successfully and passed 32 desktop workflow tests, 140 core/application/integration tests and 6 live timing process end-to-end tests (178 total). Uncommitted work from the parallel results task was excluded from this verification. No Azure deployment was performed.
