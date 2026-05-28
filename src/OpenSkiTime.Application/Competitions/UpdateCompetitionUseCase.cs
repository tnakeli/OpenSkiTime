using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Application.Competitions;

public sealed record UpdateCompetitionCommand(
    Guid EventSeriesId,
    Guid CompetitionId,
    string Name,
    string ShortLabel,
    DateOnly Date,
    Discipline Discipline,
    RaceType RaceType,
    int NumberOfRuns,
    int NumberOfIntermediateTimes,
    string? FisCode,
    string? LocalRaceCode,
    Gender? Gender,
    string? CourseName,
    int? StartAltitudeMeters,
    int? FinishAltitudeMeters,
    int? VerticalDropMeters,
    string? HomologationNumber);

public sealed class UpdateCompetitionUseCase
{
    private readonly IEventSeriesRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public UpdateCompetitionUseCase(IEventSeriesRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<Result> ExecuteAsync(UpdateCompetitionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var series = await _repository.GetByIdAsync(command.EventSeriesId, ct).ConfigureAwait(false);
        if (series is null)
        {
            return Result.Failure($"Event Series '{command.EventSeriesId}' was not found.");
        }

        var competition = series.Competitions.FirstOrDefault(c => c.Id == command.CompetitionId);
        if (competition is null)
        {
            return Result.Failure($"Competition '{command.CompetitionId}' was not found in this Event Series.");
        }

        try
        {
            competition.UpdateBasicData(
                command.Name,
                command.ShortLabel,
                command.Date,
                command.Discipline,
                command.RaceType,
                command.NumberOfRuns,
                command.NumberOfIntermediateTimes,
                command.FisCode,
                command.LocalRaceCode,
                command.Gender,
                command.CourseName,
                command.StartAltitudeMeters,
                command.FinishAltitudeMeters,
                command.VerticalDropMeters,
                command.HomologationNumber);
        }
        catch (ArgumentException ex)
        {
            return Result.Failure(ex.Message);
        }

        _repository.Update(series);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }
}
