# Live timing implementation plan

Live timing is a disposable downstream view of committed timing state. The application projects are under `src/`; tests are under `tests/`.

1. Define typed, versioned snapshot/event/health and private IPC contracts in a separate .NET 10 library. Retain integer 100 ns source timestamps and already calculated hundredths. Support all runs and corrections.
2. Build one ASP.NET Core server for local and cloud runtime: RAM sessions, signed session-scoped 14-day credentials, REST mutation API, bounded validation/rate/session limits, SignalR and an offline responsive browser view.
3. Build isolated publisher workers with private named-pipe IPC, bounded latest-state buffering, cancellation, timeouts, reconnect and full resync. Add FIS XML v53 TCP and HTTPS v1.6 transports with sequence acknowledgements and redacted protocol diagnostics.
4. Add a small desktop integration in new partial/view files. Show all three health channels and expose Start/Stop/Refresh/Delete/Delete All Data and URLs. Do not touch result calculation/publication or the series schema.
5. Add synthetic CLI simulation and process-level E2E tests: REST credentials, SignalR/browser, stop/resume, corrections, restart/full recovery, deletion, FIS transports and the supplied FIS test race. Never commit credentials or downloaded documents.
6. Prepare one container image and a complete future Azure Container Apps procedure (single replica, no database/backplane). Do not deploy Azure.
7. Run relevant builds, existing tests and E2E tests; record evidence and any external limitations honestly.

Coordination: new `OpenSkiTime.LiveTiming.*` projects and `MainViewModel.LiveTiming.cs` own the feature. Existing desktop timing files receive only explicit integration hooks and a view insertion. Re-read shared files before editing so concurrent results work is preserved.

Implementation and local/external verification completed on 2026-10-02. See [test results and explicit limits](live-timing-test-results.md), [operation/architecture](live-timing.md) and [prepared cloud deployment procedure](live-timing-cloud-deployment.md). Azure deployment remains intentionally unexecuted.
