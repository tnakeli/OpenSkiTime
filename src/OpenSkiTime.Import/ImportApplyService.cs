using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Common;
using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Domain.Participation;

namespace OpenSkiTime.Import;

/// <summary>
/// Applies an <see cref="ImportDiff"/> that was previewed by
/// <see cref="ImportPreviewService"/>. Guards against concurrent changes by
/// re-reading the snapshot version before writing.
/// </summary>
public sealed class ImportApplyService
{
    private readonly IEventSeriesRepository _repository;
    private readonly IParticipationRepository _participationRepo;
    private readonly IUnitOfWork _unitOfWork;
    private readonly AddCompetitorUseCase _addCompetitor;
    private readonly AssignBibUseCase _assignBib;

    public ImportApplyService(
        IEventSeriesRepository repository,
        IParticipationRepository participationRepo,
        IUnitOfWork unitOfWork,
        AddCompetitorUseCase addCompetitor,
        AssignBibUseCase assignBib)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _participationRepo = participationRepo ?? throw new ArgumentNullException(nameof(participationRepo));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _addCompetitor = addCompetitor ?? throw new ArgumentNullException(nameof(addCompetitor));
        _assignBib = assignBib ?? throw new ArgumentNullException(nameof(assignBib));
    }

    /// <summary>
    /// Applies the diff to the event series.
    /// <paramref name="previewSnapshotVersion"/> must match the current DB
    /// snapshot version — if not, the series was modified concurrently and
    /// the caller must re-run the preview.
    /// </summary>
    public async Task<Result<ImportApplyResult>> ApplyAsync(
        Guid eventSeriesId,
        long previewSnapshotVersion,
        ImportDiff diff,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(diff);

        // Concurrency guard.
        var snapshot = await _repository.LoadSnapshotAsync(eventSeriesId, ct);
        if (snapshot is null)
        {
            return Result<ImportApplyResult>.Failure(
                $"Event Series {eventSeriesId} not found.");
        }

        if (snapshot.Version != previewSnapshotVersion)
        {
            return Result<ImportApplyResult>.Failure(
                "The event series was modified since the preview was generated. " +
                "Please re-run the import preview.");
        }

        int added = 0;
        int bibsAssigned = 0;
        int participationsSet = 0;
        var errors = new List<string>();

        // Map competition ShortLabel → Id (from snapshot — stable across this call).
        var competitionIdByLabel = snapshot.Competitions
            .ToDictionary(c => c.ShortLabel, c => c.Id, StringComparer.OrdinalIgnoreCase);

        // ── 1. Add new competitors ─────────────────────────────────────────
        foreach (var nc in diff.NewCompetitors)
        {
            var addResult = await _addCompetitor.ExecuteAsync(new AddCompetitorRequest
            {
                EventSeriesId = eventSeriesId,
                LastName = nc.LastName,
                FirstName = nc.FirstName,
                YearOfBirth = nc.YearOfBirth,
                FisCode = nc.FisCode,
                NationCode = nc.NationCode,
                ClubName = nc.ClubName,
            }, ct);

            if (!addResult.Succeeded)
            {
                errors.Add($"Add {nc.LastName} {nc.FirstName}: {addResult.ErrorMessage}");
                continue;
            }

            added++;
            var newId = addResult.Value;

            // Assign bib if present.
            if (nc.BibNumber.HasValue)
            {
                var bibResult = await _assignBib.ExecuteAsync(
                    eventSeriesId, newId, nc.BibNumber.Value, ct);
                if (bibResult.Succeeded)
                {
                    bibsAssigned++;
                }
                else
                {
                    errors.Add($"Bib {nc.BibNumber} for {nc.LastName}: {bibResult.ErrorMessage}");
                }
            }

            // Set participations directly via repository.
            foreach (var label in nc.ParticipatingIn)
            {
                if (!competitionIdByLabel.TryGetValue(label, out var compId))
                {
                    continue;
                }

                var participation = Participation.Create(
                    Guid.NewGuid(), newId, compId, isParticipating: true);
                await _participationRepo.AddAsync(participation, ct);
                participationsSet++;
            }
        }

        if (added > 0 || participationsSet > 0)
        {
            await _unitOfWork.SaveChangesAsync(ct);
        }

        // ── 2. Bib assignments for existing competitors ────────────────────
        foreach (var ba in diff.BibAssignments)
        {
            var bibResult = await _assignBib.ExecuteAsync(
                eventSeriesId, ba.CompetitorId, ba.NewBib, ct);
            if (bibResult.Succeeded)
            {
                bibsAssigned++;
            }
            else
            {
                errors.Add($"Bib {ba.NewBib} for {ba.LastName}: {bibResult.ErrorMessage}");
            }
        }

        // ── 3. Participation flags for existing competitors ────────────────
        if (diff.Participations.Count > 0)
        {
            var live = await _repository.GetByIdAsync(eventSeriesId, ct);
            if (live is not null)
            {
                foreach (var pd in diff.Participations)
                {
                    var competitor = live.Competitors
                        .FirstOrDefault(c => c.Id == pd.CompetitorId);
                    if (competitor is null)
                    {
                        continue;
                    }

                    var existing = competitor.Participations
                        .FirstOrDefault(p => p.CompetitionId == pd.CompetitionId);
                    if (existing is not null)
                    {
                        existing.SetParticipating(pd.IsParticipating);
                    }
                    else
                    {
                        var newP = Participation.Create(
                            Guid.NewGuid(),
                            pd.CompetitorId,
                            pd.CompetitionId,
                            pd.IsParticipating);
                        await _participationRepo.AddAsync(newP, ct);
                    }

                    participationsSet++;
                }

                await _unitOfWork.SaveChangesAsync(ct);
            }
        }

        return Result<ImportApplyResult>.Success(
            new ImportApplyResult(added, bibsAssigned, participationsSet, errors));
    }
}

public sealed record ImportApplyResult(
    int CompetitorsAdded,
    int BibsAssigned,
    int ParticipationsSet,
    IReadOnlyList<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}
