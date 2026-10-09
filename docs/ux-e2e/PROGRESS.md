# Progress

Resume from here without the conversation history. Plan: [PLAN.md](PLAN.md).

## Current state

- Branch `feat/full-race-ux-e2e`, based on `docs/pre-pr-review` (`884d413`).
- Tooling verified on the development machine (Windows 11, 1920×1080 interactive session): .NET SDK 10.0.401, Codex CLI 0.162.0 (logged in with ChatGPT), Python 3.12 + Playwright + Chromium, ffmpeg 9.0.2 (winget `Gyan.FFmpeg`), Node 24.
- Baseline before any change: `dotnet test OpenSkiTime.slnx -c Release` → 557 passed (421 core, 111 desktop, 25 live), 0 failed.

## Completed

| Milestone | Evidence |
|---|---|
| Phase 0: plan, branch, tooling, baseline | This file, [PLAN.md](PLAN.md) |
| Phase 1: deterministic synthetic race + application-boundary E2E | `tests/OpenSkiTime.Tests/FullRace/` — `SyntheticRace` (generator), `ExpectedResults` (independent rules), `FullRaceScenario` (driver), `FullRaceScenarioTests`, `RaceExceptionTests`. Both full executions match all expectations with zero discrepancies and identical canonical output. See [TEST_REPORT.md](TEST_REPORT.md). |

## In progress / next

1. Codex review of milestone 1.
2. Local-data override for real-window automation (`OPENSKITIME_LOCAL_DATA`) so automation never touches the operator's real FIS cache, recent files or preferences.
3. Headless view-coverage screenshots (1280×800, 1920×1080) → UI audit.
4. UX improvements with regression tests.
5. FlaUI real-window suite; Playwright Live Timing full-race script; demo video.

## How to rerun the evidence

```bash
dotnet test tests/OpenSkiTime.Tests/OpenSkiTime.Tests.csproj -c Release --filter "FullyQualifiedName~FullRace"
```

Set `OPENSKITIME_FULL_RACE_REPORT=<absolute directory>` to keep the two `.ost` files, the backup and `full-race-report.md` (expected vs actual for all 100 athletes).

## Blockers

None so far.

## Notes for maintainers

- Folders created under the user's `Documents` inherit the read-only directory attribute on this machine, so the scenario clears it before deleting its own folders.
