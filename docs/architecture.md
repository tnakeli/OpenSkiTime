# OpenSkiTime architecture

Reference review refreshed 2026-09-26 against recovered master: `legacy-recovered-pre-codex` (`5505ceb`). The earlier `legacy-pre-codex` (`8259809`) remains unchanged. Work branch: `rewrite/codex`. The reference findings below describe legacy; current implementation progress is recorded in milestone evidence.

## Evidence and current structure

The initial repository review was refreshed against all 19 recovered commits, including the grid, import changes and Feature 002 planning documents. SDK 10.0.401 builds the complete solution; recovered master passes 234 tests. Seven M0 tests bring the working branch to 241 passing tests. Production use cases, headless Avalonia screens/commands and real SQLite migrations/reopening were exercised; native Windows controls, appearance and performance remain unverified. See [M0 evidence](m0-evidence.md) for boundaries and reproduced defects. README counts and historical task checkmarks are not acceptance evidence.

`OpenSkiTime.slnx` contains six source projects and five test projects:

```text
Desktop (Avalonia/MVVM, composition root)
  -> Application -> Domain
  -> Persistence -> Application + Domain
  -> Import      -> Application + Domain
  -> Fis.Placeholder -> Domain
```

Domain has no package dependencies, but exposes persistence hooks and reads the system clock. `EventSeries` owns the competition/competitor/category graph; competitors own participations. Application uses repository/unit-of-work interfaces. Desktop also reads repositories and passes tracked entities to views. One SQLite database per OS user is migrated before the window opens. Two migrations create five business tables; recovered master adds no migration. The series now contains a competitor tab with editable rows, participation columns, inline paste/copy and a session change log. There is still no timing engine, device driver, result calculator, result export or live service.

## Findings that change the design

Paths are relative to `src/` unless otherwise stated; module names in path examples omit the common `OpenSkiTime.` prefix.

| ID | Evidence | Consequence |
|---|---|---|
| F1 | `Import/ImportApplyService.cs` calls independently saving add/bib use cases and multiple saves; never begins a transaction. | Failed imports can leave partial changes. Success can include errors. Require one explicit commit of the reviewed batch. |
| F2 | Active `CompetitorGridViewModel.ApplyImportAsync` bypasses `ImportApplyService`, ignores the captured revision, bibs and participation, and saves rows separately. Discard removes only new rows. | Preview differs from persistence; discarded scalar edits can be saved by a later unrelated edit. Bind preview to target/source/revision and apply atomically; isolate the review buffer. |
| F3 | `Desktop/App.axaml.cs` and `ShellViewModel.cs` resolve scoped services from the root provider. | UI effectively shares a long-lived DbContext, contrary to ADR 0002. Stale tracking, overlapping operations and failed edits can affect later commands. |
| F4 | `Persistence/Configurations/EventSeriesConfiguration.cs` explicitly excludes concurrency-token semantics; `OpenSkiTimeDbContext.cs` omits category changes and requires tracked competitor ownership for participation changes. | README's optimistic-locking claim is incorrect. Pre-reading `RowVersion` is not atomic concurrency protection. |
| F5 | Participation mapping/migration has a competitor FK but no competition FK; `RemoveCompetitionUseCase` only removes the competition. | Orphan participation rows can remain. Database constraints do not ensure same-series ownership. |
| F6 | No database uniqueness for competitor codes/bibs. Add checks name/year only; editing bypasses that check. Label insertion is case-insensitive in memory, but the SQLite index uses default collation and edits bypass the aggregate check. | Identity, bib and label collisions can reach storage or make imports ambiguous. Normalize consistently and enforce scoped constraints. |
| F7 | `Competitor.UpdatePersonalData` assigns some fields before validating later fields. | A rejected edit can leave tracked mutations that a later save persists. Validate the complete change before mutation and discard failed operation contexts. |
| F8 | `DiffEngine` now previews scalar changes, but `ImportApplyService` ignores them. Gender options `Men`/`Women` do not match domain enum parsing; new import rows omit gender. Copy tests participation record existence instead of its boolean. | Reproduced data loss and incorrect TSV output despite success messages. Test the active UI command through reopened storage, not just the shared parser. |
| F9 | The grid is reachable and editable, but filter/group properties still have no effect. Cell commit identifies fields by reorderable display index; asynchronous handlers share the context. A headless checkbox probe displayed a true participation and a success message while reopened storage remained false. Category resolution remains test-side LINQ. | Keep the dense participation workflow; replace fragile command/state handling. Verify binding-triggered writes and column reordering in native UI. |
| F10 | Startup migration and most async UI operations lack a recovery boundary; logging is debug/trace only. No backup/restore or audit implementation. | Storage/startup failures can prevent operation without actionable recovery. Timing must not inherit this failure model. |

Poor abstractions: `IImportPreviewService` returns `object`; actual import services implement neither import interface; the grid injects an unused apply service while duplicating persistence orchestration. The old import view remains in source but is unregistered/unreachable from the shell. Clipboard abstraction is now useful. Repository `Update` is a tracking-dependent no-op; `IUnitOfWork` promises commit-on-dispose, but its wrapper rolls back unless its inaccessible commit method is called. Whole-graph reloads also clear grid drafts/undo state. These are boundary/lifetime problems, not evidence against SQLite or EF Core.

## Coverage and specification gaps

- Useful inherited tests cover normalization, validation, parsing, bib conflicts and SQLite round trips. Recovered master had zero desktop tests. M0 adds two migration/use-case tests, three grid-command tests and two headless Avalonia tests using migrated files and fake clipboard/dialogs. These verify XAML navigation/buttons, but cannot prove native Windows UI interaction.
- `CompetitorPersistenceTests.Import_apply_transaction_rolls_back_on_failure` tests a hand-written transaction, not the importer. `Duplicate_name_yob_raises_db_constraint` throws in memory. Code-uniqueness tests check names; missing-data tests use valid competitors; resolver tests implement resolution inside the test.
- Missing coverage: importer rollback/cancellation/retry, changed previews/series, code/bib constraints, competing edits, competition deletion with entries, failed-edit isolation, operator-database upgrades, backup/restore, startup recovery, native navigation/bindings and full keyboard workflows. Timing fault/replay tests are new work.
- [Feature 001](../specs/001-event-series-management/spec.md) still has gaps in association/points/code fields, readiness, category resolution, filtering/grouping, explicit empty overwrite, uncertain-name correction and complete preview buckets. Editing, participation, clipboard copy and scalar previews now exist, with the defects above. Homologation/FIS readiness remains incomplete.
- [Feature 002](../specs/002-competitor-grid-ux/spec.md) describes staged edits with Save/Discard, a read-only bib column and application through the shared importer. Later code instead autosaves edits/participation, delays deletes until Commit Changes, omits the bib column and puts FIS Code first in exports. The session log is capped at 200, can be cleared, and is not durable audit. Confirm the intended save/undo contract with the operator.
- Feature 001 `tasks.md` marks absent components/tests complete, including T095/T109/T110; T100's old shell route was superseded by the series tab. Other tasks remain unchecked. README, ADRs, schema descriptions and code disagree on scope, context lifetime, concurrency, GUID storage (actual TEXT) and enum storage (mixed text/integer).
- Code exceeds/differs from the feature specification in series-wide bibs, persisted `Participation.StartOrder`, additional header aliases, comma-separated names and optional FIS/nation/gender readiness. Assess these rather than automatically adopting them. Timing, ALGE Timy3/MT1, draws, second-run ordering, FIS XML/API/reports/penalties and live timing were explicitly deferred.
- No dependency versions changed in recovered master. Central versions and nullable/warning checks exist; SDK pinning, CI and lock files do not. NU1900–NU1904 are exempted from build failure; the earlier audit reported vulnerable transitive dependencies (M0 evidence). README links to missing LICENSE, uses stale UI/counts, and metadata uses `example.invalid`. Spec Kit now targets Feature 002; its renamed Devin workflows remain historical material.

## Recommended target

M5 now implements the Timing, capture coordination, Devices and journal/audit boundaries described below. SQLite stores exact packets before decoding; correction records are themselves the authoritative state transitions. Results are replayed from the journal plus audit, without a second mutable results table or checkpoint mechanism yet. A bounded background channel separates transport/storage from the UI. An event-file capture lease excludes competing capture and ordinary registration writers while timing is active. Non-capture editing retains optimistic revision checks.

Timy native USB is the sole runtime exception to .NET 10: the vendor's mixed-mode CLR 4 library runs in an isolated x64 .NET Framework 4.8 helper. It forwards both unchanged byte/text SDK fields in a versioned envelope because the current SDK's byte-array copy is defective. Storage precedes ASCII interpretation; no domain rules or persistence live in the helper. The SDK is downloaded separately, not linked into the core or redistributed. MT1 serial uses System.IO.Ports; ALGE Results uses HttpClient polling/history recovery. Windows Credential Manager is shared by the optional FIS and ALGE credentials. See [M5 evidence](m5-evidence.md), [USB acceptance](m6-evidence.md) and [device setup](timing.md).

One modular desktop process with six purposeful boundaries. Introduce modules as vertical milestones need them.

| Module | Owns | Depends on |
|---|---|---|
| Domain | Event, registration, entry, run, start-list and rule models; validation/classification | BCL only |
| Timing | Pure assignment policies, run calculations, ranking and replay | Domain |
| Application | Typed commands/queries, preview/apply, capture coordination, corrections/audit; I/O ports | Domain, Timing |
| Persistence | SQLite schema/migrations, focused stores, transactions, raw journal, audit, backup | Application, Domain |
| Integrations | Separate Devices, ImportExport and FIS modules/namespaces; transport, decoding and format conversion | Application, Domain |
| Desktop | Avalonia views/presentation models, navigation and composition | Application; adapters only at composition |

Use stable IDs and typed snapshots across boundaries, not tracked entities. Separate ownership from loading: changing one competitor must not load the whole weekend. Prefer direct methods, records and focused store interfaces over generic repositories, mediator pipelines, event buses, plugin discovery or a general rules framework. FIS stays optional. A future separate LAN/cloud publisher consumes committed result snapshots and cannot write authoritative timing state.

### Persistence and failures

Keep SQLite and EF Core for ordinary data/migrations. Each command owns a short-lived context and one explicit transaction; queries project only needed fields, following [EF Core context guidance](https://learn.microsoft.com/en-us/ef/core/dbcontext-configuration/). Replace the generic unit-of-work wrapper with an operation/session boundary with explicit commit. Never nest independently committing commands. Use conditional revision updates inside the transaction plus database constraints.

Use one local SQLite database file per event series. Each file contains exactly one series and all its competitions, registrations, entries, rules, runs, start lists, raw timing input, corrections/audit and results. It is the unit of opening, backup, transfer and recovery, independent of the OS user or machine. User preferences and recent-file paths stay outside it; authoritative race data must not depend on them. Keep series identity stable when moving or renaming the file.

Permit one application writer per database, serialize writes and use separate read contexts. Bind operations and capture sessions to the opened series file; stop capture and drain pending writes before closing or switching files. Use local disk, foreign keys, bounded busy handling, WAL and `synchronous=FULL`; verify settings and measure durable commit latency. SQLite documents the [durability tradeoff](https://sqlite.org/pragma.html#pragma_synchronous).

Provide New/Open/Close series and a consistent single-file copy for backup or transfer using the [SQLite backup API](https://sqlite.org/backup.html); do not copy only an open database's main file. A transferred file must reopen on another machine with its full race history and no source-machine dependency. Handoff continues from one authoritative copy; automatic merging of independently edited copies is outside the initial scope. Verify restores and back up each file before upgrades. Refuse unknown newer schemas. Provide recovery for locked/corrupt/full/unwritable storage. Keep bounded diagnostic logs separate from audit. Return structured expected failures; log unexpected failures with a support identifier while preserving the edit buffer.

### Timing data path

```text
transport -> durable raw journal -> adapter decoder -> normalized observations
                                                   -> timing engine
manual correction -> append-only audit ------------> recalculation
                                                   -> committed result revision
                                                   -> UI/export/optional publisher
```

Preserve exact received bytes, including malformed/unknown/duplicate input, with session/sequence and receive metadata before interpretation. Observations reference raw records and parser versions. Reprocessing adds interpretations without rewriting originals. A single ordered capture worker persists independently of rendering. Bound queues, show backlog/failure, never silently drop input or label uncommitted input as saved. Where supported, acknowledge device delivery only after durable commit. Hardware replay/backup timing remains necessary for input lost before commit or catastrophic disk failure; software must not claim otherwise.

Engine inputs include rule version, device clock/session context and committed corrections. No serial APIs, wall-clock reads, binary floating-point time arithmetic or UI callbacks occur in calculations. Save corrections with audit atomically; undo adds a correction. Checkpoints contain reproducible derived state. Restart replays committed observations/corrections after the checkpoint. Initially pause bulk imports/migrations during active capture so long writes cannot starve timing.

### Operator experience

XAML still shows two fixed sidebars consuming 520 px, wide grids/toolbars, hard-coded light surfaces mixed with system theme, inconsistent accent buttons and fixed 480×200 non-scrolling dialogs. Grid state relies on color; Refresh can clear pending changes, and edits, deletes and imports have different commit rules. Reloading after Add can lose other drafts/selection. These are source-level risks, not rendered measurements.

Use one compact event/competition/run workspace, dense virtualized participation grid, persistent context/status strip and shared typography/spacing/colors/validation. Prioritize keyboard navigation, paste, multi-cell editing, visible save/conflict state, preserved focus/selection and recoverable drafts. Show connection, capture durability and unresolved observations continuously, with text/icons as well as color. Errors must not block capture behind dialogs. Review themes, DPI, contrast, long names and small screens with an operator.

## Dependencies

Versions describe the reference, not upgrade targets. Resolve compatible supported versions and audit transitive dependencies at implementation time.

| Existing dependency | Recommendation |
|---|---|
| .NET 10; `LangVersion=latest` | Keep .NET 10 LTS; pin SDK and use its supported language version. Follow current servicing ([support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)). |
| Avalonia 11.2.3 family, DataGrid, Fluent, Inter | Retain Avalonia; validate grid behavior and target OS versions against its [platform matrix](https://docs.avaloniaui.net/docs/supported-platforms) before selecting the rewrite version. |
| CommunityToolkit.Mvvm 8.4.0 | Retain for presentation properties/commands only. |
| EF Core/SQLite 10.0.0 | Retain with corrected lifetime/transactions; keep design tooling private. Direct SQL only for a measured journal/query need; no second ORM by default. |
| Extensions DI/Logging 10.0.0 | Retain explicit composition/logging; add persistent diagnostics without a hosting framework merely for structure. |
| xUnit 2.9.3, runner 3.1.4, Test SDK 17.14.1, coverlet 6.0.4 | Retain capabilities; update as a compatible set. Coverage is diagnostic, not workflow proof. |
| FluentAssertions 6.12.2 | Preserve legacy tests; prefer xUnit assertions in new tests unless extra syntax earns its dependency. Review terms before major upgrades. |
| Avalonia Headless/XUnit, Diagnostics | Add real workflow tests using Headless; keep Diagnostics development-only. |
| FIS placeholder project; historical Spec Kit/Devin assets | Empty integration project adds no runtime value. Add real adapters when needed; retain planning evidence without requiring its ceremony. |

No device library is installed. Select transport packages after confirming hardware/firmware/interfaces and protocol samples. See [domain.md](domain.md) and [rewrite-plan.md](rewrite-plan.md).
