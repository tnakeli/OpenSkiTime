---
description: "Task list for feature 001-event-series-management"
---

# Tasks: Event Series Management

**Input**: Design documents from `/specs/001-event-series-management/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md (all present)

**Tests**: REQUIRED. The user prompt explicitly asks for "all required unit tests"; the constitution (Principle V) bans behavior changes without matching tests. Tests are written before or alongside the production code they cover.

**Organization**: Tasks are grouped by user story so each story can be implemented and demoed as an independent increment.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: Which user story this task belongs to (US1..US5); omitted for Setup/Foundational/Polish phases
- Every task includes the exact file path it touches

## Path Conventions

Multi-project .NET solution per plan.md `Project Structure`:

- Source: `src/OpenSkiTime.<Module>/`
- Tests: `tests/OpenSkiTime.<Module>.Tests/`
- Solution file: `OpenSkiTime.sln` at repo root
- Docs: `docs/`, `README.md` at repo root

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Solution scaffolding and repo hygiene. Independent of every user story.

- [x] T001 Create `OpenSkiTime.sln` at repo root (`dotnet new sln -n OpenSkiTime`)
- [x] T002 [P] Add `.gitignore` at repo root with .NET / Visual Studio / Rider / `appsettings.Local.json` / `*.secrets.json` / `bin/` / `obj/` / `.vs/` patterns
- [x] T003 [P] Add `.gitattributes` at repo root: `* text=auto eol=lf` and `*.{cs,csproj,sln,axaml,xaml,md,json,yml} text` plus `*.{ps1} text eol=crlf`
- [x] T004 [P] Add `.editorconfig` at repo root: 4-space indent for C#, file-scoped namespaces, `dotnet_diagnostic.CA*` rules at warning, `csharp_style_namespace_declarations = file_scoped:warning`
- [x] T005 Add `Directory.Build.props` at repo root pinning: `<TargetFramework>net10.0</TargetFramework>`, `<LangVersion>latest</LangVersion>`, `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`, `<ImplicitUsings>enable</ImplicitUsings>`
- [x] T006 Add `Directory.Packages.props` at repo root with central NuGet versions for: `Avalonia` 11.x, `Avalonia.Desktop` 11.x, `Avalonia.Themes.Fluent` 11.x, `Avalonia.ReactiveUI` (NOT used — omit), `CommunityToolkit.Mvvm` 8.x, `Microsoft.EntityFrameworkCore.Sqlite` 10.x, `Microsoft.EntityFrameworkCore.Design` 10.x, `Microsoft.Extensions.DependencyInjection` 10.x, `Microsoft.Extensions.Logging` 10.x, `Microsoft.Extensions.Logging.Debug` 10.x, `xunit` 2.x, `xunit.runner.visualstudio` 2.x, `Microsoft.NET.Test.Sdk` 17.x, `FluentAssertions` 6.x, `Avalonia.Headless.XUnit` 11.x; set `<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>`
- [x] T007 Create empty source project folders: `src/OpenSkiTime.Domain/`, `src/OpenSkiTime.Application/`, `src/OpenSkiTime.Persistence/`, `src/OpenSkiTime.Import/`, `src/OpenSkiTime.Fis.Placeholder/`, `src/OpenSkiTime.Desktop/`
- [x] T008 Create empty test project folders: `tests/OpenSkiTime.Domain.Tests/`, `tests/OpenSkiTime.Application.Tests/`, `tests/OpenSkiTime.Persistence.Tests/`, `tests/OpenSkiTime.Import.Tests/`, `tests/OpenSkiTime.Desktop.Tests/`
- [x] T009 [P] Create `src/OpenSkiTime.Domain/OpenSkiTime.Domain.csproj` (classlib, no dependencies)
- [x] T010 [P] Create `src/OpenSkiTime.Application/OpenSkiTime.Application.csproj` referencing `OpenSkiTime.Domain`
- [x] T011 [P] Create `src/OpenSkiTime.Persistence/OpenSkiTime.Persistence.csproj` referencing `OpenSkiTime.Application`, `OpenSkiTime.Domain` and packages `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`, `Microsoft.Extensions.DependencyInjection`
- [x] T012 [P] Create `src/OpenSkiTime.Import/OpenSkiTime.Import.csproj` referencing `OpenSkiTime.Application`, `OpenSkiTime.Domain`
- [x] T013 [P] Create `src/OpenSkiTime.Fis.Placeholder/OpenSkiTime.Fis.Placeholder.csproj` referencing `OpenSkiTime.Domain` only (NO HttpClient package, NO `System.Net.Http` reference)
- [x] T014 Create `src/OpenSkiTime.Desktop/OpenSkiTime.Desktop.csproj` (`<OutputType>WinExe</OutputType>`, references all of the above + Avalonia/CommunityToolkit packages)
- [x] T015 [P] Create each test csproj (`tests/<Module>.Tests/<Module>.Tests.csproj`) referencing the corresponding source project + `xunit`, `xunit.runner.visualstudio`, `FluentAssertions`, `Microsoft.NET.Test.Sdk`. The Desktop tests project additionally references `Avalonia.Headless.XUnit`
- [x] T016 Add all 11 csproj files to `OpenSkiTime.sln` (`dotnet sln add ...`)
- [x] T017 Verify clean build: `dotnet build` from repo root succeeds with zero warnings

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Cross-cutting primitives every user story depends on. CRITICAL — no user story may start until this phase is complete.

### Domain primitives

- [x] T018 [P] Create `src/OpenSkiTime.Domain/Common/Discipline.cs` defining `enum Discipline { SL, GS, SG, DH, AC, KOMBI, OTHER }`
- [x] T019 [P] Create `src/OpenSkiTime.Domain/Common/Gender.cs` defining `enum Gender { Male, Female, Other }`
- [x] T020 [P] Create `src/OpenSkiTime.Domain/Common/RaceType.cs` defining `enum RaceType { FIS, National, Club, Training }`
- [x] T021 [P] Create `src/OpenSkiTime.Domain/Common/UpperCaseName.cs` value object enforcing FR-023 (non-empty, trimmed, `ToUpperInvariant`); implicit `string` conversion; equality by value
- [x] T022 [P] Create `tests/OpenSkiTime.Domain.Tests/Common/UpperCaseNameTests.cs` covering: rejects null/empty/whitespace, normalizes to uppercase, trims, preserves diacritics ("Ärm" → "ÄRM"), case-insensitive equality

### Application abstractions

- [x] T023 [P] Create `src/OpenSkiTime.Application/Abstractions/IClock.cs` exposing `DateOnly Today()` and `DateTime UtcNow()`
- [x] T024 [P] Create `src/OpenSkiTime.Application/Abstractions/IUnitOfWork.cs` per `contracts/persistence.md`
- [x] T025 Create `src/OpenSkiTime.Application/Abstractions/IEventSeriesRepository.cs` and `EventSeriesSummary`, `EventSeriesSnapshot` records per `contracts/persistence.md`

### Persistence skeleton

- [x] T026 Create `src/OpenSkiTime.Persistence/OpenSkiTimeDbContext.cs` with empty `DbSet<>`s placeholders (will be filled per-entity in user-story phases) and `OnModelCreating` calling `ApplyConfigurationsFromAssembly`
- [x] T027 Create `src/OpenSkiTime.Persistence/PersistenceServiceCollectionExtensions.cs` exposing `AddOpenSkiTimePersistence(string sqliteFilePath)` per `contracts/persistence.md`
- [x] T028 Create `tests/OpenSkiTime.Persistence.Tests/Infrastructure/TempSqliteFixture.cs` test helper (creates temp DB file, applies migrations, deletes on dispose)

### FIS placeholder skeleton

- [x] T029 [P] Create `src/OpenSkiTime.Fis.Placeholder/IFisCompetitionUpdater.cs` and `FisUpdateResult` discriminated record per `contracts/fis-placeholder.md`
- [x] T030 [P] Create `src/OpenSkiTime.Fis.Placeholder/NotImplementedFisUpdater.cs` returning `FisUpdateResult.NotImplemented("FIS API update is not yet available in this release.")` with no I/O

### Desktop shell

- [x] T031 Create `src/OpenSkiTime.Desktop/Program.cs` with Avalonia `BuildAvaloniaApp` + `Microsoft.Extensions.DependencyInjection` composition root resolving SQLite path from `Environment.SpecialFolder.LocalApplicationData` → `OpenSkiTime/openskitime.db` and registering `IClock` (system clock) and FIS placeholder. **Do NOT register `HttpClient` / `IHttpClientFactory`.**
- [x] T032 Create `src/OpenSkiTime.Desktop/App.axaml` and `App.axaml.cs` (Fluent theme; `OnFrameworkInitializationCompleted` shows the Shell window)
- [x] T033 Create `src/OpenSkiTime.Desktop/Shell/ShellWindow.axaml` and `.axaml.cs` (left navigation, content pane; empty by default — populated in user-story phases)
- [x] T034 Create `src/OpenSkiTime.Desktop/Services/IDialogService.cs` and `DialogService.cs` (message/confirm/error)
- [x] T035 Create `src/OpenSkiTime.Desktop/Services/IClipboardService.cs` and `ClipboardService.cs` (read text from Avalonia clipboard)

### Foundational test pass

- [x] T036 Run `dotnet test` — UpperCaseName tests pass; everything else green or empty. **Checkpoint**: foundation ready.

---

## Phase 3: User Story 1 — Set up an Event Series and its competitions (Priority: P1) 🎯 MVP

**Goal**: A race secretary can create an Event Series, add competitions, save, and reopen offline.

**Independent Test**: Create an Event Series with all required basic data, add two competitions of different disciplines, close the app with the network disabled, reopen — all data is present and editable.

### Tests for User Story 1 (write FIRST, watch them fail)

- [x] T037 [P] [US1] `tests/OpenSkiTime.Domain.Tests/EventSeries/EventSeriesTests.cs` — required basic data, `EndDate >= StartDate` invariant, add/remove competition
- [x] T038 [P] [US1] `tests/OpenSkiTime.Domain.Tests/Competitions/CompetitionTests.cs` — required fields (FR-012), FIS-code-required-on-FIS-race (FR-013), allows blank FIS code on Club/National/Training, `NumberOfRuns >= 1`, `NumberOfIntermediateTimes >= 0`
- [x] T039 [P] [US1] `tests/OpenSkiTime.Application.Tests/EventSeries/CreateEventSeriesUseCaseTests.cs`
- [x] T040 [P] [US1] `tests/OpenSkiTime.Application.Tests/Competitions/AddCompetitionUseCaseTests.cs` (incl. FIS code rule)
- [x] T041 [P] [US1] `tests/OpenSkiTime.Persistence.Tests/EventSeriesRepositoryTests.cs` — round-trip with competitions; cascade delete

### Implementation for User Story 1

#### Domain

- [x] T042 [P] [US1] Create `src/OpenSkiTime.Domain/EventSeries/EventSeries.cs` (Id, Name, Location, Organizer, StartDate, EndDate, Nation, Season, owned `Competitions`, `Competitors`, `CategoryRules`, `RowVersion`) per `data-model.md`
- [x] T043 [P] [US1] Create `src/OpenSkiTime.Domain/Competitions/Competition.cs` with all fields per `data-model.md`; factory enforces FR-012 + FR-013

#### Application

- [x] T044 [US1] Create `src/OpenSkiTime.Application/EventSeries/CreateEventSeriesUseCase.cs` (depends on T042, T024, T025)
- [x] T045 [US1] Create `src/OpenSkiTime.Application/EventSeries/UpdateEventSeriesUseCase.cs`
- [x] T046 [US1] Create `src/OpenSkiTime.Application/EventSeries/ListEventSeriesUseCase.cs`
- [x] T047 [US1] Create `src/OpenSkiTime.Application/Competitions/AddCompetitionUseCase.cs` (depends on T043)
- [x] T048 [US1] Create `src/OpenSkiTime.Application/Competitions/UpdateCompetitionUseCase.cs`

#### Persistence

- [x] T049 [P] [US1] Create `src/OpenSkiTime.Persistence/Configurations/EventSeriesConfiguration.cs` (EF type config; `RowVersion` as `long`)
- [x] T050 [P] [US1] Create `src/OpenSkiTime.Persistence/Configurations/CompetitionConfiguration.cs` with indexes per `data-model.md` (`IX_Competitions_EventSeriesId_Date`, `UQ_Competitions_EventSeriesId_ShortLabel`)
- [x] T051 [US1] Add `DbSet<EventSeries>` and `DbSet<Competition>` to `OpenSkiTimeDbContext`; implement `RowVersion` increment in `SaveChangesAsync`
- [x] T052 [US1] Create `src/OpenSkiTime.Persistence/Repositories/EventSeriesRepository.cs` implementing `IEventSeriesRepository`
- [x] T053 [US1] Create initial EF migration `0001_Initial` containing EventSeries + Competitions tables (`dotnet ef migrations add 0001_Initial --project src/OpenSkiTime.Persistence --startup-project src/OpenSkiTime.Desktop`)
- [x] T054 [US1] Apply migrations on startup in `src/OpenSkiTime.Desktop/Program.cs` (`db.Database.Migrate()` inside scope)

#### UI

- [x] T055 [P] [US1] Create `src/OpenSkiTime.Desktop/ViewModels/EventSeriesOverviewViewModel.cs` using `[ObservableProperty]` / `[RelayCommand]`
- [x] T056 [US1] Create `src/OpenSkiTime.Desktop/Views/EventSeriesOverviewView.axaml(.cs)` showing Event Series basic data form, competitions list, "New Competition" button
- [x] T057 [P] [US1] Create `src/OpenSkiTime.Desktop/ViewModels/CompetitionEditorViewModel.cs` (basic-data form fields)
- [x] T058 [US1] Create `src/OpenSkiTime.Desktop/Views/CompetitionEditorView.axaml(.cs)` (form for all competition basic-data fields per `data-model.md`)
- [x] T059 [US1] Wire `ShellWindow` left nav: "Event Series" → opens `EventSeriesOverviewView`; "+ New Event Series" command

#### Lifecycle: Delete & Remove (covers FR-001 "delete", FR-004 "remove" — added per /speckit.analyze C1, C2)

- [x] T131 [P] [US1] `tests/OpenSkiTime.Application.Tests/EventSeries/DeleteEventSeriesUseCaseTests.cs` — deletes series, asserts cascade delete of owned competitions/competitors/participations/category-rules, asserts not-found returns failure result
- [x] T132 [P] [US1] `tests/OpenSkiTime.Application.Tests/Competitions/RemoveCompetitionUseCaseTests.cs` — removes a competition, asserts cascade delete of its participation rows, asserts other competitions in the same series are untouched
- [x] T133 [US1] Create `src/OpenSkiTime.Application/EventSeries/DeleteEventSeriesUseCase.cs` (loads via `IEventSeriesRepository.GetByIdAsync`, calls `Remove`, commits via `IUnitOfWork.SaveChangesAsync`)
- [x] T134 [US1] Create `src/OpenSkiTime.Application/Competitions/RemoveCompetitionUseCase.cs` (loads parent EventSeries, removes the competition, saves; relies on EF cascade for participations)
- [x] T135 [US1] Extend `src/OpenSkiTime.Desktop/ViewModels/EventSeriesOverviewViewModel.cs` with `DeleteEventSeriesCommand` and `RemoveCompetitionCommand`, both routed through `IDialogService.ConfirmAsync` with explicit "this cannot be undone" messaging (FR-082 plain-language errors)
- [x] T136 [US1] Update `src/OpenSkiTime.Desktop/Views/EventSeriesOverviewView.axaml` to expose: a "Delete Event Series" toolbar action, and a "Remove" action per row in the competitions list (icon + tooltip, both wired to the commands above)

**Checkpoint**: User Story 1 demo-ready: create series + competitions, edit, delete (with confirm), remove individual competitions (with confirm), save, restart offline, data persists.

---

## Phase 4: User Story 2 — Manage all competitors and their per-competition participation in one place (Priority: P1)

**Goal**: Paste competitors with separate `Last Name` / `First Name` columns and per-competition participation columns; review preview; apply; sort/filter/group the grid.

**Independent Test**: With an Event Series + 3 competitions present, paste TSV of 50 competitors with headers `Code, Last Name, First Name, Year, Gender, Nation, Club, Slalom, Giant, 3.1 SL, 4.1 GS1, 6.1 GS2`; preview shows correct buckets; apply succeeds; filtering by "Participates in 4.1 GS1" returns the expected subset; "Group by Club" rearranges rows without changing data.

### Tests for User Story 2 (write FIRST)

#### Domain / Application

- [x] T060 [P] [US2] `tests/OpenSkiTime.Domain.Tests/Competitors/CompetitorTests.cs` — `IsUsableForRaceEntry` matrix (FR-021), year-of-birth range, gender enum, club may be empty (FR-022)
- [x] T061 [P] [US2] `tests/OpenSkiTime.Domain.Tests/Participation/ParticipationTests.cs` — idempotent toggle, scoped to one Event Series
- [x] T062 [P] [US2] `tests/OpenSkiTime.Domain.Tests/Categories/CategoryResolverTests.cs` — simple rule set produces expected category for `(year, gender)`
- [x] T063 [P] [US2] `tests/OpenSkiTime.Application.Tests/Competitors/EditCompetitorUseCaseTests.cs` — uppercase last name on edit (FR-023)
- [x] T064 [P] [US2] `tests/OpenSkiTime.Application.Tests/Competitors/CompetitorCodeUniqueWithinSeriesTests.cs`

#### Importer

- [x] T065 [P] [US2] `tests/OpenSkiTime.Import.Tests/Tsv/TsvTokenizerTests.cs` — tab/comma/CRLF/LF/quoted/whitespace
- [x] T066 [P] [US2] `tests/OpenSkiTime.Import.Tests/Headers/HeaderMapperTests.cs` — case-insensitive, trimmed; recognizes the FR-051 set; `Last Name` + `First Name` together cause `Name` to be ignored
- [x] T067 [P] [US2] `tests/OpenSkiTime.Import.Tests/Names/SeparateLastFirstNameTests.cs` — uses Last/First directly, last name uppercased on import
- [x] T068 [P] [US2] `tests/OpenSkiTime.Import.Tests/Participation/ParticipationValueMatcherTests.cs` — accepts `Yes`, `YES`, `yes`, `Kyllä`, `KYLLÄ`, `kyllä`, `x`, `X`; rejects `1`, `true`, `kyllä!`, empty (FR-032)
- [x] T069 [P] [US2] `tests/OpenSkiTime.Import.Tests/Participation/ParticipationColumnHeaderTests.cs` — header `3.1 SL` mapped to the matching Competition `ShortLabel`; unknown labels go to `UnknownColumns`
- [x] T070 [P] [US2] `tests/OpenSkiTime.Import.Tests/Preview/DiffEngineTests.cs` — New/Updated/Unchanged/Error/Unknown buckets correct; match-by-Code preferred; fallback `(LastName, First, Year, Gender)` flagged `MatchedBy.NameYearGenderTuple`
- [x] T071 [P] [US2] `tests/OpenSkiTime.Import.Tests/Preview/EmptyDoesNotOverwriteTests.cs` — default mode does not overwrite (FR-033, FR-052); `OverwriteWithEmpty=true` clears values (FR-053)
- [x] T072 [P] [US2] `tests/OpenSkiTime.Import.Tests/Preview/PartialUpdateTests.cs` — only columns present in the source can be updated; absent columns never produce a `FieldDelta`

#### Persistence

- [x] T073 [P] [US2] `tests/OpenSkiTime.Persistence.Tests/CompetitorCodeUniqueIndexTests.cs` — DB raises constraint violation on duplicate `(EventSeriesId, Code)`
- [x] T074 [P] [US2] `tests/OpenSkiTime.Persistence.Tests/ImportApplyTransactionTests.cs` — failure mid-apply rolls back fully

### Implementation for User Story 2

#### Domain

- [x] T075 [P] [US2] Create `src/OpenSkiTime.Domain/Competitors/Competitor.cs` per `data-model.md`; factory normalizes last name via `UpperCaseName`, validates required fields, year range, gender, code presence rules
- [x] T076 [P] [US2] Create `src/OpenSkiTime.Domain/Participation/Participation.cs` (Id, EventSeriesId, CompetitorId, CompetitionId, IsParticipating)
- [x] T077 [P] [US2] Create `src/OpenSkiTime.Domain/Categories/CategoryRule.cs` and `src/OpenSkiTime.Domain/Categories/ICategoryResolver.cs` + `RuleBasedCategoryResolver.cs` (first-match-wins on Order)

#### Application

- [x] T078 [US2] Create `src/OpenSkiTime.Application/Competitors/AddCompetitorUseCase.cs` (depends on T075)
- [x] T079 [US2] Create `src/OpenSkiTime.Application/Competitors/EditCompetitorUseCase.cs` (FR-023 enforcement on edit)
- [x] T080 [US2] Create `src/OpenSkiTime.Application/Participation/SetParticipationUseCase.cs` (depends on T076)
- [x] T081 [US2] Create `src/OpenSkiTime.Application/Import/IImportPreviewService.cs`, `IImportApplyService.cs`, `ImportPreview`, `ImportOptions`, `CompetitorDraft`, `ParticipationDraft`, `FieldDelta`, `MatchedBy`, `NewOrUpdate`, `CompetitorField` per `contracts/importer.md`

#### Importer module

- [x] T082 [P] [US2] Create `src/OpenSkiTime.Import/Tsv/TsvTokenizer.cs` (handles tab + comma fallback, CRLF/LF, quoted cells, surrounding whitespace)
- [x] T083 [P] [US2] Create `src/OpenSkiTime.Import/Headers/HeaderMapper.cs` mapping recognized headers (FR-051) + Competition `ShortLabel` to `MappedField`
- [x] T084 [P] [US2] Create `src/OpenSkiTime.Import/Names/SeparateNameProjector.cs` — when `Last Name` + `First Name` headers present, project directly with uppercase last name
- [x] T085 [P] [US2] Create `src/OpenSkiTime.Import/Participation/ParticipationValueMatcher.cs` accepting `Yes`, `Kyllä`, `x` case-insensitively (FR-032)
- [x] T086 [US2] Create `src/OpenSkiTime.Import/Preview/DiffEngine.cs` — match by Code first, fallback `(UpperLastName, FirstName, YearOfBirth, Gender)` flagged with `MatchedBy.NameYearGenderTuple`; produces `ImportPreview` per `contracts/importer.md`
- [x] T087 [US2] Create `src/OpenSkiTime.Import/ImportPreviewService.cs` implementing `IImportPreviewService` (pure function: no DB writes, reads `EventSeriesSnapshot`)
- [x] T088 [US2] Create `src/OpenSkiTime.Import/ImportApplyService.cs` implementing `IImportApplyService` — transactional via `IUnitOfWork`; default mode skips empty cells; `OverwriteWithEmpty` clears them; verifies `EventSeriesSnapshotVersion`

#### Persistence

- [x] T089 [P] [US2] Create `src/OpenSkiTime.Persistence/Configurations/CompetitorConfiguration.cs` with `UQ_Competitors_EventSeriesId_Code` and `IX_Competitors_EventSeriesId_LastName_FirstName`
- [x] T090 [P] [US2] Create `src/OpenSkiTime.Persistence/Configurations/ParticipationConfiguration.cs` with `UQ_Participations_CompetitorId_CompetitionId` and `IX_Participations_EventSeriesId`
- [x] T091 [P] [US2] Create `src/OpenSkiTime.Persistence/Configurations/CategoryRuleConfiguration.cs`
- [x] T092 [US2] Add `DbSet<Competitor>`, `DbSet<Participation>`, `DbSet<CategoryRule>` to `OpenSkiTimeDbContext`; extend `IEventSeriesRepository.LoadSnapshotAsync` to include them
- [x] T093 [US2] Create EF migration `0002_Competitors_Participation_Categories` (`dotnet ef migrations add 0002_...`)

#### UI

- [x] T094 [P] [US2] Create `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` (Items, ViewMode {Flat, ByCategory, ByClub, ByNation}, Filters, PasteCommand)
- [x] T095 [P] [US2] Create `src/OpenSkiTime.Desktop/Controls/CompetitorRowEditor.axaml` (uppercase last-name display via converter, debounced commit)
- [x] T096 [US2] Create `src/OpenSkiTime.Desktop/Views/CompetitorGridView.axaml(.cs)` — virtualized `DataGrid`, dynamic per-Competition participation columns (one per Competition `ShortLabel`), toolbar (paste / view-mode / filters)
- [x] T097 [P] [US2] Create `src/OpenSkiTime.Desktop/ViewModels/ImportPreviewViewModel.cs` exposing tabs: New / Updated / Unchanged / Errors / UncertainNames / UnknownColumns; `OverwriteWithEmpty` toggle; Apply / Cancel
- [x] T098 [US2] Create `src/OpenSkiTime.Desktop/Views/ImportPreviewView.axaml(.cs)` — modal/sheet style; row-level edit not yet (US3 adds it)
- [x] T099 [US2] Wire paste workflow: `CompetitorGridViewModel.PasteCommand` → `IClipboardService.GetText` → `IImportPreviewService.BuildPreview` → open `ImportPreviewView` → on Apply call `IImportApplyService.Apply` → reload grid
- [x] T100 [US2] Wire `ShellWindow` left nav: "Competitors" → `CompetitorGridView` for the selected Event Series

**Checkpoint**: US2 demo-ready: paste, preview, apply, filter, group; participation columns reflect competitions of the series; uppercase last names everywhere.

---

## Phase 5: User Story 3 — Import competitors when only a single "Name" column is available (Priority: P2)

**Goal**: Single-`Name` paste produces uncertain rows reviewable in the preview; user edit overrides best-effort split.

**Independent Test**: Paste TSV with headers `Name, Year, Gender, Nation`; every row appears under the "Uncertain Names" tab with proposed uppercase last name + first name; correcting one row and applying writes the corrected split; uncorrected rows apply as best-effort with last name uppercased.

### Tests for User Story 3 (write FIRST)

- [x] T101 [P] [US3] `tests/OpenSkiTime.Import.Tests/Names/CombinedNameParserTests.cs` — strategy from `research.md`: ALL-CAPS leading tokens become last-name tokens ("VAN DER POEL Jeroen" → last "VAN DER POEL", first "Jeroen"); otherwise last whitespace token = last name; deterministic
- [ ] T102 [P] [US3] `tests/OpenSkiTime.Import.Tests/Names/UncertainNameFlaggingTests.cs` — when only `Name` header present, EVERY row goes into `UncertainNames`, never directly into `New`/`Updated` (FR-055)
- [ ] T103 [P] [US3] `tests/OpenSkiTime.Import.Tests/Preview/UncertainNameUserOverrideTests.cs` — when the user supplies a corrected `(LastName, FirstName)` split for an `UncertainNameRow`, Apply writes the corrected values, last name uppercased

### Implementation for User Story 3

- [x] T104 [US3] Create `src/OpenSkiTime.Import/Names/CombinedNameParser.cs` implementing the strategy from `research.md`
- [ ] T105 [US3] Extend `src/OpenSkiTime.Import/Headers/HeaderMapper.cs` and `src/OpenSkiTime.Import/Preview/DiffEngine.cs` to route every parsed row to `ImportPreview.UncertainNames` (with disposition New | Update) when `Name` header is the only name source
- [ ] T106 [US3] Extend `src/OpenSkiTime.Import/ImportApplyService.cs` to read user-edited `(LastName, FirstName)` from `UncertainNameRow` if provided; otherwise use the parser's best-effort split; uppercase last name unconditionally
- [ ] T107 [US3] Extend `src/OpenSkiTime.Desktop/ViewModels/ImportPreviewViewModel.cs` and `src/OpenSkiTime.Desktop/Views/ImportPreviewView.axaml` to allow inline editing of `ProposedLastName` / `ProposedFirstName` on each `UncertainNameRow`
- [ ] T108 [US3] Add UI guidance banner in `ImportPreviewView` encouraging separate `Last Name` + `First Name` columns when triggered by single-`Name` input (FR-058)

**Checkpoint**: US3 demo-ready: single-`Name` paste produces a fully reviewable list; corrections respected.

---

## Phase 6: User Story 4 — Edit competition basic data with placeholder for FIS update (Priority: P2)

**Goal**: Edit competition fields and persist; FIS button is visibly present, clearly labeled "not yet available", and performs zero network I/O.

**Independent Test**: Open a saved competition, edit course name and homologation number, save, reopen — values persist. Click "Update from FIS API" — system shows "not yet available" notification and the desktop test asserts no `HttpClient` is constructed.

### Tests for User Story 4 (write FIRST)

- [x] T109 [P] [US4] `tests/OpenSkiTime.Application.Tests/Competitions/UpdateCompetitionUseCaseTests.cs` — full field round-trip; FIS-code rule revalidated on update
- [x] T110 [P] [US4] `tests/OpenSkiTime.Desktop.Tests/Fis/FisPlaceholderButtonTests.cs` (Avalonia.Headless) — registers a tracking `IHttpClientFactory` that throws on use; clicks the FIS button; asserts (a) inline notification text matches the configured `UserMessage`, (b) the throwing factory was never invoked

### Implementation for User Story 4

- [x] T111 [US4] Extend `src/OpenSkiTime.Desktop/ViewModels/CompetitionEditorViewModel.cs` with `UpdateFromFisCommand` calling `IFisCompetitionUpdater.UpdateAsync` and surfacing the resulting `FisUpdateResult.NotImplemented.UserMessage` via `IDialogService` (or inline status banner)
- [x] T112 [US4] Extend `src/OpenSkiTime.Desktop/Views/CompetitionEditorView.axaml` adding the "Update from FIS API" button (subdued style + tooltip "Coming in a later release") wired to `UpdateFromFisCommand`
- [x] T113 [US4] Verify in `src/OpenSkiTime.Desktop/Program.cs` that `IFisCompetitionUpdater` resolves to `NotImplementedFisUpdater` only; reconfirm no `HttpClient` registration

**Checkpoint**: US4 demo-ready: editor saves all fields; FIS button shows the placeholder message and triggers zero network calls.

---

## Phase 7: User Story 5 — Validate readiness of competitors and competitions (Priority: P3)

**Goal**: Overview surfaces "K competitors with missing required data" and lists competitions missing required data; grid offers "Missing required data" filter.

**Independent Test**: With one valid competitor and one missing `Year`, the overview reads "1 competitor with missing required data"; the grid filter "Missing required data" returns exactly that competitor; a FIS race lacking a `FisCode` appears in the overview's missing-data list.

### Tests for User Story 5 (write FIRST)

- [x] T114 [P] [US5] `tests/OpenSkiTime.Application.Tests/EventSeries/EventSeriesValidationSummaryTests.cs` — counts and per-competition missing-data list (FR-070, FR-071)
- [x] T115 [P] [US5] `tests/OpenSkiTime.Application.Tests/Competitors/MissingRequiredDataFilterTests.cs` (FR-072)

### Implementation for User Story 5

- [x] T116 [US5] Create `src/OpenSkiTime.Application/EventSeries/EventSeriesValidationSummaryService.cs` (computes counts + missing-data list)
- [x] T117 [US5] Extend `src/OpenSkiTime.Domain/Competitors/Competitor.cs` with `IsUsableForRaceEntry` (FR-021) — likely already added in T075; ensure exposed
- [x] T118 [US5] Extend `src/OpenSkiTime.Desktop/ViewModels/EventSeriesOverviewViewModel.cs` to surface validation summary
- [x] T119 [US5] Extend `src/OpenSkiTime.Desktop/Views/EventSeriesOverviewView.axaml` to display the summary
- [x] T120 [US5] Extend `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` filter set with `MissingRequiredData` predicate

**Checkpoint**: US5 demo-ready: overview surfaces validation status; filter narrows the grid accordingly.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: Documentation, screenshots, ADRs, offline smoke verification.

- [x] T121 [P] Create `README.md` at repo root: what Open Ski Time is (open-source alpine race timing & race management), MIT license, prerequisites, build/run/test commands, screenshot placeholders for Event Series Overview / Competitor Grid / Competition Editor / Import Preview, links to constitution, list of out-of-scope items (timing, Alge, FIS API/XML, draws, live timing); note that Alge Timy3 + MT1 integration is planned after this feature; FIS API/XML integration is planned later and credentials must NOT be stored in the public repository
- [x] T122 [P] Create `docs/screenshots/event-series-overview.placeholder.png` (1x1 PNG with caption text in `docs/screenshots/README.md` describing intended content) — same for `competitor-grid`, `competition-editor`, `import-preview`
- [x] T123 [P] Create `docs/architecture.md` — module map mirroring `src/`, dependency arrows enforced by csproj refs, import workflow diagram from `plan.md`
- [x] T124 [P] Create `docs/adr/0001-net10-avalonia-sqlite.md` recording constitution defaults
- [x] T125 [P] Create `docs/adr/0002-efcore-over-dapper.md`
- [x] T126 [P] Create `docs/adr/0003-mvvm-with-community-toolkit.md`
- [x] T127 Update `.specify/memory/constitution.md` Sync Impact Report: close `TODO(README)` and `TODO(ADR)`; bump to `1.0.1` (PATCH — clarification, no principle change) with `Last Amended: 2026-05-28`
- [ ] T128 Add `tests/OpenSkiTime.Desktop.Tests/Smoke/OfflineSmokeTest.cs` (Avalonia.Headless) — registers a throwing `IHttpClientFactory`; starts the app; creates an Event Series; imports 100 rows; quits cleanly. Maps to SC-005, SC-007.
- [x] T129 Run full test suite (`dotnet test`) and full build (`dotnet build`) — both must be green with TreatWarningsAsErrors enabled
- [ ] T130 Run quickstart validation: follow `specs/001-event-series-management/quickstart.md` end-to-end on a clean machine state (delete `%LOCALAPPDATA%\OpenSkiTime\openskitime.db` first); confirm all steps succeed

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1 (Setup)**: no deps
- **Phase 2 (Foundational)**: depends on Phase 1; BLOCKS every user story
- **Phase 3 (US1)**: depends on Phase 2
- **Phase 4 (US2)**: depends on Phase 2; technically independent of US1, but the demo flow needs Event Series + Competition records that US1 produces — so the typical sequencing is US1 → US2
- **Phase 5 (US3)**: depends on Phase 4 (extends the importer)
- **Phase 6 (US4)**: depends on Phase 3 (extends Competition Editor + FIS placeholder wired in Phase 2)
- **Phase 7 (US5)**: depends on Phases 3 + 4
- **Phase 8 (Polish)**: depends on the user-story phases the team has chosen to ship

### Within Each User Story

- Tests (T0xx labelled "write FIRST") MUST be authored and failing before the corresponding implementation tasks
- Domain → Application → Persistence → Importer/UI
- Each story finishes with its checkpoint section verified

### Parallel Opportunities

- Phase 1: T002–T004 in parallel; T009–T013 in parallel; T015 in parallel across the 5 test csprojs
- Phase 2: T018–T022 in parallel (different files); T023–T024 in parallel; T029–T030 in parallel
- Phase 3: T037–T041 (all tests) in parallel; T042–T043 in parallel; T049–T050 in parallel; T055 + T057 in parallel
- Phase 4: T060–T074 (all tests) in parallel; T075–T077 in parallel; T082–T085, T089–T091, T094–T095, T097 in parallel
- Phase 5: T101–T103 in parallel
- Phase 6: T109–T110 in parallel
- Phase 7: T114–T115 in parallel
- Phase 8: T121–T126 in parallel

---

## Parallel Example: User Story 2

```text
# Launch all test files for US2 together (different files, no inter-deps):
T060 tests/OpenSkiTime.Domain.Tests/Competitors/CompetitorTests.cs
T061 tests/OpenSkiTime.Domain.Tests/Participation/ParticipationTests.cs
T062 tests/OpenSkiTime.Domain.Tests/Categories/CategoryResolverTests.cs
T065 tests/OpenSkiTime.Import.Tests/Tsv/TsvTokenizerTests.cs
T066 tests/OpenSkiTime.Import.Tests/Headers/HeaderMapperTests.cs
T067 tests/OpenSkiTime.Import.Tests/Names/SeparateLastFirstNameTests.cs
T068 tests/OpenSkiTime.Import.Tests/Participation/ParticipationValueMatcherTests.cs
T069 tests/OpenSkiTime.Import.Tests/Participation/ParticipationColumnHeaderTests.cs
T070 tests/OpenSkiTime.Import.Tests/Preview/DiffEngineTests.cs
T071 tests/OpenSkiTime.Import.Tests/Preview/EmptyDoesNotOverwriteTests.cs
T072 tests/OpenSkiTime.Import.Tests/Preview/PartialUpdateTests.cs

# Then domain entities for US2 in parallel:
T075 src/OpenSkiTime.Domain/Competitors/Competitor.cs
T076 src/OpenSkiTime.Domain/Participation/Participation.cs
T077 src/OpenSkiTime.Domain/Categories/CategoryRule.cs + ICategoryResolver.cs + RuleBasedCategoryResolver.cs

# Then importer pieces in parallel:
T082 src/OpenSkiTime.Import/Tsv/TsvTokenizer.cs
T083 src/OpenSkiTime.Import/Headers/HeaderMapper.cs
T084 src/OpenSkiTime.Import/Names/SeparateNameProjector.cs
T085 src/OpenSkiTime.Import/Participation/ParticipationValueMatcher.cs
```

---

## Implementation Strategy

### MVP First (User Story 1 only)

1. Phase 1 (Setup) → 2. Phase 2 (Foundational) → 3. Phase 3 (US1) → STOP & validate offline create/edit/persist of Event Series + Competitions. Demo as MVP.

### Incremental Delivery

1. MVP (US1).
2. + US2 → race-office-grade competitor management with paste import.
3. + US3 → robust handling of single-`Name` source lists.
4. + US4 → full competition editor + visible FIS roadmap.
5. + US5 → validation summary.
6. Polish (Phase 8).

### Out of Scope (must not be implemented in this feature)

Real timing capture, Alge Timy3, Alge MT1, FIS API implementation, FIS XML, FIS reports, FIS penalty calculation, start list draw, second run ordering, live timing, localization beyond English, multi-user / sync, authentication.

---

## Notes

- `[P]` tasks operate on different files; verify no hidden coupling before dispatching in parallel
- `[Story]` labels enable independent traceability per user story
- Tests precede implementation within each story (constitution Principle V)
- Commit after each task or logical group (commit policy follows `.specify/extensions/git/git-config.yml`)
- Final acceptance is "all tests green AND quickstart steps succeed offline"

---

## Task Count Summary

| Phase | Range | Count |
|---|---|---|
| 1. Setup | T001–T017 | 17 |
| 2. Foundational | T018–T036 | 19 |
| 3. US1 — Event Series + Competitions (P1, MVP) | T037–T059, T131–T136 | 29 |
| 4. US2 — Competitor grid + Importer (P1) | T060–T100 | 41 |
| 5. US3 — Single-`Name` parsing (P2) | T101–T108 | 8 |
| 6. US4 — Competition editor + FIS placeholder (P2) | T109–T113 | 5 |
| 7. US5 — Validation summary (P3) | T114–T120 | 7 |
| 8. Polish | T121–T130 | 10 |
| **Total** | T001–T136 | **136** |

> **Note**: T131–T136 were appended to Phase 3 after the `/speckit.analyze` pass to close coverage gaps for FR-001 (Event Series delete) and FR-004 (Competition remove). They are non-contiguous numerically with T037–T059 but logically belong to User Story 1 and must be completed before the US1 checkpoint.
