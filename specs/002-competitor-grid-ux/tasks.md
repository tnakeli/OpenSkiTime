# Tasks: Competitor Grid UX Overhaul (002)

**Branch**: `002-competitor-grid-ux` | **Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md)

**Prerequisites**: spec.md ✅ plan.md ✅ research.md ✅ data-model.md ✅

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (no file conflicts)
- **[USn]**: User story reference
- Tests are written **first** (TDD) — write the test, confirm it fails, then implement

---

## Phase 1: Shared Foundation

**Purpose**: In-memory session models used by US1–US4.

**Note**: No DB migrations required. These are pure C# classes in the Desktop project.

- [ ] T001 [P] Create `src/OpenSkiTime.Desktop/Models/ChangeLogEntry.cs` — `ChangeOp` enum (Add, Edit, Delete), `ChangeLogEntry` record (Id, Timestamp, OperationType, CompetitorId, FieldName, BeforeValue, AfterValue, DisplayLabel)
- [ ] T002 [P] Create `src/OpenSkiTime.Desktop/Models/ImportPasteSession.cs` — `ImportPasteSession` record (Rows, SnapshotVersion, IsActive)
- [ ] T003 [P] Create `src/OpenSkiTime.Desktop/Models/RowState.cs` — `RowState` enum (Unchanged, Added, Edited, Deleted, PasteHighlighted)

**Checkpoint**: Models compile; no UI wiring yet.

---

## Phase 2: User Story 1 — Competitors within Event Series context (Priority: P1)

**Goal**: Remove top-level Import nav item. Move competitor grid under Event Series detail view. Remove Bib from Competition editor.

**Independent Test**: Left nav has no "Import Competitors" item. Event Series detail view has Competitors section. Competition editor has no Bib field.

### Tests for User Story 1 (write FIRST)

- [ ] T010 [P] [US1] `tests/OpenSkiTime.Desktop.Tests/Navigation/NavStructureTests.cs` — assert `ShellViewModel` has no `NavigateToImportCommand`; assert `EventSeriesOverviewViewModel` exposes `CompetitorGridViewModel`
- [ ] T011 [P] [US1] `tests/OpenSkiTime.Desktop.Tests/Navigation/CompetitionEditorBibTests.cs` — assert `CompetitionEditorViewModel` has no `BibNumber` property

### Implementation for User Story 1

- [ ] T012 [US1] `src/OpenSkiTime.Desktop/ViewModels/ShellViewModel.cs` — remove `NavigateToImportAsync` command and `ImportViewModel` dependency
- [ ] T013 [US1] `src/OpenSkiTime.Desktop/ViewModels/EventSeriesOverviewViewModel.cs` — inject and expose `CompetitorGridViewModel` as `Competitors` property; wire `LoadAsync` to call `Competitors.LoadAsync(seriesId)`
- [ ] T014 [P] [US1] `src/OpenSkiTime.Desktop/ViewModels/CompetitionEditorViewModel.cs` — remove `BibNumber` observable property; remove it from `InitializeForEdit` and command parameters
- [ ] T015 [US1] `src/OpenSkiTime.Desktop/Views/ShellWindow.axaml` — remove Import nav `ListBoxItem` / `Button` from left nav
- [ ] T016 [US1] `src/OpenSkiTime.Desktop/Views/EventSeriesOverviewView.axaml` — add `TabControl` with "Competitions" and "Competitors" tabs; place `CompetitorGridView` in Competitors tab
- [ ] T017 [P] [US1] `src/OpenSkiTime.Desktop/Views/CompetitionEditorView.axaml` — remove Bib Number `TextBox` and its label

**Checkpoint**: US1 demo-ready — no top-level import nav; Competitors tab visible in Event Series; no Bib in editor.

---

## Phase 3: User Story 2 — Inline CRUD with Change Log (Priority: P1)

**Goal**: Add/edit/delete competitors inline in grid; all changes staged; Change Log records every mutation; Restore and Discard All work; Save commits.

**Independent Test**: Add competitor (green), edit field (yellow), delete (red+strikethrough). Change Log lists 3 entries. Restore delete — row reappears. Discard All — grid resets. Save — DB updated, Change Log cleared.

### Tests for User Story 2 (write FIRST)

- [ ] T020 [P] [US2] `tests/OpenSkiTime.Desktop.Tests/ChangeLog/ChangeLogTests.cs`
  - `Add_produces_Add_entry_with_correct_display_label`
  - `Edit_produces_Edit_entry_with_before_and_after_value`
  - `Delete_produces_Delete_entry`
  - `Restore_removes_entry_and_reverts_row_state`
  - `DiscardAll_clears_log_and_resets_all_rows_to_Unchanged`
  - `Log_capped_at_200_oldest_entry_evicted`
- [ ] T021 [P] [US2] `tests/OpenSkiTime.Desktop.Tests/ChangeLog/StagedRowStateTests.cs`
  - `New_row_has_state_Added`
  - `Edited_row_has_state_Edited`
  - `Deleted_row_has_state_Deleted`
  - `Saved_rows_revert_to_Unchanged`

### Implementation for User Story 2

- [ ] T022 [US2] Extract `src/OpenSkiTime.Desktop/ViewModels/CompetitorRowViewModel.cs` — move `CompetitorRowViewModel` out of `CompetitorGridViewModel.cs` into its own file; add `RowState` observable property; add `IsEditing` flag; add `PendingChanges` (`List<ChangeLogEntry>`); make all competitor fields (`LastName`, `FirstName`, `YearOfBirth`, `Gender`, `NationCode`, `ClubName`, `FisCode`) `[ObservableProperty]` (editable); `BibNumber` remains get-only
- [ ] T023a [US2] `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` — **Add**: add `CompetitorChangeLog` (`ObservableCollection<ChangeLogEntry>`, cap 200 — evict oldest on overflow); add `HasStagedChanges` computed property (`Competitors.Any(r => r.RowState != RowState.Unchanged)`); add `AddCompetitorCommand` — inserts new empty `CompetitorRowViewModel` with `RowState.Added` at top; produces `ChangeLogEntry(Add)`
- [ ] T023b [US2] `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` — **Edit**: hook `CompetitorRowViewModel` property-changed events; on field change produce `ChangeLogEntry(Edit, fieldName, before, after)` and set `RowState.Edited`
- [ ] T023c [US2] `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` — **Delete**: add `DeleteCompetitorCommand(row)` — marks `RowState.Deleted`; produces `ChangeLogEntry(Delete)`; add `RestoreCommand(entry)` — reverts row to before-state and removes entry from log; add `DiscardAllCommand` — resets all rows to persisted state and clears log
- [ ] T023d [US2] `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` — **Save**: extend `SaveCommand` to iterate staged rows: `Added` → `AddCompetitorUseCase`, `Edited` → `EditCompetitorUseCase`, `Deleted` → repository remove; on success set all rows to `Unchanged` and clear `CompetitorChangeLog`
- [ ] T024 [US2] `src/OpenSkiTime.Desktop/Views/CompetitorGridView.axaml` — set `IsReadOnly="False"` on editable columns; add row background converter for `RowState` (green/yellow/red); add Change Log collapsible side panel with `ListBox` of entries and Restore button per entry; add Discard All and Save buttons to toolbar

**Checkpoint**: US2 demo-ready — inline CRUD, row colouring, Change Log panel, Restore, Discard All, Save.

---

## Phase 4: User Story 3 — Inline Paste (Priority: P1)

**Goal**: Ctrl+V pastes TSV into grid without navigation. Changed rows highlighted. Apply/Discard bar appears. Apply commits. Discard reverts.

**Independent Test**: Copy 3 rows from Excel → Ctrl+V in grid → rows appear highlighted in grid (no page change) → Apply → rows saved → Ctrl+V again → Discard → grid unchanged.

### Tests for User Story 3 (write FIRST)

- [ ] T030 [P] [US3] `tests/OpenSkiTime.Desktop.Tests/Clipboard/PasteSessionTests.cs`
  - `Paste_new_rows_sets_state_PasteHighlighted`
  - `Paste_existing_competitor_sets_state_PasteHighlighted_not_Added`
  - `Apply_import_commits_rows_and_clears_highlight`
  - `Discard_import_reverts_grid_to_pre_paste_state`
  - `Paste_with_no_recognized_headers_shows_warning_and_highlights_nothing`
  - `Empty_participation_cell_does_not_overwrite_existing_flag` (FR-020 / constitution §VII)

### Implementation for User Story 3

- [ ] T031 [US3] `src/OpenSkiTime.Desktop/Models/ImportPasteSession.cs` — implement `IsActive`, `Rows`, `SnapshotVersion`; add `ApplyAsync()` and `Discard()` methods delegating to `ImportApplyService`
- [ ] T032 [US3] `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` — add `PasteCommand` (reads clipboard via `IClipboardService.GetTextAsync`, runs `TsvTokenizer`→`DiffEngine`, creates `ImportPasteSession`, sets rows to `PasteHighlighted`); add `ApplyImportCommand`; add `DiscardImportCommand`; add `HasActivePasteSession` observable for UI visibility; add `PasteWarningMessage` for header-not-recognized case
- [ ] T033 [US3] `src/OpenSkiTime.Desktop/Views/CompetitorGridView.axaml` — add `InputBinding` for `Ctrl+V` → `PasteCommand`; add paste confirmation bar (Apply import / Discard import / warning text) visible when `HasActivePasteSession`; add paste row highlight color to row-state converter

**Checkpoint**: US3 demo-ready — Ctrl+V pastes inline, Apply/Discard work, no navigation, empty cells safe.

---

## Phase 5: User Story 4 — Copy to Clipboard as TSV (Priority: P2)

**Goal**: Ctrl+C copies selected rows as Excel-ready TSV with header row and competition columns.

**Independent Test**: Select 3 rows → Ctrl+C → paste in Excel → correct columns, UPPERCASE last names, `x`/empty participation values.

### Tests for User Story 4 (write FIRST)

- [ ] T040 [P] [US4] `tests/OpenSkiTime.Desktop.Tests/Clipboard/CopyTsvTests.cs`
  - `Copy_single_row_produces_header_plus_one_data_row`
  - `Copy_multiple_rows_in_correct_column_order`
  - `LastName_is_uppercased_in_tsv`
  - `Participating_competition_produces_x_column`
  - `Not_participating_produces_empty_column`
  - `Header_includes_competition_short_labels_in_chronological_order`

### Implementation for User Story 4

- [ ] T041 [US4] `src/OpenSkiTime.Desktop/ViewModels/CompetitorGridViewModel.cs` — add `CopySelectedRowsCommand`; build TSV string from `SelectedItems` with header `Last Name\tFirst Name\tYear\tGender\tNation\tClub\tFIS Code\t[ShortLabels...]`; participation uses `x` / empty; write via `IClipboardService.SetTextAsync`
- [ ] T042 [US4] `src/OpenSkiTime.Desktop/Views/CompetitorGridView.axaml` — add `InputBinding` for `Ctrl+C` → `CopySelectedRowsCommand`; add toolbar "Copy" button

**Checkpoint**: US4 demo-ready — Ctrl+C copies TSV, paste in Excel works.

---

## Phase 6: User Story 5 — Bib read-only in competitor grid (Priority: P2)

**Goal**: Bib column visible but not editable in competitor grid. Competition editor has no Bib field (done in Phase 2 T014/T017).

### Implementation for User Story 5

- [ ] T050 [P] [US5] `src/OpenSkiTime.Desktop/Views/CompetitorGridView.axaml` — ensure Bib column is `IsReadOnly="True"` with a visually distinct (greyed) style to communicate read-only intent
- [ ] T051 [P] [US5] `src/OpenSkiTime.Desktop/ViewModels/CompetitorRowViewModel.cs` — ensure `BibNumber` property has no setter exposed to the grid edit path

**Checkpoint**: US5 demo-ready — Bib column present and greyed out, not editable.

---

## Phase 7: Polish & Cross-Cutting

**Purpose**: Unsaved-changes guard, final wiring, docs update, full test run.

- [ ] T060 [P] Unsaved-changes navigation guard — in `EventSeriesOverviewViewModel`, before switching away from Competitors tab or changing series, check `CompetitorGridViewModel.HasStagedChanges` (defined in T023a); if true call `IDialogService.ConfirmAsync("Discard unsaved changes?")` and call `DiscardAllCommand` on confirm
- [ ] T061 [P] `src/OpenSkiTime.Desktop/Program.cs` — verify DI registrations: `CompetitorGridViewModel` is already registered; no new registrations needed (`ImportPasteSession` is a plain record instantiated inline in `PasteCommand`, not a DI service)
- [ ] T062 [P] Update `docs/architecture.md` — add Change Log and Paste Session to the ViewModel Hierarchy section; note that `ImportViewModel` is no longer a top-level nav target
- [ ] T063 Run `dotnet build OpenSkiTime.slnx` — must succeed with zero errors and zero `TreatWarningsAsErrors` violations
- [ ] T064 Run `dotnet test OpenSkiTime.slnx` — all tests green (target: existing 234 + new ~15)
- [ ] T065 [P] Follow `specs/002-competitor-grid-ux/quickstart.md` end-to-end — all steps succeed

**Checkpoint**: Full build green, all tests pass, quickstart validated.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Phase 1** (Foundation): No dependencies — start immediately
- **Phase 2** (US1): Depends on Phase 1 (T001–T003)
- **Phase 3** (US2): Depends on Phase 1 and Phase 2 (T012, T013 must be done so `CompetitorGridViewModel` is wired); T023a–T023d are sequential within US2
- **Phase 4** (US3): Depends on T022 (`CompetitorRowViewModel` with `RowState`) and T023a (`CompetitorChangeLog`)
- **Phase 5** (US4): Depends on T022 (`SelectedItems` typed) and T023a
- **Phase 6** (US5): Depends on T022 (`CompetitorRowViewModel` extracted)
- **Phase 7** (Polish): Depends on Phases 2–6

### User Story Dependencies

- **US1** (P1): Independent after Phase 1
- **US2** (P1): Independent after US1 nav wiring (T013)
- **US3** (P1): Depends on T022 (`RowState`) and T023a (`HasStagedChanges`, `CompetitorChangeLog`)
- **US4** (P2): Depends on Phase 3 grid setup (SelectedItems, `IClipboardService`)
- **US5** (P2): Depends on US2 T022

### Parallel Opportunities

- T001, T002, T003 — parallel (different files)
- T010, T011 — parallel (different test files)
- T014, T015, T017 — parallel (different files, US1 UI tasks)
- T020, T021 — parallel (different test files)
- T030 — parallel (test file)
- T040 — parallel (test file)
- T050, T051 — parallel (different files)
- T060, T061, T062 — parallel (different files)
