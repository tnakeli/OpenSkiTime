using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;

namespace OpenSkiTime.Application.Competitions;

public sealed record AddCompetitionCommand(
    Guid EventSeriesId,
    string Name,
    string ShortLabel,
    DateOnly Date,
    Discipline Discipline,
    RaceType RaceType,
    int NumberOfRuns,
    int NumberOfIntermediateTimes,
    string? FisCode = null,
    string? LocalRaceCode = null,
    Gender? Gender = null,
    string? CourseName = null,
    int? StartAltitudeMeters = null,
    int? FinishAltitudeMeters = null,
    int? VerticalDropMeters = null,
    string? HomologationNumber = null);

public sealed class AddCompetitionUseCase
{
    private readonly IEventSeriesRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public AddCompetitionUseCase(IEventSeriesRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<Result<Guid>> ExecuteAsync(AddCompetitionCommand command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var series = await _repository.GetByIdAsync(command.EventSeriesId, ct).ConfigureAwait(false);
        if (series is null)
        {
            return Result<Guid>.Failure($"Event Series '{command.EventSeriesId}' was not found.");
        }

        Competition competition;
        try
        {
            competition = Competition.Create(
                Guid.NewGuid(),
                command.EventSeriesId,
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
            return Result<Guid>.Failure(ex.Message);
        }

        try
        {
            series.AddCompetition(competition);
        }
        catch (InvalidOperationException ex)
        {
            return Result<Guid>.Failure(ex.Message);
        }

        _repository.Update(series);
        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<Guid>.Success(competition.Id);
    }
}
