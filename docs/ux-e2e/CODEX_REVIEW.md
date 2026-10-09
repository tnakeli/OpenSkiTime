# Independent Codex reviews

Reviewer: OpenAI Codex CLI 0.162.0 (`codex review`), logged in with ChatGPT, model reported as `gpt-6.1-sol`, sandbox `read-only`, reasoning effort high. Codex is run by the implementation agent but reviews independently; its output is recorded verbatim below (tool chatter such as `mcp: node_repl/js started` lines omitted) and every finding is investigated before it is accepted or rejected.

Environment note: on this Windows machine Codex's sandbox cannot start PowerShell commands (`Failed to create unified exec process: helper_unknown_error: setup refresh had errors`). Codex falls back to its Node.js tool to read files and run read-only checks, so it can inspect the diff but generally does not run `dotnet build`/`dotnet test`. Builds and tests are run separately and reported in [TEST_REPORT.md](TEST_REPORT.md).

## Review 1 — milestone 1 (full-race scenario)

- Command: `codex review --base master`
- Commit reviewed: `bebb74b` (merge base with master `dde15bd`)
- Session: `01a1217d-4b27-7952-a88e-00d9cce6e41d`

Verbatim result:

> No actionable correctness regressions were identified in the inspected diff. The diff whitespace check passed; builds and tests were not rerun in this read-only environment.

Resolution: no findings to resolve.

## Review 2 — UX milestone (commit `f5e490c`)

`codex review` does not accept custom instructions together with `--base`, so two reviews were run: the standard branch review and a prompt-only review instructed to review `git diff master...HEAD` with the focus areas of this task (UI consistency/usability, bindings and commands, `OPENSKITIME_LOCAL_DATA`, timing/race rules/ties in the full-race tests, persistence/audit, test completeness and flakiness, architecture).

### 2a — `codex review --base master` (session `01a12192-d43b-7a01-933d-87a7612e1157`)

> No actionable correctness regressions were identified in the changes against the supplied merge base. The diff whitespace check passed; builds and tests were not rerun in this read-only environment.

### 2b — focused prompt review (session `01a12195-1b23-71c0-8efd-f8a5f9762d5a`)

> The patch introduces a column-resizing regression and a process-wide test-isolation race, and its new restore assertions do not fully verify evidence preservation. No concrete timing-rule or ranking regression was identified in the committed changes. Builds and tests were not rerun in the read-only environment.
>
> - [P2] Preserve the original width floor when reinstalling headers (Medium) — `src/OpenSkiTime.Desktop/ColumnHeaderControls.cs:24-25`. After an operator widens a fixed-width Ranking column, opening a competition with additional intermediates reinstalls `ColumnGridController` through `ConfigureRankingColumns`. Avalonia has updated `column.Width.Value` to the resized width, so this code promotes that width to `MinWidth`; the operator can no longer shrink the column back. Preserve the original declared minimum separately and make reservation idempotent across reinstalls.
> - [P2] Isolate the process-wide environment override test (Medium) — `tests/OpenSkiTime.Desktop.Tests/LocalDataDirectoryTests.cs:16-18`. When the desktop suite runs with normal xUnit collection parallelism, this separate test class can overlap `DesktopWorkflowTests`, whose view models do not always inject every local store. Those constructors can capture this temporary directory and read the deliberately invalid `[1, 2, 3]` FIS archive, producing unrelated cache errors. Run this test in a nonparallel collection or an isolated process.
> - [P2] Compare complete timing evidence after restore (Medium) — `tests/OpenSkiTime.Tests/FullRace/FullRaceScenario.cs:179-180`. The reopen/backup check verifies only packet counts, while `Fingerprint` omits raw bytes and audit timestamps, `Before` values, and most `After` values. Compare complete packet payloads and audit records against their pre-backup values for both restored files.

### Resolution

| Finding | Assessment | Resolution |
|---|---|---|
| Width floor promoted after a resize | Valid. `ConfigureRankingColumns` reinstalls the controller per run/intermediate configuration. | `ReserveWidth` records the declared floor once per column (`ConditionalWeakTable`) and reuses it; regression test `ColumnWidthReservationTests`. |
| Environment override test races other tests | Valid (process-wide variable; default-constructed stores elsewhere). | Test moved to a dedicated xUnit collection with `DisableParallelization = true`. |
| Restore check too weak | Valid. | `EvidenceAsync` compares every capture session, every raw packet (session, sequence, receive time, protocol/source/stream, exact bytes) and every audit record (serialized in full) before backup vs. after reopening both the original and the backup file. |

Verification after the fixes: `dotnet test OpenSkiTime.slnx -c Release` → 564 passed, plus the new reservation test.
