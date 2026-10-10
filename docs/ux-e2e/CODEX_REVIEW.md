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

## Review 3 — real-window automation, Live Timing E2E (commit `a903b53`)

### 3a — `codex review --base master` (session `01a121cc-20d6-72b1-b1c2-717ca13d9140`)

> The new exporter can delete unrelated data, and the real-window harness has concurrency and fallback failures.
>
> - [P1] Restrict snapshot cleanup to exporter-owned files — `tests/OpenSkiTime.Desktop.Tests/LiveFullRaceSnapshots.cs:24`. If `OPENSKITIME_LIVE_FULL_RACE` points to an existing shared directory, this deletes every JSON file there.
> - [P2] Serialize tests that share the interactive desktop — `tests/OpenSkiTime.Desktop.E2E/DemoVideoTests.cs:11-12`. xUnit can execute this class alongside `FullRaceWindowTests`; both control the same mouse, keyboard, clipboard and desktop-wide menus.
> - [P2] Avoid physical keystrokes in the pattern-only fallback — `tests/OpenSkiTime.Desktop.E2E/RaceOffice.cs:57-58`. On a locked session `app.Press` still sends physical input; folder navigation, clipboard paste and filter submission depend on it.

### 3b — focused prompt review (session `01a121cf-1683-74c1-8d8d-68fc5bb5bf87`)

> The automation introduces unsafe file/process operations and isolation failures, while intermediate-timing verification can pass despite regressions.
>
> - [P1] **High:** Verify the full save destination before writing — `RaceOffice.cs:59-61`. A suffix-only folder check can pass in another folder with the same name and save (and approve overwriting) there.
> - [P1] **High:** Scope overwrite confirmation to the launched application — `RaceOffice.cs:79-80`. A desktop-wide search can press *Yes* in another application's `Confirm Save As`.
> - [P1] **High:** Restrict screen-saver termination to the current session — `DesktopApp.cs:148-150`.
> - [P1] **High:** Delete only files owned by the snapshot exporter — `LiveFullRaceSnapshots.cs:24`.
> - [P2] **Medium:** Serialize tests that share the interactive desktop — `FullRaceWindowTests.cs:28-29`.
> - [P2] **Medium:** Reject unavailable physical input instead of incomplete fallback — `DesktopApp.cs:205`.
> - [P2] **Medium:** Verify saved intermediate times in the real-window race — `FullRaceWindowTests.cs:109-111`.
> - [P2] **Medium:** Require intermediate columns rather than skipping missing ones — `tests/live-timing-full-race-e2e.py:112-114`; `expected.json` split values are never checked.

### Resolution

All findings concern test tooling (no application code); all were accepted.

| Finding | Resolution |
|---|---|
| Snapshot cleanup | Only `snapshot-*.json` and `expected.json` are replaced. |
| Save destination | The target folder receives a uniquely named marker file; the dialog must list that marker after navigation and again before *Save*, otherwise it is cancelled. Markers are removed afterwards. |
| Overwrite confirmation | Only confirmations owned by the application's own modal windows are considered, and a replace prompt is always answered *No* and fails the step; automation only writes new files. |
| Screen-saver termination | Limited to `.scr` processes in the test's own session, and only when the current user's screen saver is not password-protected. |
| Parallel real-window tests | `[assembly: CollectionBehavior(DisableTestParallelization = true)]` in the E2E project. |
| Incomplete fallback | The pattern-only fallback was removed: without an unlocked interactive desktop the suite stops with a clear message. Documentation updated. |
| Real-window splits | The verification now compares every finisher's saved intermediate time with the independent expectation. |
| Live intermediate column | A missing `Intermediate 1` column fails the check, and the completed Run 1 intermediate times are compared with `expected.json`. |

## Review 4 — short demonstration, FIS look-ups, letterhead PDFs, jury labels (commit `ab80de9`)

### 4a — `codex review --base master` (session `01a12298-ca4c-74b2-8e37-88af122868a7`)

> The demonstration tooling can expose information that its redaction mechanism is intended to hide, both during normal recording and on failures.
>
> - [P1] Synchronize redaction timestamps with the recording timeline — `DemoVideoTests.cs:54-55`. `ScreenRecorder.Start` waits 1.5 s before the chapter/redaction clock starts, so redactions end early.
> - [P2] Finalize active redactions when recording fails — `DemoVideoTests.cs:109-111`. An interval still open when a step throws is never serialized.

### 4b — focused prompt review (session `01a1229b-1f3b-7d63-8f1f-d7ce1657496f`)

> The demo tooling has concrete privacy, file-cleanup, and false-success defects. No introduced timing, race-rule, tie-ranking, persistence, audit, jury-label, or external-submission regression was identified in the branch review.
>
> - [P1] **High:** Align redaction timestamps with the recording start — `DemoVideoTests.cs:54-55` (the recording exceeded the chapter clock by 1.568 s).
> - [P1] **High:** Preserve active redactions when a recording fails — `DemoVideoTests.cs:109-111`.
> - [P2] **Medium:** Restrict cleanup to composer-owned segments — `scripts/demo-video/compose.py:133-136`.
> - [P2] **Medium:** Verify that the homologation lookup succeeded — `RaceOffice.cs:427-430`.
> - [P2] **Medium:** Reject failed PDF generation instead of logging success — `RaceOffice.cs:540-542`.

### Resolution

All accepted (demonstration tooling only; no application defect was reported).

| Finding | Resolution |
|---|---|
| Redaction timing | The clock now starts immediately before ffmpeg; every redaction gets a 2 s margin on both sides. The already recorded take was corrected by its measured offset (1.568 s) before composing, and a new take was recorded with the fix. Boundary frames of every redaction (start, just before the end, just after the end) were extracted from the final video and checked: blurred while visible, clean afterwards. |
| Unfinished redaction | An interval still open when recording stops is blurred to the end of the recording. |
| Composer cleanup | Each composition uses a fresh `compose-*` work folder; nothing existing is deleted or overwritten. |
| Homologation false success | The step requires the application's success status (`N homologations cached · …`); otherwise it fails. |
| PDF false success | The step parses the summary and requires generated > 0 and 0 failed. |

Additional privacy measures found while verifying the final video: the operator field (default: Windows account name) is set to "Race office" in the demonstration, the window title bar (local path) is replaced by a neutral bar, and the status line is hidden in the results and PDF chapters (it shows the export path).

## Review 5 — after the review-4 fixes (commit `aa45870`)

`codex review --base master` (session `01a122ac-880c-7171-8a54-4f935b425e33`):

> The new tooling can delete non-owned files and prematurely stop privacy redaction on a dialog failure.
>
> - [P1] Verify ownership before deleting existing execution folders — `tests/OpenSkiTime.Tests/FullRace/FullRaceScenarioTests.cs:51-53`. With `OPENSKITIME_FULL_RACE_REPORT` pointing to an existing directory containing `execution-1`/`execution-2`, those folders are deleted recursively without checking ownership.
> - [P2] Keep redaction active until the file dialog actually closes — `tests/OpenSkiTime.Desktop.E2E/RaceOffice.cs:396-397`. A failed open dialog ends redaction while the dialog remains visible.

Resolution (both accepted): the report option now writes into a new timestamped subfolder and deletes nothing; redaction of open and save dialogs ends only after the dialog is confirmed closed, otherwise it stays active to the end of the recording. Re-verified: full-race tests 4/4 twice from the command line (report: identical canonical output, no discrepancies), E2E project builds.

## Review 6 — final (commit `4c643b7`)

`codex review --base master` (session `01a122b0-5d7a-72f1-888d-230c52436a0e`):

> No actionable correctness regressions were identified in the changes against the supplied merge base. The diff whitespace check passed; builds and tests were not rerun in this read-only environment.

Final status: no open Critical, High or Medium findings. `dotnet test OpenSkiTime.slnx -c Release` → 566 passed, 0 failed.
