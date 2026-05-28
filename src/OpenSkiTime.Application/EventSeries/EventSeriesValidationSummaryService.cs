using OpenSkiTime.Application.Abstractions;

namespace OpenSkiTime.Application.Series;

public sealed record ValidationSummary(
    int TotalCompetitors,
    int CompetitorsWithMissingData,
    IReadOnlyList<string> CompetitionsWithMissingData);

public sealed class EventSeriesValidationSummaryService(IEventSeriesRepository repository)
{
    public async Task<ValidationSummary?> GetAsync(Guid seriesId, CancellationToken ct = default)
    {
        var series = await repository.GetByIdAsync(seriesId, ct);
        if (series is null)
        {
            return null;
        }

        int missing = series.Competitors.Count(c => !c.IsUsableForRaceEntry);

        var badCompetitions = series.Competitions
            .Where(c => string.IsNullOrWhiteSpace(c.ShortLabel))
            .Select(c => $"{c.Name} — short label is required")
            .ToList();

        return new ValidationSummary(
            series.Competitors.Count,
            missing,
            badCompetitions);
    }
}
