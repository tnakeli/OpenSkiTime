using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Application.Competitors;

public sealed record CompetitorDto(
    Guid Id,
    Guid EventSeriesId,
    string LastName,
    string FirstName,
    int YearOfBirth,
    string? FisCode,
    string? NationCode,
    string? ClubName,
    Gender? Gender,
    int? BibNumber);

public sealed class ListCompetitorsUseCase(IEventSeriesRepository repository)
{
    public async Task<Result<IReadOnlyList<CompetitorDto>>> ExecuteAsync(
        Guid eventSeriesId,
        CancellationToken ct = default)
    {
        var series = await repository.GetByIdAsync(eventSeriesId, ct);
        if (series is null)
        {
            return Result<IReadOnlyList<CompetitorDto>>.Failure(
                $"Event Series {eventSeriesId} not found.");
        }

        var dtos = series.Competitors
            .Select(c => new CompetitorDto(
                c.Id,
                c.EventSeriesId,
                c.LastName.Value,
                c.FirstName,
                c.YearOfBirth,
                c.FisCode,
                c.NationCode,
                c.ClubName,
                c.Gender,
                c.BibNumber))
            .ToList();

        return Result<IReadOnlyList<CompetitorDto>>.Success(dtos);
    }
}
