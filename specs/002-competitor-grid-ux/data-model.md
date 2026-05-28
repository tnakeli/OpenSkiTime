# Data Model: Competitor Grid UX Overhaul (002)

No new database entities are introduced in this feature. All changes are UI-layer and in-memory only.

---

## Existing Entities (unchanged schema)

| Entity | Owned by | Notes |
|---|---|---|
| `EventSeries` | `OpenSkiTime.Domain.Series` | Aggregate root — unchanged |
| `Competition` | `OpenSkiTime.Domain.Competitions` | No Bib field was ever on Competition; editor change is UI-only |
| `Competitor` | `OpenSkiTime.Domain.Competitors` | `BibNumber` property retained; now read-only in UI |
| `Participation` | `OpenSkiTime.Domain.Participation` | Unchanged |

---

## New In-Memory Models (session-only, never persisted)

### `ChangeLogEntry`

```
ChangeLogEntry
  Id               : Guid           -- local session ID
  Timestamp        : DateTime       -- UTC, when the change occurred
  OperationType    : ChangeOp       -- Add | Edit | Delete
  CompetitorId     : Guid?          -- null for new (not yet saved) competitors
  FieldName        : string?        -- null for Add/Delete; field name for Edit
  BeforeValue      : string?        -- null for Add; serialized previous value
  AfterValue       : string?        -- null for Delete; serialized new value
  DisplayLabel     : string         -- e.g. "SMITH John — Year edited 2005→2006"
```

`ChangeOp` enum: `Add`, `Edit`, `Delete`

### `StagedRow` (backing for `CompetitorRowViewModel`)

The existing `CompetitorRowViewModel` gains new observable properties:

```
CompetitorRowViewModel (extended)
  + RowState          : RowState    -- Unchanged | Added | Edited | Deleted | PasteHighlighted
  + IsEditing         : bool        -- true when cell editor is active
  + PendingChanges    : List<ChangeLogEntry>   -- entries produced by this row's edits
```

`RowState` enum: `Unchanged`, `Added`, `Edited`, `Deleted`, `PasteHighlighted`

### `ImportPasteSession`

Transient state owned by `CompetitorGridViewModel` while a paste operation is pending:

```
ImportPasteSession
  Rows              : IReadOnlyList<CompetitorRowViewModel>   -- highlighted rows
  SnapshotVersion   : long                                    -- version at paste time
  IsActive          : bool                                    -- true while awaiting Accept/Discard
```

---

## ViewModel Hierarchy (new)

```
ShellViewModel
  └── EventSeriesOverviewViewModel
        ├── CompetitionsTabViewModel        (existing)
        └── CompetitorGridViewModel         (moved here; no longer top-level nav target)
              ├── CompetitorRowViewModel[]  (extended with RowState)
              ├── CompetitorChangeLog       (new: ObservableCollection<ChangeLogEntry>)
              └── ImportPasteSession?       (new: non-null while paste pending)
```

---

## Removed Navigation Target

`ImportViewModel` is no longer a top-level navigation target in `ShellViewModel`. Its import services (`ImportPreviewService`, `ImportApplyService`) are consumed directly by `CompetitorGridViewModel`.
