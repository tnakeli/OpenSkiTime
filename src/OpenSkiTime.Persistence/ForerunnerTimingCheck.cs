using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Persistence;

// A run's forerunners are saved in the race information; their impulses are in the run's timing journal. Both writes
// check the other half inside the same serialized write, so the file never holds an impulse assigned to a forerunner
// that is not saved for that run.
internal sealed partial class SqliteSeriesFileSession
{
    // Forerunner keys that currently own an impulse in any start-list revision of the run.
    private static async Task<HashSet<int>> TimedForerunnerKeysAsync(SeriesDbContext db, Guid competitionId, int runNumber, CancellationToken ct)
    {
        var listIds = await db.StartLists.Where(l => db.Runs.Any(r => r.Id == l.RunId && r.CompetitionId == competitionId && r.Number == runNumber))
            .Select(l => l.Id).ToArrayAsync(ct);
        if (listIds.Length == 0) { return []; }
        var current = new Dictionary<(Guid, string), TimingDecision>();
        foreach (var row in await db.TimingAudit.AsNoTracking().Where(x => listIds.Contains(x.ListId)).OrderBy(x => x.Id).ToArrayAsync(ct))
        {
            var audit = ToAudit(row);
            current[(audit.ListId, TimingEngine.DecisionKey(audit.After))] = audit.After;
        }
        return current.Values.Where(x => x.Kind == DecisionKind.Assignment && x.Bib is { } bib && TimingForerunner.IsKey(bib))
            .Select(x => x.Bib!.Value).ToHashSet();
    }

    // The run's forerunners as saved in the race information, in their listed order.
    private static async Task<TimingForerunner[]> SavedForerunnersAsync(SeriesDbContext db, Guid competitionId, int runNumber, CancellationToken ct)
    {
        var json = await db.Set<RaceInformationRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .OrderByDescending(x => x.Revision).Select(x => x.ValuesJson).FirstOrDefaultAsync(ct);
        if (json is null) { return []; }
        try
        {
            var values = JsonSerializer.Deserialize<RaceInformation>(json);
            return (values?.Runs.FirstOrDefault(x => x.Number == runNumber)?.Forerunners ?? [])
                .Select(x => new TimingForerunner(x.Letter.Trim().ToUpperInvariant(), x.Person.FirstName, x.Person.LastName, x.Person.Nation)).ToArray();
        }
        catch (JsonException ex) { throw new SeriesFileException("Race information is unreadable. Restore a backup or contact support.", ex); }
    }

    // The saved identifiers of the run's forerunners (upper case).
    private static async Task<string[]> SavedForerunnerLettersAsync(SeriesDbContext db, Guid competitionId, int runNumber, CancellationToken ct)
        => (await SavedForerunnersAsync(db, competitionId, runNumber, ct)).Select(x => x.Letter).ToArray();

    private static async Task<HashSet<int>> SavedForerunnerKeysAsync(SeriesDbContext db, Guid competitionId, int runNumber, CancellationToken ct)
        => (await SavedForerunnerLettersAsync(db, competitionId, runNumber, ct)).Select(TimingForerunner.KeyOf).ToHashSet();

    // Identifiers saved for the run in any race-information revision, by timing key.
    private static async Task<ILookup<int, string>> EverSavedForerunnerLettersAsync(SeriesDbContext db, Guid competitionId, int runNumber, CancellationToken ct)
    {
        var letters = new List<string>();
        foreach (var json in await db.Set<RaceInformationRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId).Select(x => x.ValuesJson).ToArrayAsync(ct))
        {
            try
            {
                letters.AddRange((JsonSerializer.Deserialize<RaceInformation>(json)?.Runs.FirstOrDefault(x => x.Number == runNumber)?.Forerunners ?? [])
                    .Select(x => x.Letter.Trim().ToUpperInvariant()));
            }
            catch (JsonException ex) { throw new SeriesFileException("Race information is unreadable. Restore a backup or contact support.", ex); }
        }
        return letters.Distinct().ToLookup(TimingForerunner.KeyOf);
    }

    private static async Task EnsureTimedForerunnersKeptAsync(SeriesDbContext db, Guid competitionId, int runCount, RaceInformation values, CancellationToken ct)
    {
        for (var number = 1; number <= runCount; number++)
        {
            // Timing identifies a forerunner by a key derived from the identifier. Within a run, each key belongs to one
            // identifier for good: a different identifier with the same key could inherit another forerunner's
            // impulses, statuses or undo history. (Realistic identifiers such as A–Z or F27 never collide.)
            var everSaved = await EverSavedForerunnerLettersAsync(db, competitionId, number, ct);
            var taken = (values.Runs.FirstOrDefault(x => x.Number == number)?.Forerunners ?? []).Select(x => x.Letter.Trim().ToUpperInvariant())
                .FirstOrDefault(x => everSaved[TimingForerunner.KeyOf(x)].Any(other => other != x));
            if (taken is not null)
            { throw new DomainValidationException($"Forerunner identifier {taken} cannot be used in Run {number}: timing cannot tell it apart from an earlier forerunner of this run. Choose another identifier."); }
            var timed = await TimedForerunnerKeysAsync(db, competitionId, number, ct);
            if (timed.Count == 0) { continue; }
            var kept = (values.Runs.FirstOrDefault(x => x.Number == number)?.Forerunners ?? []).Select(x => x.Letter.Trim().ToUpperInvariant()).ToHashSet();
            // Compare identifiers, not only keys: two identifiers can share a key, and a timed forerunner keeps its own.
            var saved = await SavedForerunnerLettersAsync(db, competitionId, number, ct);
            if (timed.Any(key => saved.FirstOrDefault(x => TimingForerunner.KeyOf(x) == key) is { } letter
                    ? !kept.Contains(letter) : !kept.Any(x => TimingForerunner.KeyOf(x) == key)))
            {
                throw new DomainValidationException($"A Run {number} forerunner has timing impulses. Unassign them in Timing before removing the forerunner or changing its identifier.");
            }
        }
    }
}
