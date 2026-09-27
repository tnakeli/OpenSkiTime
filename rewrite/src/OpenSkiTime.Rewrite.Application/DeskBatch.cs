using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public sealed record DeskBatchRow(Guid? CompetitorId, CompetitorValues Values,
    IReadOnlyList<ImportEntryPatch> Entries);

public sealed record DeskBatch(Guid SeriesId, long ExpectedRevision,
    IReadOnlyList<DeskBatchRow> Rows, IReadOnlyList<Guid> DeletedIds);

public sealed record DeskBatchResult(long Revision, int Created, int Updated, int Deleted);
