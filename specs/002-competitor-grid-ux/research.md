# Research: Competitor Grid UX Overhaul (002)

All foundational technology decisions are inherited from feature 001 (ADRs 0001–0003). This research covers only new decisions specific to this feature.

---

## R-001: Inline Grid Editing in Avalonia DataGrid

**Decision**: Use Avalonia `DataGrid` with `IsReadOnly="False"` on individual columns; bind each editable cell to an observable row VM property. Use `BeginningEdit` / `CellEditEnding` events to intercept edits and write them to the Change Log.

**Rationale**: Avalonia's `DataGrid` supports cell-level editing via `DataGridTemplateColumn` with a `TextBox` cell template. This avoids external dependencies and keeps the existing DataGrid already in use.

**Alternatives considered**: Custom `ListBox` + `ItemTemplate` — rejected (more complex layout, no built-in column sizing/virtualization); ReactiveUI `ReactiveList` — rejected (no ReactiveUI dependency in the project).

---

## R-002: Change Log (Undo) — In-Memory Model

**Decision**: Implement `CompetitorChangeLog` as a session-only `ObservableCollection<ChangeLogEntry>` owned by `CompetitorGridViewModel`. Entries are never persisted. Cap at 200 entries (oldest dropped silently). Expose `RestoreCommand(entry)` and `DiscardAllCommand`.

**Rationale**: The spec explicitly states the log is session-only. An in-memory LIFO list is sufficient and requires zero schema migrations.

**Alternatives considered**: SQLite journal table — rejected (persists changes the user has not committed; adds migration complexity); dedicated undo framework (e.g., UndoNet) — rejected (overkill for a single-aggregate operation set).

---

## R-003: Inline Paste — Clipboard Integration

**Decision**: Reuse existing `IClipboardService` for read (`GetTextAsync`). Wire `Ctrl+V` via an `InputBinding` on the `DataGrid` or the containing `UserControl`. Parse via existing `TsvTokenizer` + `HeaderMapper` + `DiffEngine`. Highlight pasted rows via a `IsPasteHighlighted` property on `CompetitorRowViewModel`.

**Rationale**: All import pipeline components (`TsvTokenizer`, `HeaderMapper`, `RowParser`, `DiffEngine`, `ImportApplyService`) are already production-tested in feature 001. No new parse logic is needed.

**Alternatives considered**: New dedicated `PasteParser` — rejected (redundant with existing pipeline); separate paste dialog — rejected (spec FR-016 explicitly forbids page navigation on paste).

---

## R-004: Copy to Clipboard — TSV Format

**Decision**: Implement `CopySelectedRowsCommand` in `CompetitorGridViewModel`. Build a TSV string: header row = `Last Name\tFirst Name\tYear\tGender\tNation\tClub\tFIS Code\t[ShortLabel...]`; data rows from selected `CompetitorRowViewModel` instances. Write via `IClipboardService.SetTextAsync`. Competition columns use `x` / empty per FR-024.

**Rationale**: `IClipboardService` already has a write path (or can be trivially extended). TSV with tab separators is universally paste-able into Excel without CSV quoting issues.

**Alternatives considered**: CSV — rejected (commas in names/clubs require quoting, Excel import is less reliable); OLE/Excel format — rejected (Windows-only, breaks Linux portability goal).

---

## R-005: Navigation Restructure — Shell and Left Nav

**Decision**: Remove `NavigateToImportAsync` from `ShellViewModel`. Remove `ImportViewModel` as a top-level navigation target. Move competitor management into `EventSeriesOverviewViewModel` as a new `CompetitorsTab` child VM. The left nav retains only "Event Series" as the primary entry point.

**Rationale**: The spec FR-001 mandates that competitors are accessible only within Event Series context. `ShellViewModel` currently has a `NavigateToImportAsync` command wired to the left nav — this must be removed.

**Files to change**: `ShellViewModel.cs`, `ShellWindow.axaml`, `EventSeriesOverviewViewModel.cs`, `EventSeriesOverviewView.axaml`. `ImportViewModel.cs` and `ImportView.axaml` are either deleted or repurposed as internal services.

---

## R-006: Bib Removal from Competition Editor

**Decision**: Remove `BibNumber` property from `CompetitionEditorViewModel` and `CompetitionEditorView.axaml`. The `Competitor.BibNumber` domain property is retained (bibs still exist in the data model), but is rendered as a read-only column in the competitor grid only.

**Rationale**: `Competition` entity does not own bibs — `Competitor` does. The editor was showing a non-existent relationship. The bib column in the grid is informational; editing it belongs to a future Draw feature.

**Note**: `Competition.Create` and `Competition.Update` never had a Bib parameter — the change is UI-only (remove the field from ViewModel + XAML).

---

## R-007: Deleted Row Visual Style — Background-Only (No Strikethrough)

**Decision**: Deleted rows are shown with a **red background** only. Strikethrough text decoration is **not used**.

**Rationale**: Avalonia `DataGrid` row background is trivially set via a `IValueConverter` on `DataGridRow.Background` using a `RowState`-bound style. Strikethrough requires overriding `DataGridCell` `ContentTemplate` for every column — significant template overhead for minimal UX gain. A red background clearly communicates "marked for deletion" without implementation complexity.

**Colour palette** (consistent with Avalonia default theme):
- `Added` → `#d1fae5` (light green)
- `Edited` → `#fef9c3` (light yellow)
- `Deleted` → `#fee2e2` (light red)
- `PasteHighlighted` → `#dbeafe` (light blue)
- `Unchanged` → transparent

**Alternatives considered**: Full strikethrough via `TextDecorations` on each cell template — rejected (high XAML overhead, fragile with custom column templates). Opacity reduction — rejected (makes text hard to read in dense grids).
