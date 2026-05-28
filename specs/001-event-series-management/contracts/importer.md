# Contract — Importer

**Feature**: 001-event-series-management
**Module**: `OpenSkiTime.Import` + `OpenSkiTime.Application.Import`

## Purpose

Define the contract that the UI uses to (a) build an immutable preview of a
clipboard paste, and (b) apply that preview transactionally. The preview is
a pure function of inputs; only `Apply` writes.

## Interfaces

```csharp
namespace OpenSkiTime.Application.Import;

public interface IImportPreviewService
{
    /// <summary>
    /// Build an in-memory preview of an Excel/TSV paste against the current
    /// state of the given Event Series. Performs no DB writes.
    /// </summary>
    ImportPreview BuildPreview(
        Guid eventSeriesId,
        string pastedText,
        ImportOptions options);
}

public interface IImportApplyService
{
    /// <summary>
    /// Apply a previously built preview transactionally. The preview must
    /// have been built from the same Event Series snapshot; Apply verifies
    /// this by snapshot version and returns a conflict if data changed
    /// underneath.
    /// </summary>
    ImportApplyResult Apply(ImportPreview preview, ImportOptions options);
}
```

## Records

```csharp
public sealed record ImportOptions(
    bool OverwriteWithEmpty = false /* FR-053 */
);

public sealed record ImportPreview(
    Guid EventSeriesId,
    long EventSeriesSnapshotVersion,
    IReadOnlyList<HeaderMapping> Headers,
    IReadOnlyList<UnknownColumn> UnknownColumns,
    IReadOnlyList<NewCompetitorRow> New,
    IReadOnlyList<UpdatedCompetitorRow> Updated,
    IReadOnlyList<UnchangedCompetitorRow> Unchanged,
    IReadOnlyList<ErrorRow> Errors,
    IReadOnlyList<UncertainNameRow> UncertainNames
);

public sealed record HeaderMapping(string SourceHeader, MappedField Mapped);

public abstract record MappedField;
public sealed record CompetitorFieldMapping(CompetitorField Field) : MappedField;
public sealed record ParticipationMapping(Guid CompetitionId, string ShortLabel) : MappedField;
public sealed record IgnoredField(string Reason) : MappedField;

public sealed record UnknownColumn(string SourceHeader, int ColumnIndex);

public sealed record NewCompetitorRow(int SourceRowIndex, CompetitorDraft Draft);
public sealed record UpdatedCompetitorRow(
    int SourceRowIndex,
    Guid ExistingCompetitorId,
    CompetitorDraft Draft,
    IReadOnlyList<FieldDelta> Changes,
    MatchedBy Match);
public sealed record UnchangedCompetitorRow(int SourceRowIndex, Guid ExistingCompetitorId);
public sealed record ErrorRow(int SourceRowIndex, string Message);
public sealed record UncertainNameRow(
    int SourceRowIndex,
    string OriginalCombined,
    string ProposedLastName,   // already uppercase
    string ProposedFirstName,
    NewOrUpdate Disposition);

public enum MatchedBy { Code, NameYearGenderTuple }
public enum NewOrUpdate { New, Update }
public enum CompetitorField {
    Code, LastName, FirstName, Gender, YearOfBirth, Nation, Club, Association,
    DownhillPoints, SuperGPoints, GiantSlalomPoints, SlalomPoints, SuperCombinedPoints
}

public sealed record FieldDelta(CompetitorField Field, string? OldValue, string? NewValue);

public sealed record CompetitorDraft(
    string? Code,
    string? LastNameUppercase,
    string? FirstName,
    Gender? Gender,
    int? YearOfBirth,
    string? Nation,
    string? Club,
    string? Association,
    decimal? DownhillPoints,
    decimal? SuperGPoints,
    decimal? GiantSlalomPoints,
    decimal? SlalomPoints,
    decimal? SuperCombinedPoints,
    IReadOnlyDictionary<Guid /*CompetitionId*/, ParticipationDraft> Participation
);

public sealed record ParticipationDraft(bool? IsParticipating /* null = not present in source */);

public sealed record ImportApplyResult(
    bool Succeeded,
    int Created,
    int Updated,
    int ParticipationsTouched,
    string? ConflictReason
);
```

## Behavior contracts (testable)

1. **Header recognition** is case-insensitive and trimmed. Recognized
   headers (FR-051): `Code, Last Name, First Name, Name, Year, Gender,
   Nation, Club, Category, Slalom, Giant, Downhill, Super-G, Super
   Combined, Association`, plus any header matching a Competition's
   `ShortLabel` within the target Event Series.

2. **Name resolution** (FR-054, FR-055):
   - If both `Last Name` and `First Name` headers are present, `Name` is
     ignored (`IgnoredField`).
   - If only `Name` is present, every produced row goes into
     `UncertainNames`, never directly into `New` or `Updated`. The user
     promotes them by confirming the preview.
   - Last name in any case is normalized to uppercase.

3. **Match strategy** (research.md):
   - Match by `Code` if present and unique.
   - Else match by `(LastNameUpperCase, FirstName, YearOfBirth, Gender)`
     tuple; result row's `Match = NameYearGenderTuple` is shown in UI.
   - No match → `New`.

4. **Empty-cell semantics** (FR-033, FR-052, FR-053):
   - If a header is present but the cell is empty:
     - default mode: do not change existing value; no `FieldDelta`
       produced for that field.
     - `OverwriteWithEmpty = true`: clear the field; `FieldDelta(_, old,
       null)` produced.
   - If a header is **not** present, the field is never considered.

5. **Participation truthy values** (FR-032): `Yes`, `Kyllä`, `x` —
   case-insensitive, trimmed. Anything else → `IsParticipating = false`
   AND surfaced as a warning row in the preview (UI presents these in
   the Errors tab as "unknown participation value").

6. **Apply transaction** (Persistence):
   - Single EF Core transaction.
   - On any DB constraint failure, the transaction rolls back and
     `ImportApplyResult.Succeeded = false` with `ConflictReason`.

7. **Snapshot version**:
   - `EventSeriesSnapshotVersion` is the value of `EventSeries.RowVersion`
     (a per-series monotonic counter incremented on any write within the
     series). Apply rejects with `Succeeded = false` if the value changed
     since the preview was built.

## Test mapping

See plan §Test Plan, in particular `OpenSkiTime.Import.Tests`. Each
behavior contract above corresponds to at least one named test class.
