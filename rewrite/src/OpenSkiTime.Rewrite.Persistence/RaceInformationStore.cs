using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed class RaceInformationRow
{
    public Guid CompetitionId { get; set; }
    public int Revision { get; set; }
    public string ValuesJson { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
}

internal sealed partial class SqliteSeriesFileSession : IRaceInformationStore
{
    public async Task<SavedRaceInformation?> ReadRaceInformationAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            var row = await db.Set<RaceInformationRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            return row is null ? null : new(row.CompetitionId, row.Revision,
                JsonSerializer.Deserialize<RaceInformation>(row.ValuesJson) ?? throw new SeriesFileException("Race information is unreadable."), row.SavedAt);
        }
        catch (JsonException ex) { throw new SeriesFileException("Race information is unreadable. Restore a backup or contact support.", ex); }
        catch (SqliteException ex) { throw SqliteSeriesFileStore.FileError(ex); }
    }

    public async Task<long> SaveRaceInformationAsync(Guid competitionId, RaceInformation values, long expectedSeriesRevision,
        DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = await WriteDeskAsync(async (db, series) =>
        {
            var competition = await db.Competitions.SingleOrDefaultAsync(x => x.Id == competitionId, ct)
                ?? throw new DomainValidationException("The competition no longer exists.");
            values.Validate(competition.RunCount);
            var revision = (await db.Set<RaceInformationRow>().Where(x => x.CompetitionId == competitionId)
                .MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1;
            db.Add(new RaceInformationRow { CompetitionId = competitionId, Revision = revision,
                ValuesJson = JsonSerializer.Serialize(values), SavedAt = at });
            return revision;
        }, expectedSeriesRevision, ct, allowCaptureOwner: true);
        return result.Revision;
    }
}
