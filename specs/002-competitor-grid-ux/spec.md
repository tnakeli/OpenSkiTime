# Feature Specification: Competitor Grid UX Overhaul

**Feature Branch**: `002-competitor-grid-ux`

**Created**: 2026-05-28

**Status**: Draft

**Input**: User description: "Import Competitors is removed from the top-level left nav. Competitor management lives exclusively within the selected Event Series context. Bib number is removed from the Competition editor and the competition entry phase. Bibs are assigned only after the draw, in a dedicated future step. The competitor grid supports inline add, edit, and delete. All changes are staged locally before saving. Every destructive change (add, edit, delete) is recorded in a Change Log ("trash"). Each entry can be individually restored ("undelete") or the entire log can be replayed as undo steps. Import (paste) happens inline in the competitor grid, not on a separate page. After paste, changed rows are highlighted; the user accepts (Apply) or discards (Discard changes) before the data is committed. The grid supports Copy: selected rows are copied to the clipboard as TSV matching the import column format, ready to paste directly into Excel."

---

## User Scenarios & Testing

### User Story 1 — Competitors managed within Event Series context (Priority: P1)

The race secretary opens an Event Series and sees the competitor list directly — no separate "Import Competitors" item in the left nav. Adding, editing, and deleting competitors all happen within the Event Series view. There is no top-level competitor route.

**Why this priority**: The current top-level nav placement breaks the mental model — competitors belong to a series, not to the application globally. This is the most disorienting issue in the current UX and must be fixed first.

**Independent Test**: Open the application, select an Event Series. The left nav shows no "Import Competitors" item. The Event Series detail view includes a Competitors tab/section. All competitor CRUD is accessible from within that section.

**Acceptance Scenarios**:

1. **Given** the application is open, **When** the user looks at the left nav, **Then** there is no top-level "Import Competitors" or "Competitors" item — only "Event Series" and its children.
2. **Given** an Event Series is selected, **When** the user opens its detail view, **Then** a Competitors section is visible alongside the Competitions list.
3. **Given** a Competitors section is open, **When** the user clicks "Add Competitor", **Then** an inline row editor appears without navigating away from the Event Series view.

---

### User Story 2 — Inline add, edit, delete with Change Log undo (Priority: P1)

The race secretary can add, edit, and delete competitors directly in the grid. All unsaved changes are staged locally (highlighted rows). Any destructive action (delete, field overwrite) is recorded in a Change Log visible as a side panel or expandable section. Individual entries can be restored ("undelete") or all changes can be discarded at once.

**Why this priority**: Race offices make mistakes under pressure. A recoverable change model prevents data loss without requiring a full database backup/restore cycle. Inline editing is faster than modal dialogs for bulk corrections.

**Independent Test**: Add a competitor, edit their year of birth, then delete a second competitor. Open the Change Log — it lists two entries (edit + delete). Restore the deleted competitor — they reappear in the grid. Discard all staged changes — the grid returns to its last saved state.

**Acceptance Scenarios**:

1. **Given** a competitor exists, **When** the user edits their first name inline, **Then** the row is highlighted as "modified" and the original value is recorded in the Change Log.
2. **Given** a competitor is deleted, **When** the user opens the Change Log, **Then** the deleted entry appears with a "Restore" action.
3. **Given** staged changes exist, **When** the user clicks "Restore" on a Change Log entry, **Then** that row returns to its pre-change state in the grid.
4. **Given** staged changes exist, **When** the user clicks "Discard all changes", **Then** the grid resets to the last persisted state and the Change Log is cleared.
5. **Given** staged changes exist, **When** the user clicks "Save", **Then** all staged changes are committed to the database and the Change Log is cleared.

---

### User Story 3 — Inline paste with row-level Accept/Discard (Priority: P1)

The race secretary pastes TSV data (from Excel or FIS results) directly into the competitor grid using Ctrl+V or a "Paste" toolbar button. After paste, new and modified rows are highlighted. The user reviews the highlighted rows and clicks "Apply import" to commit or "Discard import" to revert. No separate import page or navigation step.

**Why this priority**: The current separate import page breaks flow — the user must navigate away, lose context, and navigate back. Inline paste is the natural clipboard workflow and matches how Excel users think.

**Independent Test**: Copy three rows from Excel (Last Name, First Name, Year, Nation columns). In the competitor grid, press Ctrl+V. Three rows appear highlighted in the grid. Click "Apply import" — the rows are saved. Press Ctrl+V again, then "Discard import" — the grid returns to its previous state.

**Acceptance Scenarios**:

1. **Given** the competitor grid is active, **When** the user presses Ctrl+V or clicks "Paste", **Then** TSV data from the clipboard is parsed and new/changed rows appear highlighted in the grid — no navigation occurs.
2. **Given** pasted rows are highlighted, **When** the user clicks "Apply import", **Then** all highlighted rows are committed to the database and highlighting is cleared.
3. **Given** pasted rows are highlighted, **When** the user clicks "Discard import", **Then** the grid reverts to its pre-paste state with no database writes.
4. **Given** a pasted row matches an existing competitor by last name + first name + year, **When** the import is applied, **Then** the existing record is updated (not duplicated).
5. **Given** an empty participation cell in pasted data, **When** the import is applied, **Then** the existing participation flag for that competition is NOT overwritten (FR-033 / constitution §VII).

---

### User Story 4 — Copy selected rows to clipboard as TSV (Priority: P2)

The race secretary selects one or more rows in the competitor grid and presses Ctrl+C or clicks "Copy". The rows are placed on the clipboard as TSV with the same column order as the import format (Last Name, First Name, Year, Gender, Nation, Club, FIS Code, then one column per competition ShortLabel). Pasting into Excel produces a correctly structured spreadsheet immediately.

**Why this priority**: Copy and paste form a workflow pair. Paste-in is only fully useful if the user can also copy out — for backup, sharing with FIS, or preparing start lists in Excel.

**Independent Test**: Select three competitors, press Ctrl+C, open Excel, press Ctrl+V. Columns appear in order: Last Name, First Name, Year, Gender, Nation, Club, FIS Code, [competition columns]. Last names are in UPPERCASE.

**Acceptance Scenarios**:

1. **Given** one or more rows are selected, **When** the user presses Ctrl+C or clicks "Copy", **Then** the rows are placed on the clipboard as tab-separated text with a header row.
2. **Given** the clipboard content is pasted into Excel, **Then** each column aligns with the import column format with no manual reformatting needed.
3. **Given** an Event Series has competitions, **Then** each competition's ShortLabel appears as a column header in the copied TSV, with `x` for participating and empty for not participating.

---

### User Story 5 — Bib number removed from competition entry phase (Priority: P2)

The Competition editor no longer shows a Bib field. Bib numbers are not set during competition creation or editing. Bib assignment is a separate future workflow ("Draw") that happens after the entry list is finalized. The competitor grid's Bib column remains visible (showing the current assigned bib, if any) but is read-only in this feature.

**Why this priority**: Showing a Bib field during competition entry creates confusion — bibs cannot be known before the draw. Removing it prevents invalid data entry and clarifies the race-office workflow.

**Independent Test**: Open an existing Competition editor — no Bib field is present. The competitor grid still shows a Bib column (read-only). Bibs set from a previous import are displayed but cannot be edited from the grid in this feature.

**Acceptance Scenarios**:

1. **Given** the Competition editor is open (create or edit), **Then** no Bib Number field is present.
2. **Given** competitors have bibs assigned from a previous import, **When** the competitor grid loads, **Then** bibs are displayed in a read-only Bib column.
3. **Given** the competitor grid is in edit mode, **Then** the Bib column cannot be edited inline (it is greyed out / locked).

---

### Edge Cases

- What happens when the user pastes TSV that contains no recognizable headers? → Show an inline warning banner; no rows are highlighted; the paste is silently discarded.
- What happens when the user pastes duplicate rows (same last name + first name + year)? → The second paste occurrence is flagged as a warning row; user can discard or accept each individually.
- What happens if the clipboard is empty when Ctrl+V is pressed? → No action; no error shown.
- What happens if the user navigates away from the Event Series with staged (unsaved) changes? → A confirmation dialog warns "You have unsaved changes. Discard and leave?" — follows existing `IDialogService.ConfirmAsync` pattern.
- What happens when the Change Log grows large (100+ entries)? → The Change Log is capped at 200 entries per session; oldest entries are silently dropped (not persisted).
- What happens when "Restore" is clicked but the restored row conflicts with a concurrently added row? → The conflict is surfaced as an inline row error; the user must resolve manually.

---

## Requirements

### Functional Requirements

#### Navigation & Context

- **FR-001**: The top-level left navigation MUST NOT contain a standalone "Import Competitors" or "Competitors" item. Competitor management is accessible exclusively via the selected Event Series.
- **FR-002**: The Event Series detail view MUST expose a Competitors section (tab or collapsible panel) adjacent to the Competitions section.

#### Competition Editor — Bib Removal

- **FR-003**: The Competition editor (create and edit) MUST NOT expose a Bib Number input field.
- **FR-004**: The Competitor grid's Bib column MUST be present but read-only in this feature; bib assignment is deferred to a future "Draw" workflow.

#### Inline Competitor CRUD

- **FR-005**: The competitor grid MUST support inline row addition: clicking "Add" inserts a new empty row at the top of the grid in edit mode.
- **FR-006**: The competitor grid MUST support inline row editing: clicking a cell activates an inline editor for that field.
- **FR-007**: The competitor grid MUST support inline row deletion: selecting a row and clicking "Delete" marks it for deletion (highlighted) without immediately committing to the database.
- **FR-008**: Last Name MUST always be stored and displayed in UPPERCASE (constitution §VII).
- **FR-009**: All staged changes (add, edit, delete) MUST be visible as highlighted rows (distinct color per change type: add=green, edit=yellow, delete=red/strikethrough) before the user saves.

#### Change Log (Undo Trash)

- **FR-010**: Every destructive or mutating operation (add, edit, delete) on a competitor row MUST produce a Change Log entry recording: timestamp, operation type, competitor identifier, before-value (for edits/deletes), after-value (for edits/adds).
- **FR-011**: The Change Log MUST be displayed as a collapsible panel or side drawer within the Event Series competitor section.
- **FR-012**: Each Change Log entry MUST expose a "Restore" action that reverts that single change in the staged grid (without committing to the database).
- **FR-013**: A "Discard all changes" action MUST reset the grid to the last persisted state and clear the Change Log.
- **FR-014**: The Change Log MUST be session-only (not persisted to the database). It is cleared on Save and on application restart.
- **FR-015**: The Change Log MUST be capped at 200 entries per session; oldest entries are silently evicted when the cap is reached.

#### Inline Paste Import

- **FR-016**: The competitor grid MUST accept Ctrl+V (and a toolbar "Paste" button) to trigger a TSV import directly into the grid — no separate navigation or page transition.
- **FR-017**: After paste, new and updated rows MUST be highlighted in the grid pending user confirmation.
- **FR-018**: An "Apply import" button MUST commit the highlighted rows to the database via the existing `ImportApplyService`.
- **FR-019**: A "Discard import" button MUST revert the grid to its pre-paste state without any database write.
- **FR-020**: Paste follows all existing import fidelity rules: empty cells do not overwrite, absent columns are ignored, last name is uppercased (constitution §VII, feature 001 FR-033).
- **FR-021**: If the pasted TSV contains no recognizable column headers, an inline warning banner MUST be shown and no rows highlighted.

#### Copy to Clipboard

- **FR-022**: Selecting one or more rows and pressing Ctrl+C (or a toolbar "Copy" button) MUST place the selected rows on the clipboard as TSV.
- **FR-023**: The TSV header row MUST match the import column format: `Last Name`, `First Name`, `Year`, `Gender`, `Nation`, `Club`, `FIS Code`, followed by one column per competition ShortLabel in chronological order.
- **FR-024**: Participation values in the copied TSV MUST use `x` for participating and empty string for not participating.
- **FR-025**: Last names in the copied TSV MUST be in UPPERCASE.

### Key Entities

- **CompetitorChangeLogEntry**: Records a single staged change — `Id`, `Timestamp`, `OperationType` (Add | Edit | Delete), `CompetitorId`, `BeforeSnapshot` (nullable), `AfterSnapshot` (nullable). Session-only; never persisted.
- **StagedCompetitorGrid**: In-memory representation of the grid containing both persisted and unstaged rows; owns the Change Log list.
- **ImportPasteSession**: Transient state representing an active paste operation — set of highlighted rows pending Accept/Discard.

---

## Success Criteria

### Measurable Outcomes

- **SC-001**: From the application's left nav, there is zero top-level "Import Competitors" or "Competitors" navigation item.
- **SC-002**: A new competitor can be added, saved, and verified in the database in ≤ 4 clicks from the Event Series detail view.
- **SC-003**: A paste of 50 competitor rows from Excel completes (highlight visible) in < 1 second on the target machine.
- **SC-004**: A deleted competitor can be restored from the Change Log in ≤ 2 clicks without any database write.
- **SC-005**: Copying 50 rows and pasting into Excel produces a correctly structured spreadsheet with no manual column adjustment required.
- **SC-006**: The Competition editor contains no Bib Number field (verified by UI test).
- **SC-007**: The application remains fully functional offline; no HTTP calls are triggered by any action in this feature.

---

## Assumptions

- The existing `ImportApplyService`, `DiffEngine`, `ParticipationValueMatcher`, and `TsvTokenizer` from feature 001 are reused without modification for the inline paste flow.
- The existing `IDialogService.ConfirmAsync` is used for unsaved-changes navigation guards.
- The `IClipboardService` abstraction (already present in the Desktop project) is used for both read (paste) and write (copy) clipboard operations.
- Bib assignment is explicitly out of scope for this feature; a future "Draw" feature will implement it.
- The Change Log is session-only and is not persisted — no migration or schema change is required.
- Avalonia's `DataGrid` is used as the base component; virtualization is required for lists exceeding 200 rows.
- The Competitors section within the Event Series view is implemented as a tab alongside the existing Competitions tab (or an equivalent layout that does not require a separate navigation item).
