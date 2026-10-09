# UI audit

Audited on 2026-10-09 against the completed synthetic 100-athlete race (`SyntheticRace`), rendered by the real Avalonia views at 1280×800 and 1920×1080. Screenshots are regenerated with:

```bash
OPENSKITIME_VIEW_COVERAGE=<absolute dir> dotnet test tests/OpenSkiTime.Desktop.Tests -c Release --filter "FullyQualifiedName~GenerateViewCoverageScreenshotsFromFullRace"
```

Before/after sets for this branch were written to `artifacts/ux-e2e/screenshots/before` and `.../after` (generated, not committed). Real-window screenshots from the Windows desktop session are listed in [TEST_REPORT.md](TEST_REPORT.md).

## View inventory and coverage

| # | View / dialog | Where | 1280×800 | 1920×1080 | Automated regression coverage |
|---|---|---|---|---|---|
| 00 | Welcome / no series | Event series without file | ✓ | ✓ | `EveryMainViewFollows…` (nav disabled state) |
| 01 | Event series form + FIS calendar browse | `MainWindow.axaml` | ✓ | ✓ | conventions test, `SeriesCalendarWorkflowTests` |
| 02 | Competitions grid + competition editor (course, homologation, TD) | `MainWindow.axaml` | ✓ | ✓ | conventions test, `CompetitionColumnControlsTests` |
| 03 | Competitors grid (paste import, filters, entries, points) | `MainWindow.axaml` | ✓ | ✓ | conventions test, `CompetitorGridLayoutTests` |
| 04 | Competitors → Update from FIS expander | `MainWindow.axaml` | ✓ | ✓ | `DesktopWorkflowTests` |
| — | Competitors → Category rules expander, review-changes panel, context menu | `MainWindow.axaml` | code review | code review | existing desktop tests |
| 05 | Start list Run 1 (draw settings, export, print) | `DrawView.axaml` | ✓ | ✓ | conventions test, draw workflow tests |
| 06 | Start list Run 2 (reversal) | `DrawView.axaml` | ✓ | ✓ | conventions test |
| 07 | Start list Run 2 → Run 1 input | `DrawView.axaml` | ✓ | ✓ | existing desktop tests |
| 08 | Timing: status bar, connection banner, At start, Running, Timestamps, Ranking, simulator controls | `TimingView.axaml`, `LiveTimingView.axaml` | ✓ | ✓ | conventions test (headers, splitters, ≥3 ranking rows), `RaceControlWorkflowTests` (queues at 980×680) |
| 09 | Timing → classification editor (DNS/DNF/DSQ/NPS, gate, reason, judge) | `TimingView.axaml` | ✓ | ✓ | conventions test, `PenaltyWorkflowTests`, `RaceControlWorkflowTests` |
| — | Timing → manual timestamp editor, B Clock panel, drag preview | `TimingView.axaml` | code review | code review | `ManualTimestampWorkflowTests`, `TimingDropPreviewWorkflowTests`, `BackupMonitorLayoutTests` |
| 10 | Timing Run 2 | `TimingView.axaml` | ✓ | ✓ | existing timing tests |
| 11 | Results & FIS penalty + Race information (jury, runs, forerunners, weather) | `ResultsView.axaml`, `RaceInformationView.axaml`, `RaceRunView.axaml` | ✓ | ✓ | conventions test, `RaceInformationWorkflowTests`, `ResultsVisualTests` |
| 12 | Timing report → Overview | `TimingReportView.axaml` | ✓ | ✓ | conventions test, `TimingReportWorkflowTests` |
| 13 | Timing report → Times | `TimingReportView.axaml` | ✓ | ✓ | `TimingReportOverviewTests` |
| 14 | PDF Factory | `PdfFactoryView.axaml` | ✓ | ✓ | conventions test, `PdfFactoryWorkflowTests` |
| 15 | Settings: FIS, Timing devices, Timing report (persons), Live timing, About | `SettingsView.axaml`, `TimingSettingsView.axaml`, `TimingReportPersonView.axaml` | ✓ (5 tabs) | ✓ (5 tabs) | conventions test, `SettingsLayoutTests`, `TimingRoleSettingsTests` |
| 16 | Receipt / device read dialog | `TimingReceiptDialog.axaml` | ✓ | ✓ | `TimingReceiptLayoutTests` |
| — | Confirm dialogs (discard changes, remove competition) | `AvaloniaFileDialogs.cs` | code review | code review | buttons already use `dangerAction` / `secondaryAction` |
| — | Live Timing control panel (separate process) | `LiveTiming.ControlPanel/PanelWindow.axaml` | real window | real window | `LiveControlPanelConnectionTests` |
| — | Browser Live Timing viewer | `LiveTiming.Server/wwwroot` | Playwright | Playwright | `tests/live-timing-browser-e2e.py`, full-race Playwright script |

## Findings

Severity: **High** = blocks reading or operating at a supported size; **Medium** = inconsistent or slower operation; **Low** = cosmetic.

| ID | View | Finding | Severity | Status |
|---|---|---|---|---|
| UX-01 | Timing ranking, Competitions grid (all grids with sort/filter headers) | Short headers (RK, BIB, NAT, HOMOLOGATION) broke inside words, one letter per line, because the sort and filter icons took the width of narrow columns; at 1280 px the Competitions grid also squeezed fixed columns until dates were cut (`12.12.2`) | High | Fixed: headers wrap only between words; `ColumnHeaderControls.ReserveWidth` keeps the declared width and room for the longest word plus icons, so the grid scrolls horizontally instead |
| UX-02 | Timing | After a run, Ranking showed only 2 rows at 1280×800; the pane splitters existed but were invisible | High | Improved: ranking share increased within the constraint that queues stay readable at 980×680 (existing regression test); visible splitter grips with hover state; ranking header one line. Now ≥3 rows at 1280×800, guarded by test |
| UX-03 | Many | Fluent's default grey buttons (`Read B…`, `Hand start…`, `Edit in Settings`, `Remove`, `Add replacement`, `Save classification`, receipt dialog actions, calendar actions, filter Apply/Cancel) next to OpenSkiTime-styled buttons | Medium | Fixed: every action uses `primaryAction` / `secondaryAction` / `dangerAction`; enforced on all main views by `EveryMainViewFollowsTheSharedButtonHeaderAndAccentConventions` |
| UX-04 | Competitors, Timing, Settings | Windows/Fluent blue (#0078D4) on check boxes, numeric spinners and the checked *Edit classification* toggle, while the app accent is teal | Medium | Fixed: Fluent `SystemAccentColor` family pinned to the app accent in `App.axaml`; test asserts equality |
| UX-05 | Settings, Timing report | Fluent display-size tab headers (~28 px) out of scale with the dense UI | Medium | Fixed: shared compact `TabItem` style (14 px semibold, accent selection pipe) |
| UX-06 | Start lists | FIS points formatted with the Windows decimal comma (`9,88`) while the competitors grid, PDFs and FIS use a decimal point | Medium | Fixed: `DrawStartListRow.Points` uses invariant `0.00`; test runs under fi-FI |
| UX-07 | Start lists | Status repeated twice (`Run started` + `Run started. Starting order is locked…`) in small grey text, no page heading | Low | Fixed: state shown as section title; help no longer repeats it |
| UX-08 | Navigation | *8 PDF Factory* stayed enabled with no series open while steps 2–7 were disabled | Low | Fixed: bound to the same availability as the other steps; test |
| UX-09 | Timing | *Save classification* (the primary action of the editor) was a grey default button; *Add timestamp* likewise | Medium | Fixed: `primaryAction` |
| UX-10 | Results | Results/penalty grid is below the Race information form; at 1280×800 the operator scrolls past jury/run forms to reach results | Medium | Open (layout change of a reviewed workflow; recorded for product decision) |
| UX-11 | Results, Race information | Jury grid uses larger body text than other grids and has no visible edit affordance until a cell is focused | Low | Open |
| UX-12 | Timing report | Replacement table uses large plain headers (`RUN BIB REASON`) instead of the shared field-label style; long header wraps | Low | Fixed: `fieldLabel` headers; the long source header is shortened with the full text as tooltip |
| UX-13 | Settings → Timing devices | Device date shown as `2026-12-12` while the rest of the UI uses `dd.MM.yyyy` | Low | Open (the field is parsed as ISO; changing it needs input-format review) |
| UX-14 | Competitors | Horizontal scrollbar overlays the last visible row of the grid | Low | Open (Avalonia DataGrid overlay scrollbar behaviour) |
| UX-15 | Receipt dialog | Disabled combo boxes use Fluent's grey fill | Low | Open |
| UX-17 | All forms and grids (accessibility) | Real-window UI Automation shows form inputs without accessible names (labels are separate text elements) and DataGrid cells named after their type (`Avalonia.Controls.TextBlock`). Screen readers and automation cannot identify fields by name; the real-window driver locates inputs relative to their visible labels | Medium | Open (needs `AutomationProperties.LabeledBy`/`Name` across forms and a DataGrid cell peer; recorded for a dedicated accessibility pass) |
| UX-18 | Browser Live Timing | After the last finisher the banner still reads `On course · —` | Low | Open |
| UX-19 | Timing → Running | A new starter is inserted at the top of the Running list, so rows move under the pointer at the moment a start impulse arrives; a click intended for the racer on course can select the new starter. The selected-racer line below the grids shows who a quick status will apply to | Medium | Open (product decision on list order; mitigated by the identity line and the existing selection synchronisation) |
| UX-20 | Timing | Every newly connected or newly selected run starts with all inputs on HOLD; the only hint is the small `HOLD · all positions` text in the banner | Low | Open |
| UX-16 | Results | With no FIS category on the competition, Results reported "no unique penalty rules" | — | Not a defect: the category comes from the FIS calendar data; the synthetic competition now carries category, gender and TD like a real FIS race |

## Conventions (design system)

All tokens live in `src/OpenSkiTime.Desktop/App.axaml` (and timing-specific ones in `TimingStyles.axaml`).

- **Colour**: one accent (`AccentBrush` #087E78) shared with Fluent through `SystemAccentColor*`; navy toolbar; error/warning brushes for status only.
- **Actions**: `primaryAction` = the one main action of a panel (filled accent); `secondaryAction` = other actions (white, bordered); `dangerAction` = destructive/removal (red outline); `toolbarAction` = navy top bar only; `statusDrop` = timing quick status targets. No unstyled Fluent buttons in views. Toggles that open inline editors use `ToggleButton.secondaryAction` (accent tint while open).
- **Sizes**: action buttons 34 px high (compact inline toggles may set 19–22 px explicitly), inputs 34 px, body text 12 px, labels 10 px semibold uppercase, section titles 16 px, tabs 14 px.
- **Grids**: `raceGrid`; header text never breaks inside a word; columns with sort/filter keep the declared width and header room, the grid scrolls horizontally rather than truncating values; numeric FIS values use a decimal point.
- **Layout**: page inset 22/18; surfaces `workSurface`; stacked panes are separated by visible `paneSplitter` grips; timing queues keep priority at small sizes.
