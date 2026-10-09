# Test report

All results below come from real executions on the development machine (Windows 11 Pro 10.0.26200, .NET SDK 10.0.401, 1920×1080). Sections are updated as phases complete; each lists the exact command.

## Baseline (before changes)

`dotnet test OpenSkiTime.slnx -c Release`: 557 passed, 0 failed (OpenSkiTime.Tests 421, OpenSkiTime.Desktop.Tests 111, OpenSkiTime.LiveTiming.Tests 25).

## 100-athlete synthetic slalom — application boundary

Command:

```bash
dotnet test tests/OpenSkiTime.Tests/OpenSkiTime.Tests.csproj -c Release --filter "FullyQualifiedName~FullRace"
```

Executed twice from the command line (once with `OPENSKITIME_FULL_RACE_REPORT` set, once without); each test execution itself runs the complete race twice in separate series files and asserts identical canonical output. Result: 4/4 tests passed both times (~27 s for the full race pair).

### What is exercised

| Area | How | Assertion against |
|---|---|---|
| Data | `SyntheticRace` (seed 20261212): 100 fictional women, codes 990101–990200, 12 nations, 10 invented clubs, birth years 1998–2008, 92 with SL points, 8 without; equal points groups of 3, 2 and 3 athletes; synthetic FIS points list ZIP incl. penalty tables | Generator determinism test |
| Import | TSV preview → accepted commit → `ApplyImportAsync`; repeat the same commit | 100 created, 0 updated, no warnings; repeat is `AlreadyApplied`; every stored athlete equals the source row |
| Run 1 draw | `FisStartOrder.FirstRun` with fixed seed, entrants from the parsed points list | Bibs 1–100; first group expanded from 15 to 16 by equal points at the boundary; ascending points afterwards; 8 no-points athletes last; 8 equal-points competitors flagged; same order when the input order is reversed |
| Run 1 timing | `SimulatorTimingSource` → ALGE ASCII decoder → durable capture, auto-assignment following the start order; 45 s start interval; start, intermediate and finish impulses on the 0.0001 s grid | Every result, split and rank equals the independent calculation (subtraction then truncation) |
| Race exceptions (Run 1) | 2 DNS (classified as expected starter), 3 DNF (one before the intermediate), 1 post-run DSQ with gate/reason/judge, 1 missing finish impulse replaced by an audited hand time (hundredths) and assigned, 1 missing intermediate, 1 photocell double impulse 0.03 s after a finish (auto-assigned to the next skier on course, then ignored by the operator), 1 DSQ on the wrong bib undone (reversal appended) | Status/time per athlete; raw packets = pulses sent; 1 manual timestamp and 1 ignored impulse in the audit; run complete with 0 unresolved impulses |
| Ties | Two Run 1 ties created with different raw ticks but equal hundredths (ranks 12/13 and 30/31); two combined ties (ranks 3 and 15) | Shared ranks in Run 1 and final results; next rank skipped |
| Run 2 order | `FisStartOrder.SecondRun` from Run 1 timing, reversal 30 | Exactly the 94 Run 1 finishers (DNS/DNF/DSQ excluded); reversed group expanded to 31 by the boundary tie; order equals an independent ICR 621.11 implementation; bibs unchanged |
| Run 2 timing | Same pipeline; 2 DNF, 1 post-run DSQ | Every result equals the independent calculation |
| Results | `FisRaceResults.Assemble` and official listing order | 100 rows: status, run times, totals and ranks equal the independent calculation; ex aequo listed by higher bib first |
| Penalty | Rule tables from the synthetic list (F 730, max 165, min 23, FIS category) | Independent implementation of FIS Points Rules 4.4–4.5 gives the same calculated (3.13) and applied (330.00, double minimum because ≥3 finishers lack points) |
| XML / approval | `FisResultXml.Create`, approve with source fingerprint | 91 `AL_ranked` in official order with matching ranks; 9 `AL_notranked` with statuses DNS1×2, DNF1×3, DSQ1, DNF2×2, DSQ2; DSQ rows carry the gate; approved bytes stored |
| Persistence | Close/reopen the `.ost`; online backup; open the backup as a restored series | Results, splits, DSQ details, audit (300 + 283 entries), raw packets (289 + 280) and approved XML bytes identical |

### Counts from the last execution

| Run | On list | Finished | DNS | DNF | DSQ | Raw packets | Observations | Audit entries | Manual timestamps | Ignored impulses |
|---|---|---|---|---|---|---|---|---|---|---|
| 1 | 100 | 94 | 2 | 3 | 1 | 289 | 290 | 300 | 1 | 1 |
| 2 | 94 | 91 | 0 | 2 | 1 | 280 | 280 | 283 | 0 | 0 |

Expected-vs-actual: 100/100 final rows match; discrepancies: none. The per-athlete table is written to `full-race-report.md` in the report directory (generated artifact, not committed).

### Controlled exception tests

`RaceExceptionTests` (same command):

- A device retransmitting an identical start line: the second observation is `Duplicate` of the first, raw input keeps all 3 packets, the result is unaffected and survives reopening.
- A missing finish without hand time keeps the run incomplete and blocks `ToRunFinishes`; malformed manual time or a blank reason is rejected without an audit row; a valid hand time is audited (operator/reason) and produces the manual finish flag.

## Live Timing in the browser (Playwright, Chromium)

Commands (see `DEVELOPMENT.md`): export the snapshots with `ExportLiveSnapshotsOfTheFullRace`, then `python tests/live-timing-full-race-e2e.py --snapshots <dir> --screenshots <dir>`.

The snapshots are built with the desktop's own `LiveSnapshotMapper` from the timing state of the full-race scenario at 25 %, 50 %, 75 % and the end of each run (10 checkpoints), published with `PUT /api/sessions/{id}/state` to a real local `OpenSkiTime.LiveTiming.Server` (loopback only, the browser is blocked from any other host) and checked in Chromium after each update.

Result of the last execution:

```
snapshot-01-run1-24pct: run 1, 100 rows, 23 finished, 23 finished rows verified
snapshot-02-run1-49pct: run 1, 100 rows, 46 finished, 46 finished rows verified
snapshot-03-run1-74pct: run 1, 100 rows, 70 finished, 70 finished rows verified
snapshot-04-run1-98pct: run 1, 100 rows, 93 finished, 93 finished rows verified
snapshot-05-run1-complete: run 1, 100 rows, 94 finished, 94 finished rows verified
  run 1 verified against 100 independent expectations; ties [[14, 25], [31, 30]] share ranks
snapshot-06-run2-24pct: run 2, 94 rows, 23 finished, 23 finished rows verified
snapshot-07-run2-49pct: run 2, 94 rows, 45 finished, 45 finished rows verified
snapshot-08-run2-74pct: run 2, 94 rows, 67 finished, 67 finished rows verified
snapshot-09-run2-99pct: run 2, 94 rows, 91 finished, 91 finished rows verified
snapshot-10-run2-complete: run 2, 94 rows, 91 finished, 91 finished rows verified
  final standings verified against 91 independent totals; combined ties [[5, 18], [21, 20]] share ranks
PASS: all live checkpoints verified in Chromium
```

Per checkpoint: row count equals the run's start order; every row's status (Ready/OnCourse/Finished/DNS/DNF/DSQ) matches; every finisher's rank, intermediate time, run time (Run 1) or Run 1/Run 2/total (Run 2) matches the published values; the completed runs match the independent expectations (all 100 Run 1 rows and all 91 classified totals) and both deliberate ties in each run share a rank. A phone-sized viewport (390×844) loads the same final standings. No browser errors.
