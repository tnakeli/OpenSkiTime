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

## Real Windows desktop (FlaUI, interactive session)

Command: `OPENSKITIME_DESKTOP_E2E=1 dotnet test tests/OpenSkiTime.Desktop.E2E -c Release --filter "FullyQualifiedName~OperatorRunsTheFullRace"` (after `dotnet build src/OpenSkiTime.Desktop -c Release`).

The test launches the built `OpenSkiTime.Desktop.exe` on the Windows desktop with `OPENSKITIME_LOCAL_DATA` pointing to a temporary folder (seeded only with the synthetic FIS list) and operates it with the real mouse and keyboard through UI Automation:

1. Event series form → *Create series file* → Windows save dialog (navigated to the temporary folder and verified before saving).
2. *Add competition*: name, FIS category, intermediates, codex, gender, TD, course and homologation → *Save competition*.
3. Competitors: the 100-athlete TSV on the clipboard, Ctrl+V in the grid, *Save changes*.
4. Start lists → Run 1 → *Draw*.
5. Settings → Timing devices: simulator as start device, *Add intermediate*, *Connect*, *Back to timing*, switch START/I1/FINISH inputs on.
6. Run 1: for every impulse the simulator device time is entered and *Test start* / *Test I1* / *Test finish* pressed; DNS on the next starter and DNF on the running racer through the quick status buttons (after checking the selected-racer line).
7. Ranking filter by bib → *Edit classification* → DSQ, gate, reason, judge → *Save classification*.
8. *Prepare Run 2* → *Create start list*; Timing Run 2, inputs on, all impulses, DNF, DSQ.
9. Results opened; the results summary is read from the window.
10. The application is closed and the saved `.ost` is opened with the application's persistence layer and compared against the independent expectations computed from the start lists the window actually drew.

Last execution (`artifacts/ux-e2e/e2e/real-window-race.log`):

```
Real-window race 2026-10-09T17:52:04Z: input mode mouse/keyboard
Run 1: 100 on list, 94 finished, 2 DNS, 3 DNF, 1 DSQ, 289 raw packets
Run 2: 94 on list, 91 finished, 2 DNF, 1 DSQ, 280 raw packets
Results view: 91 classified · 9 not classified. Review penalty and race information with the TD. 1 extra timestamp(s) remain unassigned or need review in Timing; original input is preserved.
Final: 91 classified; mismatches: 0
```

Result: passed (4 min 16 s with impulses entered through the controls' UI Automation Value/Invoke patterns; ~10 min per run with typed keys and pointer clicks, as in the video). Real-window screenshots of every step are written to `artifacts/ux-e2e/e2e/steps/` (01-series-created … 12-results).

Defects found in the automation while building it (not in the application) and how they were handled:

- The first runs showed that a new connection, and every newly selected run, starts with all timing positions on HOLD; the operator must switch START/I1/FINISH on. The driver now does so for each run (and the video shows it).
- A DNF was once applied to the racer who had just started instead of the one on course: the driver computed the row position just before the next start impulse reached the screen, and the new starter is inserted at the top of the Running list. The application kept its own selection correct (`SynchronizeSelection`); the click landed on the shifted row. The driver now waits until the Running list shows exactly the racers on course and verifies the selected-racer line before pressing a status. Recorded as usability finding UX-19.

Observation (explained): Results reported "1 extra timestamp(s) remain unassigned or need review" only while timing capture was still connected. In the demonstration run, where the operator disconnects timing first, Results shows no extra timestamp, and after reopening the file replay shows none either; approval is blocked while capture is connected in any case. Low; left as is.

## Demonstration video

`artifacts/ux-e2e/video/OpenSkiTime-demo.mp4` (generated, not committed): 1920×1080 H.264/AAC, 2 min 31 s (ffprobe 150.6 s), 13 chapters plus title, PDF showcase and closing cards.

Recorded from the real running application by `DemoVideoTests.RecordDemonstration` (FlaUI, ffmpeg gdigrab) and composed by `scripts/demo-video/compose.py`. Every step of the last recording succeeded (`steps.log`: fis-calendar, homologations, jury, race-information, xml, pdf all `ok`):

1. FIS calendar loaded from the FIS API (892 alpine events for season 2027), an event and its races shown, nothing imported.
2. Event series, FIS slalom, paste import of 100 athletes, FIS draw.
3. FIS equipment homologations refreshed from the FIS API; simulator connected, inputs switched on.
4. Run 1 and Run 2 with all impulses through the simulator controls, DNS/DNF quick status, DSQ with gate/reason/judge, Run 2 order.
5. Live Timing in Chromium (real local live server, 10 checkpoints).
6. Results with FIS penalty, jury and race information, TD approval, XML exported (`FIN9123.xml`).
7. PDF Factory with an organizer letterhead (`scripts/demo-video/make-letterhead.py`, embedded as the PDF background with 40/24 mm margins): 13 reports generated; three generated pages are shown in the closing showcase (rendered with `scripts/demo-video/Render-PdfPage.ps1`).

Privacy in the recording: the operator field is set to a role name ("Race office") instead of the Windows account; Windows file dialogs (which first list the user's own Documents folder) and the FIS calendar's technical-delegate columns are blurred for the time they are on screen (`redactions.json`), the window title bar (local temp path) is replaced by a neutral bar, and the status line is hidden in the results and PDF chapters (export path). Redaction boundaries were checked frame by frame in the final video. FIS points-list download, which contains real athletes, is not shown. All race data is synthetic.

PDF headers and footers: the organizer letterhead uses the existing PDF Factory background feature; no renderer change was needed for it. The review of the generated Official Results found jury functions printed as XML codes (`TechnicalDelegate:`, `ChiefRace:`); they now print their display names (shared `RaceInformation.JuryFunctionLabel`, asserted in `PdfFactoryTests`).
