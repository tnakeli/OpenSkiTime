# Quickstart: Competitor Grid UX Overhaul (002)

## Prerequisites

- Feature 001 merged to `master` (Event Series, Competitions, Competitors, Import pipeline all present)
- .NET 10 SDK installed
- SQLite DB from feature 001 present at `%LOCALAPPDATA%\OpenSkiTime\openskitime.db` (or created fresh on first run)

## Build & Run

```powershell
dotnet build OpenSkiTime.slnx
dotnet run --project src\OpenSkiTime.Desktop\OpenSkiTime.Desktop.csproj
```

## Demo Walkthrough

### 1. Competitors within Event Series (US1)

1. Open the application.
2. Observe: left nav shows only "Event Series" — no "Import Competitors" item.
3. Select or create an Event Series.
4. Observe: Event Series detail view shows two tabs/sections: **Competitions** and **Competitors**.

### 2. Inline Add / Edit / Delete + Change Log (US2)

1. Open the **Competitors** section of an Event Series.
2. Click **Add** — a new empty row appears at the top of the grid in edit mode.
3. Enter Last Name (`SMITH`), First Name (`John`), Year (`2005`). The row is highlighted green (Added).
4. Click an existing competitor's Year cell — edit it inline. Row turns yellow (Edited).
5. Select a competitor and click **Delete** — row turns red/strikethrough (Deleted).
6. Expand the **Change Log** panel — three entries are listed.
7. Click **Restore** on the Delete entry — the row reappears in the grid.
8. Click **Discard all changes** — the grid resets to its persisted state; Change Log clears.
9. Make changes again, then click **Save** — all changes commit to the database; Change Log clears.

### 3. Inline Paste (US3)

1. Copy competitor rows from Excel (columns: Last Name, First Name, Year, Nation).
2. In the **Competitors** section, press **Ctrl+V** or click **Paste**.
3. Rows appear highlighted (blue / paste colour) in the grid — no navigation occurs.
4. An **Apply import** and **Discard import** bar appears above the grid.
5. Click **Apply import** — rows are committed; highlighting clears.
6. Paste again, then click **Discard import** — grid reverts, no DB write.

### 4. Copy to Clipboard (US4)

1. Select one or more competitor rows (Ctrl+click for multi-select).
2. Press **Ctrl+C** or click **Copy** in the toolbar.
3. Open Excel, press **Ctrl+V**.
4. Verify: columns appear in order `Last Name | First Name | Year | Gender | Nation | Club | FIS Code | [competition short labels]`. Last names are in UPPERCASE.

### 5. Competition Editor — No Bib (US5)

1. Open any Competition (edit or create).
2. Verify: no **Bib Number** field is present in the form.
3. Return to the Competitors grid — the **Bib** column is visible but greyed out (read-only).

## Run Tests

```powershell
dotnet test OpenSkiTime.slnx --nologo
```

All tests must pass (green).
