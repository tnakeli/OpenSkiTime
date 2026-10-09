# Progress

Resume from here without the conversation history. Plan: [PLAN.md](PLAN.md).

## Current state

- Branch `feat/full-race-ux-e2e`, based on `docs/pre-pr-review` (`884d413`).
- Tooling on the development machine (Windows 11, 1920×1080 interactive session): .NET SDK 10.0.401, Codex CLI 0.162.0 (ChatGPT login), Python 3.12 + Playwright 1.58 + Chromium, ffmpeg 9.0.2 (winget `Gyan.FFmpeg`, not on the default PATH), Node 24, FlaUI 5.0.0.
- Baseline before any change: `dotnet test OpenSkiTime.slnx -c Release` → 557 passed.

## Completed

| Milestone | Evidence |
|---|---|
| Phase 0: plan, branch, tooling, baseline | [PLAN.md](PLAN.md) |
| Phase 1: deterministic synthetic race + application-boundary E2E | `tests/OpenSkiTime.Tests/FullRace/`; [TEST_REPORT.md](TEST_REPORT.md) |
| Codex review 1 | [CODEX_REVIEW.md](CODEX_REVIEW.md): no findings |
| Local-data isolation | `LocalDataDirectory` + `OPENSKITIME_LOCAL_DATA` |
| Phase 2: UI audit | `ViewCoverageScreenshots`; [UI_AUDIT.md](UI_AUDIT.md) |
| Phase 3: UX implementation | Shared accent, compact tabs, button classes, header width reservation, pane splitters, timing balance, invariant points, start-list heading, PDF nav state, live banner. `UxConsistencyTests`, `ColumnWidthReservationTests`. |
| Codex review 2 | 3 Medium findings fixed (width floor, env-var test isolation, full evidence comparison) |
| Phase 4: real desktop E2E | `tests/OpenSkiTime.Desktop.E2E` (FlaUI): the whole race through the real window, verified against independent expectations — passed |
| Phase 5: Live Timing browser E2E | `tests/live-timing-full-race-e2e.py` + `LiveFullRaceSnapshots` — passed (10 checkpoints) |
| Codex review 3 | 4 High + 4 Medium findings in test tooling, all fixed |

## In progress / next

1. Demonstration video: recording (`RecordDemonstration`) → `scripts/demo-video/compose.py` → verify with ffprobe and sampled frames.
2. Rebuild/re-run after the review-3 fixes (E2E project, desktop tests), full suite, scenario twice.
3. Final Codex review after the last commit; final summary.

## How to rerun the evidence

See the table in `DEVELOPMENT.md` ("Full-race, real-window and Live Timing end-to-end tests").

## Blockers and environment notes

- The screen saver on this machine starts after 60 s (non-secure). It blocked synthetic input and screen capture once ("Access is denied" from SendInput, input desktop `Screen-saver`). The real-window suites now keep the display awake for their duration and end only this session's non-secure saver; a locked or secure session stops the suite with a clear message.
- The agent's default command sandbox blocks synthetic input to other windows; real-window suites must run outside it.
- Folders under `Documents` inherit the read-only directory attribute here; test cleanup clears it on its own folders.
- Codex's Windows sandbox cannot start shell commands (`setup refresh had errors`); it reviews by reading files.

## Status (2026-10-10)

- Short demonstration video recorded and composed (2 min 31 s), with FIS calendar/homologation look-ups, letterhead PDFs, privacy blurring. See [TEST_REPORT.md](TEST_REPORT.md#demonstration-video).
- Real-window E2E re-run after the review-3 fixes: passed, 0 mismatches (now including intermediate times).
- Full suite: 566 passed.
- Remaining: final Codex review of the last commit; pull request not opened (awaiting the user's decision).
