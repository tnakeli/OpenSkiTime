# M0 legacy reference evidence (partial)

Refreshed 2026-09-26 against recovered master `5505ceb` (`legacy-recovered-pre-codex`). The earlier `legacy-pre-codex` tag at `8259809` is unchanged; initial documents and two M0 tests were preserved in `6744c1a`. Master was merged into `rewrite/codex` in `0e3e354`. These are local preservation refs; no push was performed.

The recovered 19 commits add the Feature 002 competitor grid, editing, participation, paste/copy and session undo. Earlier claims that these screens were absent apply only to the old baseline. No production fix or M1 implementation was made during this refresh.

## Environment and results

- Windows x64; .NET SDK 10.0.401, runtime 10.0.12, EF CLI 10.0.0. The full `OpenSkiTime.slnx` restores and builds successfully with zero compiler warnings/errors.
- Restore used a local feed of cached official NuGet packages because this environment's Windows TLS stack could not reach NuGet directly. Audit was disabled for this restore. The prior online audit reported vulnerable transitive packages; no dependency upgrade occurred in recovered master. Zero build warnings are not a clean dependency audit.
- Untouched recovered master: **234 passing, 0 failing, 0 skipped** (Domain 94, Application 37, Import 93, Persistence 10). Desktop test project contains no inherited tests.
- Updated working branch: **241 passing, 0 failing, 0 skipped** (same Domain/Application/Import counts, Persistence 12, Desktop 5). No inherited assertion was changed. Seven M0 tests cover correct reference behavior, not the defects below.
- The rebuilt desktop process started, reported an `Open Ski Time` window and produced no stderr; it was then stopped. Headless Avalonia tests rendered production XAML and used focused keyboard activation of actual buttons. Native Windows controls are unavailable through this session's UI tool, so no Windows screenshot, DPI/theme review or real app close/reopen interaction is claimed.

Commands from the repository root in the configured development environment:

```powershell
dotnet restore OpenSkiTime.slnx --configfile ../work/NuGet.offline.config -p:NuGetAudit=false
dotnet build OpenSkiTime.slnx --no-restore
dotnet test OpenSkiTime.slnx --no-build --no-restore
```

The feed configuration and logs are local environment artifacts under `../work/`; a machine with normal NuGet access can use ordinary restore. This refresh's build/test logs, TRX files and isolated diagnostic probes are under `../work/m0-refreshed/`. They are not committed fixtures or public documentation.

## Verified boundaries

| Workflow | Evidence and limit |
|---|---|
| Series and competition create/edit | `Persistence.Tests/LegacyReferenceTests` uses production use cases against a migrated SQLite file; edited names, IDs and competition metadata survive a new context. Headless forms/navigation are tested below; native Windows interaction remains unverified. |
| Competitors and participation | Same test exercises service import, bib assignment, manual competitor editing and true/false participation. `Desktop.Tests/LegacyGridReferenceTests` additionally drives grid commands for Add, autosaved scalar edit, Restore, participation true/false, staged delete/undo and committed deletion; values are read from a separate context. This test does not drive XAML bindings. |
| TSV preview/apply/copy | Service test preserves uppercase `MÜLLER`, bib 7 and participation. Grid test separately previews existing Nation/Club changes without writing, applies them, reopens the file and copies selected fields through a fake clipboard. These are different apply paths; successful service tests do not validate the grid importer. |
| Headless UI | Two Avalonia tests instantiate the production application styles, shell, series/competition editor and competitor grid. Button activation creates/edits a series and competition; grid buttons preview/apply a simple pasted competitor and copy its selected row. Each save is checked through reopened SQLite. This covers XAML templates, commands and navigation; native rendering and operator input remain unverified. |
| Categories | A real `CategoryRule` is saved/reloaded and its inclusive year/gender match checked. No production category editor/resolver is connected to the grid. |
| Schema upgrade | Test creates migration `20260528160602_0001_Initial`, writes a series/competition, applies `20260528162742_0002_Competitors_Participation_Categories`, repeats migration, then reopens. Original rows remain, both migrations are applied once, no pending migration remains, and new tables are queryable. |
| Actual local database | Before startup, SQLite's backup API made a consistent copy of `%LOCALAPPDATA%/OpenSkiTime/openskitime.db`. Read-only inspection found both migration IDs, five empty business tables, `integrity_check=ok`, no reported FK violations, and only a competitor FK on `Participations`. The recovered version has no schema changes. This is a blank local database; no populated operator database upgrade was tested. |

## Reproduced defects

Isolated diagnostic runs used production grid/import services and migrated temporary databases with synthetic data; reopened reads supplied the observations below. These are observed defects, not passing assertions defining desired behavior. Diagnostics are separate from the repository test suite.

| Reproduction | Observed behavior | Required distinction |
|---|---|---|
| Save participation as false, select that competitor, Copy | Stored flag is false; copied competition cell is `x`. | Export must respect the boolean, not merely record existence. |
| Edit Gender to offered option `Men`, commit cell | Status says Saved, but reopened gender is null. | UI options must map to domain values; free text cannot silently erase data. |
| Paste a new `Müller / Hannes / 2007` row with Gender `Male`, Bib `7`, `3.1 SL=x`; Apply | Preview participation is true; reopened competitor has no participation, gender or bib. No error dialog occurs. | Grid apply must persist the reviewed fields or explicitly reject unsupported fields. |
| Existing nation FIN; paste GER; Discard; edit only Club | Immediately after Discard storage is FIN, display is GER; after the Club edit storage is GER. | Discard must restore/isolate preview changes so later edits cannot save them. |
| Preview Nation FIN→GER through `ImportPreviewService`, then shared `ImportApplyService` | Diff has one field change and Apply succeeds, but reopened nation remains FIN. | Shared service ignores the newly added scalar diff; grid instead has its own implementation. |
| In a headless Avalonia window, select the participation checkbox for an imported row | The cell is true and status says “Participation saved”, but a separate SQLite read remains false; this occurred with both direct property change and simulated pointer click. | Treat as a UI/persistence inconsistency requiring native reproduction and correction in the rewrite. The diagnostic was not retained as a passing characterization test. |

Source/schema findings also remain: non-atomic imports; grid ignores captured preview revision; `RowVersion` is not an EF concurrency token; no competition FK on participation; no SQLite code/bib uniqueness; rejected edits can mutate tracked entities before later validation fails. New risks include save callbacks triggered by checkbox binding changes, field identification by reorderable display index, and Refresh/Add reload discarding other staged state. The checkbox inconsistency was reproduced headlessly; column reordering and visual effects were not tested through native controls.

## Classification and specification comparison

| Classification | Findings |
|---|---|
| Correct reference | Persisted series/competition metadata, uppercase surnames, inclusive category matching, validated manual scalar edits and restore, manual participation toggles, staged delete/undo, and the tested scalar preview/apply path. Keep the compact series-scoped grid and TSV exchange concepts. |
| Known defects | Reproductions above and source/schema findings. They must not become rewrite acceptance criteria. Passing inherited tests leave these paths uncovered. |
| Unfinished | Filtering/grouping properties have no implementation; category editor/resolver, association/points, complete import warnings/empty overwrite and durable audit are absent. No actual runs/start lists, timing, ALGE integration, results or result export exist. |
| Human decision | Autosave versus staged edits, deletion/undo rules, bib display/scope, clipboard column order, identity/readiness, category/age policy and actual FIS/race rules. The rewrite's one portable database per event series is already decided. |

README still describes the old separate import page, claims optimistic locking and lists 168 tests. Feature 001 is partially implemented; this recovery supplies much of its missing grid workflow but not all validation, category or import behavior. Feature 002 describes Save/Discard for staged edits, application through `ImportApplyService`, a visible read-only bib column and a different clipboard order. Later code instead autosaves edits/participation, commits deletes separately, hides Bib and puts FIS Code first. Its 200-entry session log can be cleared and is not persistent audit. Historical tasks/quickstart therefore are not proof of acceptance. Public README was left unchanged during this internal evidence refresh.

## Outstanding M0 work

M0 remains **partial** until the recovered screens are exercised in a native Windows session: series/competition forms, manual competitor edits, participation, paste preview/apply/discard, copy, change-log restore/delete and app close/reopen. Verify stored data as well as status messages, especially the checkbox inconsistency. Test reordered columns, focus/selection, shortcuts, errors, DPI/theme and unsaved-change navigation. Category UI cannot be tested because it is unimplemented. No device behavior or real race results exist to exercise.

Failing repository tests in the final suite: **none**. A temporary headless checkbox assertion failed as described above; it was kept as diagnostic evidence rather than adding a passing test for defective behavior. Unverified work: the native UI checks above, populated operator-database migration/recovery, performance and hardware behavior. Process startup and headless/service tests do not close those gaps. Do not begin M1 on the assumption that M0 is complete.
