using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

// FisPointsSpecified: replace the competitor's saved FIS points with FisPoints (null removes them).
public sealed record DeskBatchRow(Guid? CompetitorId, CompetitorValues Values,
    IReadOnlyList<ImportEntryPatch> Entries, CompetitorFisPoints? FisPoints = null, bool FisPointsSpecified = false);

// PointsLists: the FIS points lists the saved points refer to (identity, validity and penalty rules).
public sealed record DeskBatch(Guid SeriesId, long ExpectedRevision,
    IReadOnlyList<DeskBatchRow> Rows, IReadOnlyList<Guid> DeletedIds, IReadOnlyList<FisPointsListInfo>? PointsLists = null);

public sealed record DeskBatchResult(long Revision, int Created, int Updated, int Deleted);
