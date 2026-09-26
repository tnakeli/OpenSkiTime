using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Application;

public interface ILegacyConversionPreviewer
{
    Task<LegacySourcePreview> PreviewAsync(string filePath, CancellationToken ct = default);
}

public sealed record LegacySourcePreview(string SourcePath, string SnapshotSha256,
    IReadOnlyList<LegacySeriesPreview> Series, IReadOnlyList<string> Warnings);
public sealed record LegacySeriesPreview(Guid SourceId, SeriesValues Values,
    IReadOnlyList<LegacyCompetitionPreview> Competitions,
    IReadOnlyList<LegacyCompetitorPreview> Competitors,
    IReadOnlyList<LegacyEntryPreview> Entries,
    IReadOnlyList<LegacyCategoryPreview> Categories,
    IReadOnlyList<string> Warnings);
public sealed record LegacyCompetitionPreview(Guid SourceId, CompetitionValues Values);
public sealed record LegacyCompetitorPreview(Guid SourceId, CompetitorValues Values, int? SeriesBibReference);
public sealed record LegacyEntryPreview(Guid CompetitorId, Guid CompetitionId, bool Participates,
    int? ImportedBib, int? StartOrder);
public sealed record LegacyCategoryPreview(Guid SourceId, CategoryRuleValues Values);
