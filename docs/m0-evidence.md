# M0 legacy reference evidence (partial)

Reference: `legacy-pre-codex` at `8259809`; M0 tests run on `rewrite/codex`. This is evidence about the existing application, not acceptance of every legacy behavior.

## Environment and checks

- Windows x64, .NET SDK 10.0.401 / runtime 10.0.12, EF CLI 10.0.0. The complete `OpenSkiTime.slnx` restored, built and tested. Commands here: `dotnet restore OpenSkiTime.slnx --configfile ../work/NuGet.offline.config -p:NuGetAudit=false`, `dotnet build OpenSkiTime.slnx --no-restore`, `dotnet test OpenSkiTime.slnx --no-build --no-restore`. The restore used locally cached official NuGet packages because the Windows TLS stack cannot reach NuGet directly; the prior successful NuGet audit reported vulnerable transitive packages. The build's zero-warning count therefore does not certify dependencies as safe.
- Baseline before M0 tests: 234 passing tests; no failing tests. The Desktop test assembly discovers **zero** tests. Existing persistence tests use `EnsureCreated`, so their success alone says nothing about migrations. README's claim of 168 tests is stale.
- Two M0 characterization tests now exercise real on-disk SQLite migrations and a use-case/import/reopen path. Their synthetic names and dates are fixtures, not race results. Both pass; the full suite now has 236 passing tests and no failures. Intermediate test failures were only test cleanup attempting to delete pooled SQLite files; clearing the pools resolved them.
- The exact desktop executable initially failed to create `%LOCALAPPDATA%\OpenSkiTime` due to sandbox permissions. After permission to create that folder was granted, it started and reported an `Open Ski Time` window. Its new SQLite file was opened read-only after startup: both migrations are recorded, the five business tables exist, all contain zero rows, and `PRAGMA integrity_check` returns `ok`. `PRAGMA foreign_key_list(Participations)` shows a competitor FK but no competition FK. This is a fresh, blank startup database, not an operator database. The process was closed after inspection. This session's UI tool does not expose native Windows app windows, so no desktop screenshot, mouse/keyboard workflow or application-session reopening could be verified.

## Verified behavior

| Workflow | Evidence and limit |
|---|---|
| Event Series create/edit | M0 test uses production use cases, then reopens with a new context: edited name and identity persist. UI form behavior unverified. |
| Competition create/edit | Same migrated-file test creates a competition, updates its name and reopens it. UI editor unverified. |
| Competitor, bib and participation | TSV preview/apply through production services adds `Müller` as `MÜLLER`, bib 7 and participation in `3.1 SL`. Separate production use cases add `Korhonen`, edit the surname to `VIRTANEN`, and toggle participation true then false. All final values survive reopening. Manual competitor editing is not reached from the shell UI. |
| Categories | A domain `CategoryRule` is saved and reloaded; its inclusive year/gender matcher returns the expected result. No production UI category editor/resolver was exercised or found. |
| SQLite migration | Migrate a test file to `0001_Initial`, write a series/competition, upgrade to `0002_Competitors_Participation_Categories`, migrate again, then reopen. Both migration IDs are applied once, no pending migration remains, original rows survive, and new tables are queryable. Separately, the real desktop startup created a fresh database with both migrations. Neither check covers an unknown operator database. |

## Classification for the rewrite

| Classification | Findings |
|---|---|
| Correct reference behavior | Series/competition metadata survives SQLite reopening; surnames normalize to uppercase; a reviewed simple TSV row can add a competitor, bib and participation; category year bounds are inclusive. Preserve these concepts with new acceptance tests. |
| Known defects | `ImportApplyService` saves in stages and has no transaction over the reviewed batch. `ImportViewModel.ApplyAsync` recalculates a diff from potentially changed text/series. `RowVersion` is not an EF concurrency token. Migration 0002 has no competition FK on `Participations`; code/bib uniqueness is not enforced by SQLite. These are source/schema findings, not passing correctness claims. |
| Unfinished | `ShellViewModel` never navigates to `CompetitorGridViewModel`; that grid is read-only and its filter/group properties do not change rows. Category classification is not connected to a production editor/grid. No timing, ALGE, result calculation or result export exists. The Desktop test project is empty. |
| Needs human decision | Competition versus series bib scope, competitor identity/readiness fields, category overlap priority and age basis, and actual FIS validation/race rules need operator/rulebook decisions. Do not infer them from legacy fields. |

The [README](../README.md) labels Feature 001 shipped and claims optimistic locking; the executable paths and checks above support a narrower state. Historical [Feature 001](../specs/001-event-series-management/spec.md) additionally specifies editable/filterable/grouped competitor participation, category assignment, personal-data update previews and explicit blank overwrite; these are not established by the current UI or M0 tests. Treat the specification as intent, with the confirmed gaps tracked in [architecture.md](architecture.md). Do not execute its old workflow or use checked tasks as proof.

M0 remains open until the actual desktop screens and keyboard/mouse workflows can be exercised in an interactive Windows session. Service and database checks above are reproducible reference evidence, not a substitute for that UI review.

For that review, use synthetic data: create a series named `M0 Test`, add a `3.1 SL` Club competition, edit both names, paste `LastName\tFirstName\tYOB\tBib\t3.1 SL` with a row `Müller\tHannes\t2007\t7\tx` (actual tabs), inspect Preview, Apply, then close and reopen the application. Record what remains visible, any errors, and whether competitor, participation and category editing can be reached from the shell. Do not infer successful persistence from a status message alone; read the resulting SQLite file separately.
