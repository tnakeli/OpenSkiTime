# Implementation Plan: Event Series Management

**Branch**: `001-event-series-management` | **Date**: 2026-05-28 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-event-series-management/spec.md`

## Summary

Deliver the first production-shaped slice of Open Ski Time: a Windows desktop
application that lets a race secretary create an Event Series, add competitions,
and manage a shared competitor pool with per-competition participation, fully
offline. The slice is intentionally small in feature surface but built on the
target architecture (.NET 10 LTS, Avalonia UI, SQLite via EF Core, modular
solution, MVVM) so subsequent features (timing, devices, FIS, live timing) can
be added without re-architecting.

This plan documents how the spec's requirements map onto:

- a multi-project .NET solution with strict module boundaries (UI, Domain,
  Application services, Persistence, Import, FIS placeholder),
- a relational schema in SQLite migrated by EF Core,
- a header-based TSV/Excel importer with explicit preview/apply phases,
- a unit-test-first discipline for domain, validation, importer, and
  persistence boundaries.

## Technical Context

**Language/Version**: C# 13 on .NET 10 LTS.

**Primary Dependencies**:

- Avalonia UI 11.x (desktop UI framework)
- CommunityToolkit.Mvvm 8.x (MVVM source generators, `ObservableObject`,
  `RelayCommand`)
- Microsoft.EntityFrameworkCore.Sqlite 10.x (persistence)
- Microsoft.Extensions.DependencyInjection 10.x (composition root)
- Microsoft.Extensions.Logging 10.x (structured logging)
- xUnit + FluentAssertions (unit tests)
- Avalonia.Headless.XUnit (UI smoke tests on headless platform)

**Storage**: SQLite, single file under
`%LOCALAPPDATA%\OpenSkiTime\openskitime.db` (Windows). EF Core migrations.

**Testing**: xUnit, FluentAssertions, optional Avalonia headless tests.
Coverage gates not enforced numerically in this feature; instead, every
acceptance scenario from the spec maps to ≥1 named test case (see Test Plan).

**Target Platform**: Windows 10/11 x64 desktop. Code is portable to Linux
where reasonable; Windows-only APIs are not used.

**Project Type**: Desktop application with modular library projects.

**Performance Goals** (from spec SC-002, SC-009):

- Apply a 100-row competitor import (preview → applied) in < 3 s on a typical
  race-office laptop (i5/8 GB).
- Inline grid edit visible in < 200 ms after commit.

**Constraints**:

- Fully offline-capable; the application MUST start, run, and persist data
  with no network.
- No outbound network requests in this feature, even from the FIS placeholder.
- No secrets or credentials in the repository.
- Domain assemblies MUST NOT reference Avalonia, EF Core, or SQLite.

**Scale/Scope**:

- Single Event Series open at a time in the UI.
- Up to ~500 competitors per Event Series in this feature (real race weekends
  are well under this; design supports more without changes).
- Up to ~20 competitions per Event Series.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| # | Principle | Status | Evidence |
|---|---|---|---|
| I | Race-Office Practicality | PASS | Every spec User Story is grounded in a real race-office workflow; the plan keeps the import-paste-confirm loop as the central interaction. |
| II | Modern, Compact UX (No SkiAlp Pro) | PASS | UI built in Avalonia with a SaaS-style shell (left nav, content pane, dense data grid). No legacy SkiAlp Pro UI patterns are reproduced. English-only, with localization-ready string layer (`IStringLocalizer`-style facade) deferred to feature 002+. |
| III | Modular Architecture & SoC | PASS | Solution layout below: `Domain`, `Application`, `Persistence`, `Import`, `Fis.Placeholder`, `Desktop` (UI), with `Domain` having zero outward dependencies. Live timing and device modules deliberately absent in this feature; their seams (interfaces in `Application`) are not added until needed (YAGNI). |
| IV | Offline-First, Cloud-Optional | PASS | SQLite local file; no HTTP client wired in this feature. FIS placeholder explicitly performs zero network calls (FR-011, SC-007). |
| V | Tested, Documented, Reviewable | PASS | Test Plan section enumerates xUnit projects per module; documentation tasks (README + ADRs) are explicit in the Tasks phase. |
| VI | Secrets Hygiene | PASS | No FIS or cloud credentials are introduced; `.gitignore` will exclude `appsettings.Local.json` and any `*.secrets.json`. |
| VII | Data Import Fidelity | PASS | Import design: header-based, present-columns-only, uppercase last name, separate-name preferred + secondary `Name` parser with mandatory review, accepted truthy set `{Yes, Kyllä, x}` case-insensitive. See Import Workflow section. |

**Initial gate result**: PASS, no violations to justify in Complexity Tracking.

(Re-check after Phase 1 design at the bottom of this file.)

## Project Structure

### Documentation (this feature)

```text
specs/001-event-series-management/
├── plan.md              # This file
├── research.md          # Phase 0 output
├── data-model.md        # Phase 1 output
├── quickstart.md        # Phase 1 output
├── contracts/           # Phase 1 output (interface contracts as C# signatures)
│   ├── importer.md
│   ├── persistence.md
│   └── fis-placeholder.md
├── checklists/
│   └── requirements.md  # produced by /speckit.specify
└── tasks.md             # produced by /speckit.tasks
```

### Source Code (repository root)

```text
OpenSkiTime.sln
src/
├── OpenSkiTime.Domain/                # Pure C#. No Avalonia, no EF Core.
│   ├── EventSeries/
│   ├── Competitions/
│   ├── Competitors/
│   ├── Participation/
│   ├── Categories/                    # Configurable category rule model
│   ├── Common/                        # Value objects, primitives (UpperCaseName, RaceCode)
│   └── OpenSkiTime.Domain.csproj
├── OpenSkiTime.Application/           # Use cases, ports (interfaces). No EF Core, no UI.
│   ├── EventSeries/
│   ├── Competitions/
│   ├── Competitors/
│   ├── Import/                        # IImportPreviewService, IImportApplyService
│   ├── Abstractions/                  # IEventSeriesRepository, IUnitOfWork, IClock
│   └── OpenSkiTime.Application.csproj
├── OpenSkiTime.Persistence/           # EF Core + SQLite. Implements Application ports.
│   ├── Configurations/                # EF entity type configurations
│   ├── Migrations/
│   ├── OpenSkiTimeDbContext.cs
│   ├── Repositories/
│   └── OpenSkiTime.Persistence.csproj
├── OpenSkiTime.Import/                # Header-based TSV/Excel paste parser + preview engine.
│   ├── Tsv/                           # TSV tokenizer
│   ├── Headers/                       # Header normalization & mapping
│   ├── Names/                         # Last/First parser; combined-Name best-effort parser
│   ├── Participation/                 # Truthy-value matcher
│   ├── Preview/                       # ImportPreview model + diff engine
│   └── OpenSkiTime.Import.csproj
├── OpenSkiTime.Fis.Placeholder/       # Empty-by-design module: defines IFisCompetitionUpdater.
│   ├── IFisCompetitionUpdater.cs
│   ├── NotImplementedFisUpdater.cs    # Returns "not yet available"; no HTTP.
│   └── OpenSkiTime.Fis.Placeholder.csproj
└── OpenSkiTime.Desktop/               # Avalonia app. Composition root.
    ├── App.axaml(.cs)
    ├── Program.cs                      # DI container wiring
    ├── Shell/                          # Main window, navigation
    ├── Views/
    │   ├── EventSeriesOverviewView.axaml
    │   ├── CompetitorGridView.axaml
    │   ├── CompetitionEditorView.axaml
    │   └── ImportPreviewView.axaml
    ├── ViewModels/                     # MVVM via CommunityToolkit.Mvvm
    ├── Controls/                       # Reusable cells, toolbars
    ├── Services/                       # IDialogService, IClipboardService, etc.
    └── OpenSkiTime.Desktop.csproj

tests/
├── OpenSkiTime.Domain.Tests/
├── OpenSkiTime.Application.Tests/
├── OpenSkiTime.Persistence.Tests/      # SQLite in-memory or temp-file integration
├── OpenSkiTime.Import.Tests/
└── OpenSkiTime.Desktop.Tests/          # Optional Avalonia.Headless smoke tests

docs/
├── architecture.md                     # Module map (mirrors src/ layout)
├── adr/
│   ├── 0001-net10-avalonia-sqlite.md
│   ├── 0002-efcore-over-dapper.md
│   └── 0003-mvvm-with-community-toolkit.md
└── screenshots/                        # Placeholder PNGs referenced from README

.editorconfig
.gitattributes
.gitignore
README.md
Directory.Packages.props                 # Centralized NuGet versions
Directory.Build.props                    # Common csproj settings (LangVersion, Nullable, TreatWarningsAsErrors)
```

**Dependency direction (enforced by csproj references and verified in code review):**

```text
Desktop ──▶ Application ──▶ Domain
   │            │
   │            ├──▶ Fis.Placeholder
   │            └──▶ Import
   │
   └──▶ Persistence ──▶ Application (implements ports) ──▶ Domain
```

`Domain` references nothing in this solution. `Application` references only
`Domain`. `Persistence` and `Import` reference `Application` + `Domain`.
`Desktop` is the only project allowed to reference everything; it is the
composition root.

**Structure Decision**: Modular .NET solution under `src/` and `tests/`,
single Avalonia desktop entry point, EF Core / SQLite isolated to
`OpenSkiTime.Persistence`. This matches Constitution Principle III without
introducing layers we do not yet need (no microservices, no separate live-
timing project — those land in later features).

## Domain Model (high level — see data-model.md for fields)

- **EventSeries** (aggregate root) owns its **Competitions** and
  **Competitors**.
- **Competition** belongs to one EventSeries.
- **Competitor** belongs to one EventSeries.
- **Participation** is a join: (CompetitorId, CompetitionId) → bool.
- **Category** is computed via `ICategoryResolver` from `(YearOfBirth,
  Gender)` and a `CategoryRuleSet` configured per Event Series.
- **UpperCaseName** is a value object guaranteeing the last-name uppercase
  invariant at the type level (FR-023, FR-024).
- All identifiers are GUIDs (no auto-increment) to remain stable across
  imports and future sync features.

Validation rules from spec (FR-012, FR-013, FR-021, FR-022, etc.) live in
`Domain` as guard methods on entity factories / value objects, and are
re-checked at the Application layer for use-case-level errors (e.g.,
duplicate codes within an Event Series).

## Persistence Model

- One SQLite file per OS user account. Path resolved via
  `Environment.SpecialFolder.LocalApplicationData` →
  `OpenSkiTime\openskitime.db`.
- EF Core code-first; migrations checked into `OpenSkiTime.Persistence/Migrations/`.
- Tables (see data-model.md for columns):
  - `EventSeries`, `Competitions`, `Competitors`, `Participations`,
    `CategoryRules` (per Event Series), `__EFMigrationsHistory`.
- Indexes:
  - `Competitors (EventSeriesId, Code)` UNIQUE — enforces FR-021's
    competitor-code uniqueness within an Event Series.
  - `Participations (EventSeriesId, CompetitorId, CompetitionId)` UNIQUE.
  - `Competitions (EventSeriesId, Date)` for grid sorting.
- Date fields use SQLite `TEXT` ISO-8601 date-only (no time component) to
  satisfy FR-003 and the local-timezone edge case in the spec.
- Concurrency: single-process desktop app; no row versioning needed in this
  feature.

## UI Screens (Avalonia + MVVM)

1. **Shell** — main window, left navigation (Event Series picker / "New
   Event Series"), content pane.
2. **Event Series Overview** (`EventSeriesOverviewView`)
   - Read/edit Event Series basic data (name, location, organizer, dates,
     nation, season).
   - Competitions list with inline status (missing required data).
   - Competitor count and "K with missing data" summary (FR-070, FR-071).
   - Buttons: "New Competition", "Open Competitor Grid".
3. **Competitor Management Grid** (`CompetitorGridView`)
   - Virtualized data grid; one row per competitor; columns include
     standard fields + per-competition participation columns labeled with
     the competition's short label (FR-031).
   - Toolbar: paste-from-clipboard button → opens Import Preview;
     view-mode dropdown (Flat / Group by Category / Club / Nation —
     FR-044); filters (category, nation, club, "participates in
     <competition>", "missing required data" — FR-043, FR-072).
   - Inline editing with debounced commit; last name displayed uppercase
     regardless of user typing (FR-023).
4. **Import Preview** (`ImportPreviewView`)
   - Tabs: New, Updated, Unchanged, Errors, Uncertain Names, Unknown
     Columns (FR-056).
   - Per-row inline edit for uncertain name splits (FR-055, US3).
   - Mode toggle: "Do not overwrite with empty" (default) vs "Overwrite
     with empty" (FR-052, FR-053).
   - Buttons: Apply / Cancel (FR-057).
5. **Competition Basic Data Editor** (`CompetitionEditorView`)
   - Form for all competition fields.
   - Disabled-but-visible "Update from FIS API" button with tooltip and
     status banner "Not yet available — coming in a later release"
     (FR-011, SC-007).

## Import Workflow

```text
[Clipboard text]
   │
   ▼
TsvTokenizer  ──▶ raw rows + headers (CR/LF, tab, comma fallback)
   │
   ▼
HeaderMapper  ──▶ recognized header → CompetitorField | ParticipationKey | Unknown
   │              (case-insensitive, trimmed; "Last Name" / "First Name"
   │               preferred; "Name" mapped only if neither present)
   ▼
RowProjector ──▶ per-row CompetitorImportRow
   │              + name disposition: Separate | ParsedFromName(Uncertain)
   │              + ParticipationCellMatcher: {Yes, Kyllä, x} case-insensitive
   ▼
DiffEngine    ──▶ ImportPreview {New[], Updated[], Unchanged[], Errors[],
   │                              UncertainNames[], UnknownColumns[]}
   │              Match strategy:
   │                1. by Code (preferred)
   │                2. fallback: (UpperLastName, FirstName, YearOfBirth, Gender)
   │                   — flagged "matched without code"
   ▼
[User reviews preview, optionally edits uncertain names, picks Overwrite mode]
   │
   ▼
ImportApplyService (transactional)
   │  - For Updated rows: write only columns present in the source.
   │  - Empty cells in present columns: ignored unless OverwriteWithEmpty.
   │  - Last name normalized to UpperCaseName on the way in.
   │  - Participation: write true on truthy match; on empty, leave existing
   │    value untouched (default mode); never silently set false.
   ▼
[DB committed; Grid refreshed]
```

Key design rules:

- The importer is a **pure function** of (input text, current Event Series
  snapshot, options) → ImportPreview. No DB writes happen during preview.
  This makes it cheap to unit-test and re-runnable as the user toggles
  options.
- Apply is the only DB write step and runs inside one EF Core transaction.

## Validation Rules (mapped to spec FRs)

| Rule | Source | Layer |
|---|---|---|
| Competition: name, date, discipline, race type, runs, intermediates required | FR-012 | Domain factory + Application use case |
| FIS race ⇒ FIS code required | FR-013 | Domain (`Competition.SetRaceType` guard) |
| Last name: required, uppercase | FR-021, FR-023 | `UpperCaseName` value object |
| First name: required | FR-021 | Domain |
| Competitor: code, gender, year, nation required for "usable for race entry" | FR-021 | Domain `Competitor.IsUsableForRaceEntry` |
| Year of birth plausibility (1900 ≤ y ≤ current year) | Edge cases | Domain |
| Gender within configured set | Edge cases | Domain |
| Code unique within Event Series | FR-021 + DB index | Application + Persistence |
| Participation truthy = {Yes, Kyllä, x}, case-insensitive | FR-032 | `ParticipationValueMatcher` (Import) |
| Empty values do not overwrite by default | FR-033, FR-052 | `ImportApplyService` |
| Single `Name` only ⇒ uncertain, mandatory review | FR-055 | Import preview |

## Test Plan

Tests are written before or alongside the production code they cover (no
test-after-the-fact for new behavior in this feature). Each spec acceptance
scenario maps to ≥1 test, named after its FR or scenario.

### `OpenSkiTime.Domain.Tests`

- `UpperCaseNameTests`: rejects null/empty, normalizes to uppercase,
  preserves diacritics, case-insensitive equality, FR-023.
- `EventSeriesTests`: required basic data, add/remove competition, add/
  remove competitor.
- `CompetitionTests`: required-fields guard (FR-012), FIS-code-required-on-
  FIS-race (FR-013), allows blank FIS code on Club/Training/National.
- `CompetitorTests`: `IsUsableForRaceEntry` true/false matrix, year-of-
  birth range, gender enum.
- `CategoryResolverTests`: simple rule set produces expected category for
  given year/gender pairs.
- `ParticipationTests`: idempotent toggle, scoped to one Event Series.

### `OpenSkiTime.Application.Tests`

- `CreateEventSeriesUseCaseTests`
- `AddCompetitionUseCaseTests` (incl. FIS-code rule)
- `EditCompetitorUseCaseTests` (incl. uppercase last-name on edit, FR-023)
- `EventSeriesValidationSummaryTests` (FR-070, FR-071)
- `CompetitorCodeUniqueWithinSeriesTests`

### `OpenSkiTime.Import.Tests` (largest set — heart of FR-Import block)

- `TsvTokenizerTests`: tab/comma/CRLF/LF/quoted/whitespace.
- `HeaderMapperTests`: case-insensitive, trimmed; recognizes the FR-051
  header set; Last/First prefer over Name when both present.
- `NameParserTests`:
  - separate columns → no parse, last name uppercased.
  - combined `Name` → best-effort split, every row flagged uncertain
    (FR-055).
  - tricky cases: "VAN DER POEL Jeroen", "Mc Donald Sean", multi-token
    last names — assert behavior is *deterministic* and flagged uncertain.
- `ParticipationValueMatcherTests`: matches `Yes`, `YES`, `yes`, `Kyllä`,
  `KYLLÄ`, `kyllä`, `x`, `X`; rejects `1`, `true`, `kyllä!`, empty.
- `DiffEngineTests`: New/Updated/Unchanged/Error/UncertainName/Unknown
  buckets correct; match-by-code preferred; fallback match flagged.
- `EmptyDoesNotOverwriteTests` (FR-033, FR-052, FR-053): default mode and
  overwrite mode both verified.
- `ParticipationColumnHeaderTests`: header `3.1 SL` mapped to the
  competition with that label; unknown labels go to UnknownColumns.

### `OpenSkiTime.Persistence.Tests`

Uses a temp-file SQLite DB per test; runs migrations.

- `SchemaMigrationTests`: migrations apply cleanly to an empty DB.
- `EventSeriesRepositoryTests`: round-trip with competitions, competitors,
  participations.
- `CompetitorCodeUniqueIndexTests`: DB raises constraint violation.
- `ImportApplyTransactionTests`: failure mid-apply rolls back fully.

### `OpenSkiTime.Desktop.Tests` (lightweight)

- `FisPlaceholderButtonTests` (Avalonia.Headless): clicking the button
  raises a "not yet available" status and **no `HttpClient` is constructed**
  during the click (verified by registering a tracking factory).
  Maps directly to SC-007.

### Cross-cutting

- `OfflineSmokeTest`: with all network adapters policy-disabled (or via a
  test `IHttpClientFactory` that throws), the application starts, creates
  an Event Series, imports 100 rows, and quits cleanly. SC-005.

## Documentation Updates (deliverables of this feature)

- **`README.md`** (new): what Open Ski Time is, MIT license, Windows
  prerequisites, build/run commands, screenshot placeholders for:
  - Event Series Overview
  - Competitor Management Grid
  - Competition Basic Data Editor
  - Import Preview

  References the constitution and lists what's *not* in this release
  (timing, Alge, FIS API, live timing).

- **`docs/architecture.md`** (new): module map mirroring `src/`, dependency
  arrows, and the import workflow diagram.

- **ADRs** (new):
  - `docs/adr/0001-net10-avalonia-sqlite.md` — affirms constitution
    defaults; records research findings.
  - `docs/adr/0002-efcore-over-dapper.md` — chosen for migrations and
    change-tracking ergonomics; performance is not a constraint at this
    scale.
  - `docs/adr/0003-mvvm-with-community-toolkit.md` — chosen over hand-
    rolled INotifyPropertyChanged for boilerplate reduction.

- **Constitution Sync Impact Report** in `.specify/memory/constitution.md`
  is **not** changed by this feature (no principle change). The README TODO
  recorded in the constitution's Sync Impact Report is closed by this
  feature's README task.

## Phase 0 — Research

See [research.md](./research.md). All NEEDS CLARIFICATION items resolved;
none remained from the spec (clarifications were already converted into
spec assumptions during `/speckit.specify`).

## Phase 1 — Design & Contracts

- [data-model.md](./data-model.md): entities, fields, relationships,
  validation rules, and SQLite mapping notes.
- [contracts/importer.md](./contracts/importer.md): `IImportPreviewService`,
  `IImportApplyService`, `ImportPreview`, `ImportApplyOptions`.
- [contracts/persistence.md](./contracts/persistence.md):
  `IEventSeriesRepository`, `IUnitOfWork`, `IClock`.
- [contracts/fis-placeholder.md](./contracts/fis-placeholder.md):
  `IFisCompetitionUpdater` and the `NotImplementedFisUpdater` behavior.
- [quickstart.md](./quickstart.md): how to clone, build, run tests, run
  the app, and reset the local database.

### Agent context update

`.windsurf/rules/specify-rules.md` is updated to reference this plan file
between the `<!-- SPECKIT START -->` / `<!-- SPECKIT END -->` markers.

## Constitution Re-Check (post-design)

| # | Principle | Status | Note |
|---|---|---|---|
| I  | Race-Office Practicality | PASS | Design preserves paste-preview-apply as the primary loop. |
| II | Modern, Compact UX | PASS | Avalonia screens are dense, keyboard-friendly; no SkiAlp Pro patterns. |
| III| Modular Architecture | PASS | `Domain` has zero outward refs; verified at csproj level. |
| IV | Offline-First | PASS | No HTTP client wired; FIS placeholder tested for zero outbound calls. |
| V  | Tested, Documented | PASS | Test Plan + Documentation Updates sections explicit. |
| VI | Secrets Hygiene | PASS | No credentials introduced; `.gitignore` updated for local config. |
| VII| Data Import Fidelity | PASS | All FR-Import items mapped to importer module + tests. |

**Result**: PASS. No violations to record in Complexity Tracking.

## Complexity Tracking

> No constitution violations. This section intentionally left empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| (none) | — | — |
