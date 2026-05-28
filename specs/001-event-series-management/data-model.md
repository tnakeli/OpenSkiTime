# Data Model — Event Series Management

**Feature**: 001-event-series-management
**Date**: 2026-05-28

This is the canonical entity, field, and relationship reference for this
feature. Field types use C# / EF Core / SQLite forms.

## Entities

### EventSeries (aggregate root)

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | Stable identifier |
| `Name` | `string` (≤200) | yes | FR-002 |
| `Location` | `string` (≤200) | yes | Resort/venue |
| `Organizer` | `string` (≤200) | yes | |
| `StartDate` | `DateOnly` | yes | No timezone (FR-003) |
| `EndDate` | `DateOnly` | yes | `>= StartDate` |
| `Nation` | `string` (3) | yes | ISO 3-letter (FIN, ITA, …) |
| `Season` | `string` (≤20) | yes | e.g., `2025/26` |
| `CreatedAt` | `DateTime` (UTC) | yes | Auditing only; not user-visible |
| `RowVersion` | `long` | yes | Monotonic per-series counter; incremented in `OpenSkiTimeDbContext.SaveChangesAsync` whenever any entity within the series (EventSeries itself, Competitions, Competitors, Participations, CategoryRules) is added, updated, or removed in the current change set. Used as `EventSeriesSnapshotVersion` for import-preview conflict detection (see `contracts/importer.md`). |

Owned: `Competitions`, `Competitors`, `CategoryRules`.

### Competition

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `EventSeriesId` | `Guid` (FK) | yes | cascade delete |
| `Name` | `string` (≤200) | yes | FR-012 |
| `ShortLabel` | `string` (≤20) | yes | Used as participation column header (e.g., `3.1 SL`) |
| `Date` | `DateOnly` | yes | FR-012 |
| `Discipline` | enum `Discipline` | yes | `SL, GS, SG, DH, AC, KOMBI, OTHER` |
| `RaceType` | enum `RaceType` | yes | `FIS, National, Club, Training` |
| `FisCode` | `string?` (≤50) | conditional | Required when `RaceType = FIS` (FR-013) |
| `LocalRaceCode` | `string?` (≤50) | no | |
| `Gender` | enum `Gender?` | no | When applicable |
| `CourseName` | `string?` (≤200) | no | |
| `StartAltitudeMeters` | `int?` | no | |
| `FinishAltitudeMeters` | `int?` | no | |
| `VerticalDropMeters` | `int?` | no | |
| `HomologationNumber` | `string?` (≤50) | no | Surfaced in validation summaries (FR-014) |
| `NumberOfRuns` | `int` | yes | `>= 1` (FR-012) |
| `NumberOfIntermediateTimes` | `int` | yes | `>= 0` (FR-012) |

Indexes:

- `IX_Competitions_EventSeriesId_Date`
- `UQ_Competitions_EventSeriesId_ShortLabel` (unique within series so
  participation columns are unambiguous)

### Competitor

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `EventSeriesId` | `Guid` (FK) | yes | cascade delete |
| `Code` | `string` (≤32) | yes for race entry (FR-021) | Unique within Event Series |
| `LastName` | `UpperCaseName` | yes | FR-023 — value object enforces uppercase |
| `FirstName` | `string` (≤100) | yes | FR-021 |
| `Gender` | enum `Gender` | yes | `Male, Female, Other` |
| `YearOfBirth` | `int` | yes | `1900 ≤ y ≤ currentYear` |
| `Nation` | `string` (3) | yes | ISO 3-letter |
| `Club` | `string?` (≤200) | no | FR-022 |
| `Association` | `string?` (≤200) | no | |
| `DownhillPoints` | `decimal?` | no | |
| `SuperGPoints` | `decimal?` | no | |
| `GiantSlalomPoints` | `decimal?` | no | |
| `SlalomPoints` | `decimal?` | no | |
| `SuperCombinedPoints` | `decimal?` | no | |

Indexes:

- `UQ_Competitors_EventSeriesId_Code`
- `IX_Competitors_EventSeriesId_LastName_FirstName` (grid sort/search)

### Participation

| Field | Type | Required | Notes |
|---|---|---|---|
| `Id` | `Guid` (PK) | yes | |
| `EventSeriesId` | `Guid` (FK) | yes | denormalized for cascade & query speed |
| `CompetitorId` | `Guid` (FK) | yes | cascade delete |
| `CompetitionId` | `Guid` (FK) | yes | cascade delete |
| `IsParticipating` | `bool` | yes | true ⇒ participating |

Indexes:

- `UQ_Participations_CompetitorId_CompetitionId`
- `IX_Participations_EventSeriesId`

### CategoryRule (per Event Series)

Configurable rule model (FR-025). First version supports simple birth-year
range + gender mapping.

| Field | Type | Required |
|---|---|---|
| `Id` | `Guid` (PK) | yes |
| `EventSeriesId` | `Guid` (FK) | yes |
| `CategoryName` | `string` (≤50) | yes |
| `Gender` | enum `Gender?` | no — null = any |
| `MinYearOfBirth` | `int?` | no — null = open |
| `MaxYearOfBirth` | `int?` | no — null = open |
| `Order` | `int` | yes — first match wins |

`Category` is **derived**, not stored on `Competitor`. The grid resolves it
lazily via `ICategoryResolver`.

## Value Objects

### `UpperCaseName`

```csharp
public readonly record struct UpperCaseName
{
    public string Value { get; }

    public UpperCaseName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Last name is required.");
        Value = raw.Trim().ToUpperInvariant();
    }

    public override string ToString() => Value;
    public static implicit operator string(UpperCaseName n) => n.Value;
}
```

Tests in `OpenSkiTime.Domain.Tests/UpperCaseNameTests.cs` cover FR-023.

## Enums

```csharp
public enum Discipline { SL, GS, SG, DH, AC, KOMBI, OTHER }
public enum RaceType   { FIS, National, Club, Training }
public enum Gender     { Male, Female, Other }
```

## Validation Invariants (Domain layer)

| Invariant | Enforcement |
|---|---|
| `EndDate >= StartDate` on `EventSeries` | factory + setter |
| `NumberOfRuns >= 1` | `Competition` setter |
| `NumberOfIntermediateTimes >= 0` | `Competition` setter |
| `RaceType = FIS ⇒ FisCode != null && != ""` | `Competition.SetRaceType` / factory |
| `LastName` non-empty, uppercase | `UpperCaseName` constructor |
| `FirstName` non-empty | `Competitor` factory |
| `1900 ≤ YearOfBirth ≤ currentYear` | `Competitor` factory (uses `IClock`) |
| Competitor `Code` unique within Event Series | DB unique index + Application precheck |
| Participation row unique per (Competitor, Competition) | DB unique index |

## Relationships (ER summary)

```text
EventSeries 1 ────* Competition
EventSeries 1 ────* Competitor
EventSeries 1 ────* CategoryRule
Competitor   1 ────* Participation *──── 1 Competition
```

`Participation` carries `EventSeriesId` redundantly to make
`DELETE EventSeries` a single cascade and to keep "all participations
within this series" queries indexed.

## SQLite Mapping Notes

- `DateOnly` is mapped to SQLite `TEXT` as ISO-8601 (`yyyy-MM-dd`) via the
  built-in EF Core 10 converter.
- `Guid` mapped to `BLOB` (default) — keeps the file compact.
- `decimal?` for points mapped to `TEXT` (default EF Core SQLite mapping)
  to avoid float precision loss.
- All enums stored as `INTEGER`.
- All cascading deletes are declared on the FK relationships in
  `OpenSkiTimeDbContext.OnModelCreating`.
