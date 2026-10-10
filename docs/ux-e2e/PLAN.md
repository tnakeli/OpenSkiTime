# UX consistency, full-race E2E and demo video — plan

Branch: `feat/full-race-ux-e2e` (from `docs/pre-pr-review`, which carries the pre-PR review rules in `AGENTS.md`).
Resume state: [PROGRESS.md](PROGRESS.md). Evidence: [UI_AUDIT.md](UI_AUDIT.md), [TEST_REPORT.md](TEST_REPORT.md), [CODEX_REVIEW.md](CODEX_REVIEW.md).

## Scope

1. Audit every desktop view, dialog, toolbar and grid, the Live Timing control panel and the browser viewer.
2. Implement consistency/usability improvements through the shared styles in `App.axaml`/`TimingStyles.axaml` first, then targeted view fixes. Keep the dense race-office layout.
3. Deterministic synthetic 100-athlete two-run women's Slalom exercised end to end: import, draw, Run 1, exceptions/corrections, Run 2 from Run 1 classification, results, XML, reports, Live Timing, reopen, backup/restore.
4. Real desktop automation of the running Windows application (FlaUI/UIA) and browser automation of Live Timing (Playwright).
5. A 1920×1080 MP4 demonstration recorded from the real running application, driven by a reproducible script.
6. Independent Codex CLI reviews after milestones and before delivery.

Out of scope: changes to timing algorithms or FIS rules unless a verified defect requires them; submitting anything to real FIS or public live services; database migrations (pre-release policy).

## Architecture decisions

| Concern | Decision |
|---|---|
| Synthetic race data | One generator (`SyntheticRace`) with a fixed seed produces athletes, FIS-style points (with deliberate equal-points groups), a synthetic FIS points-list archive and all run times (with deliberate equal Run 1 and combined times). Shared by tests, desktop automation and the video script. Nothing is committed except the generator. |
| Full-race validation | Application boundary (`SeriesWorkspace`, real SQLite `.ost`, real simulator `ITimingSource`, ALGE decoding) in `OpenSkiTime.Tests`, with expected values calculated independently in the test from the generator's raw times. |
| Local-data isolation | The real desktop app reads FIS lists, recent files and preferences from `%LOCALAPPDATA%/OpenSkiTime`. A documented `OPENSKITIME_LOCAL_DATA` override lets automation run against a temporary directory so the operator's real FIS cache and preferences are never touched. |
| Real-window automation | New opt-in project `tests/OpenSkiTime.Desktop.E2E` using FlaUI (UIA3). Skipped unless `OPENSKITIME_DESKTOP_E2E=1`, so `dotnet test OpenSkiTime.slnx` stays headless-safe. |
| Browser Live Timing | Extend the existing Python Playwright approach (`tests/live-timing-browser-e2e.py`) with a full-race script fed by snapshots from the desktop mapper. |
| Video | FlaUI demo driver writes a chapter log; `ffmpeg` (gdigrab) records 1920×1080; post-processing adds chapter cards/captions and speeds up repetitive timing. Output under `artifacts/` (git-ignored). |

## Phases and acceptance criteria

| # | Phase | Done when |
|---|---|---|
| 0 | Preparation | Branch, plan/progress docs, tooling verified (Codex, ffmpeg, Playwright, FlaUI), baseline test results recorded. |
| 1 | Synthetic race + application E2E | 100-athlete scenario passes twice with identical outputs; expected-vs-actual assertions for draws, Run 2 eligibility/order, ties, results, XML, audit, reopen, backup/restore. |
| 2 | UX audit | Every view in the coverage matrix has rendered screenshots at 1280×800 and 1920×1080 and evaluated findings. |
| 3 | UX implementation | Shared conventions documented and applied; each fix has headless layout/functional regression coverage; existing tests pass. |
| 4 | Real desktop E2E | FlaUI suite launches the real app and exercises navigation, import, draw, timing, classification and results; real-window screenshots captured. |
| 5 | Live Timing browser E2E | Playwright verifies updates during both runs, intermediates, classifications, ties and final results. |
| 6 | Codex review | Reviews after milestones and before delivery saved verbatim; no unresolved Critical/High findings. |
| 7 | Video | 6–12 min 1920×1080 MP4 with the 12 required chapters, verified with ffprobe and frame sampling; script reproducible. |
| 8 | Delivery | `dotnet test OpenSkiTime.slnx -c Release` green, scenario run twice, final Codex review after last commit, summary with paths. |
