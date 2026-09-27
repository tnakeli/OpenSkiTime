using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed partial class SqliteSeriesFileSession
{
    public async Task<StartListDesk> ReadStartListsAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            var series = await db.Series.AsNoTracking().SingleAsync(ct);
            var runIds = await db.Runs.Where(x => x.CompetitionId == competitionId).Select(x => x.Id).ToArrayAsync(ct);
            var lists = await db.StartLists.AsNoTracking().Where(x => runIds.Contains(x.RunId)).ToListAsync(ct);
            var revisions = new List<StartListRevision>();
            foreach (var list in lists) { revisions.Add(await ReadListAsync(db, list, ct)); }
            return new(series.Revision, revisions.OrderBy(x => x.Plan.RunNumber).ThenBy(x => x.Revision).ToArray());
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        { throw new SeriesFileException("The saved start lists could not be read.", ex); }
    }

    public async Task<StartListDesk> SaveStartListAsync(SaveStartList request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var plan = request.Plan;
        await WriteDeskAsync(async (db, _) =>
        {
            if (string.IsNullOrWhiteSpace(request.Operator) || string.IsNullOrWhiteSpace(request.Reason))
            { throw new DomainValidationException("Enter the operator and reason for this start-list revision."); }
            await ValidateStartPlanAsync(db, plan, ct);
            if (await db.StartLists.AnyAsync(x => x.StartedAt != null && db.Runs.Any(r => r.Id == x.RunId
                && r.CompetitionId == plan.CompetitionId && r.Number == plan.RunNumber), ct))
            { throw new DomainValidationException("This run has started. Its start list can no longer be redrawn."); }
            var run = await db.Runs.SingleOrDefaultAsync(x => x.CompetitionId == plan.CompetitionId
                && x.Gender == plan.Gender && x.Number == plan.RunNumber, ct);
            if (run is null)
            {
                run = new RunRow { Id = Guid.NewGuid(), CompetitionId = plan.CompetitionId, Gender = plan.Gender, Number = plan.RunNumber };
                db.Runs.Add(run);
            }
            var revision = (await db.StartLists.Where(x => x.RunId == run.Id).MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1;
            var list = new StartListRow { Id = Guid.NewGuid(), RunId = run.Id, Revision = revision,
                CreatedAt = request.At, Operator = request.Operator.Trim(), Reason = request.Reason.Trim(),
                PlanJson = JsonSerializer.Serialize(plan with { Entries = [] }) };
            db.StartLists.Add(list);
            foreach (var entry in plan.Entries)
            {
                db.StartListEntries.Add(new StartListEntryRow { ListId = list.Id, Position = entry.Position,
                    Bib = entry.Bib, CompetitorId = entry.Entrant.CompetitorId, EntryJson = JsonSerializer.Serialize(entry) });
            }
            return true;
        }, request.ExpectedRevision, ct);
        return await ReadStartListsAsync(plan.CompetitionId, ct);
    }

    public async Task<StartListDesk> ApproveStartListAsync(Guid id, long expectedRevision, DateTimeOffset at, CancellationToken ct = default)
    {
        var result = await WriteDeskAsync(async (db, _) =>
        {
            var row = await db.StartLists.SingleOrDefaultAsync(x => x.Id == id, ct)
                ?? throw new DomainValidationException("The start-list revision no longer exists.");
            if (row.ApprovedAt is not null) { throw new DomainValidationException("This revision is already approved."); }
            if (await db.StartLists.AnyAsync(x => x.RunId == row.RunId && x.Revision > row.Revision, ct))
            { throw new DomainValidationException("Approve the latest revision, not an older draft."); }
            var list = await ReadListAsync(db, row, ct);
            await ValidateStartPlanAsync(db, list.Plan, ct);
            row.ApprovedAt = at;
            return list.Plan.CompetitionId;
        }, expectedRevision, ct);
        return await ReadStartListsAsync(result.Value, ct);
    }

    public async Task<StartListDesk> MarkRunStartedAsync(Guid listId, long expectedRevision, string operatorName, DateTimeOffset at, CancellationToken ct = default)
    {
        var result = await WriteDeskAsync(async (db, _) =>
        {
            if (string.IsNullOrWhiteSpace(operatorName)) { throw new DomainValidationException("Enter the operator before marking the run started."); }
            var row = await db.StartLists.SingleOrDefaultAsync(x => x.Id == listId, ct)
                ?? throw new DomainValidationException("The start list no longer exists.");
            if (row.ApprovedAt is null || row.StartedAt is not null
                || await db.StartLists.AnyAsync(x => x.RunId == row.RunId && x.Revision > row.Revision, ct))
            { throw new DomainValidationException("Only the latest approved list can be marked started, once."); }
            var list = await ReadListAsync(db, row, ct);
            await ValidateStartPlanAsync(db, list.Plan, ct);
            row.StartedAt = at;
            row.StartedBy = operatorName.Trim();
            return list.Plan.CompetitionId;
        }, expectedRevision, ct);
        return await ReadStartListsAsync(result.Value, ct);
    }

    private static async Task<StartListRevision> ReadListAsync(SeriesDbContext db, StartListRow row, CancellationToken ct)
    {
        var plan = JsonSerializer.Deserialize<StartListPlan>(row.PlanJson)
            ?? throw new SeriesFileException("Invalid start-list metadata.");
        var entries = await db.StartListEntries.AsNoTracking().Where(x => x.ListId == row.Id).OrderBy(x => x.Position).ToArrayAsync(ct);
        return new(row.Id, row.Revision, row.CreatedAt, row.ApprovedAt, row.Operator, row.Reason,
            plan with { Entries = entries.Select(x => JsonSerializer.Deserialize<StartListEntry>(x.EntryJson)
                ?? throw new SeriesFileException("Invalid saved start-list entry.")).ToArray() })
            { StartedAt = row.StartedAt, StartedBy = row.StartedBy };
    }

    private static async Task ValidateStartPlanAsync(SeriesDbContext db, StartListPlan plan, CancellationToken ct)
    {
        var competition = await db.Competitions.SingleOrDefaultAsync(x => x.Id == plan.CompetitionId, ct)
            ?? throw new DomainValidationException("Select an existing competition.");
        if (Values(competition) != plan.Competition) { throw new DomainValidationException("Competition details changed. Prepare a new start list."); }
        StartListPlan expected;
        if (plan.RunNumber == 1)
        {
            var otherRuns = await db.Runs.Where(x => x.CompetitionId == plan.CompetitionId && x.Gender != plan.Gender && x.Number == 1).Select(x => x.Id).ToArrayAsync(ct);
            foreach (var otherRun in otherRuns)
            {
                var lists = await db.StartLists.Where(x => x.RunId == otherRun).OrderByDescending(x => x.Revision).ToArrayAsync(ct);
                var reservedLists = new[] { lists.FirstOrDefault(), lists.FirstOrDefault(x => x.ApprovedAt is not null) }
                    .OfType<StartListRow>().Select(x => x.Id).Distinct().ToArray();
                var reserved = await db.StartListEntries.Where(x => reservedLists.Contains(x.ListId)).Select(x => x.Bib).ToArrayAsync(ct);
                if (plan.Entries.Any(x => reserved.Contains(x.Bib)))
                { throw new DomainValidationException("These bibs are already used by the other field in this competition. Choose a different first bib in Draw settings."); }
            }
            // A newer first-run draft would invalidate the bibs behind a second-run list.
            if (await db.Runs.AnyAsync(x => x.CompetitionId == plan.CompetitionId && x.Gender == plan.Gender && x.Number > 1, ct))
            { throw new DomainValidationException("Run 2 already references these bibs. Run 1 can no longer be redrawn."); }
            expected = FisStartOrder.FirstRun(plan.CompetitionId, plan.Competition, plan.Gender,
                plan.Entries.Select(x => x.Entrant).ToArray(), plan.PointsList, plan.Options, plan.Seed);
        }
        else
        {
            var source = await db.StartLists.SingleOrDefaultAsync(x => x.Id == plan.SourceListId, ct)
                ?? throw new DomainValidationException("The approved source start list is missing.");
            var first = await ReadListAsync(db, source, ct);
            if (first.StartedAt is null && !await db.Runs.AnyAsync(x => x.CompetitionId == plan.CompetitionId && x.Number == 2, ct))
            { throw new DomainValidationException("Mark Run 1 started before preparing Run 2."); }
            if (first.Plan.CompetitionId != plan.CompetitionId || first.Plan.Gender != plan.Gender)
            { throw new DomainValidationException("The source list belongs to a different competition or gender."); }
            if (await db.StartLists.AnyAsync(x => x.RunId == source.RunId && x.Revision > source.Revision, ct))
            { throw new DomainValidationException("A newer Run 1 revision exists. Use that revision first."); }
            expected = FisStartOrder.SecondRun(first, plan.SourceResults);
        }
        if (JsonSerializer.Serialize(expected) != JsonSerializer.Serialize(plan))
        { throw new DomainValidationException("Start-list order does not match the recorded draw rules and inputs."); }
        var enteredIds = await db.Participations.Where(x => x.CompetitionId == plan.CompetitionId && x.Participates)
            .Select(x => x.CompetitorId).ToArrayAsync(ct);
        var athletes = await db.Competitors.Where(x => enteredIds.Contains(x.Id)).ToArrayAsync(ct);
        if (athletes.Any(x => x.Gender is null or Gender.Other))
        { throw new DomainValidationException("Assign Men or Women to every entered competitor before drawing."); }
        if (athletes.Any(x => x.Gender != plan.Gender))
        { throw new DomainValidationException("The FIS profile draws the whole competition. Use separate competitions for Men and Women; local mixed competitions need category draw rules."); }
        var scoped = athletes.ToDictionary(x => x.Id);
        var referenceEntries = plan.Entries;
        if (plan.SourceListId is { } sourceId)
        {
            var source = await db.StartLists.SingleAsync(x => x.Id == sourceId, ct);
            referenceEntries = (await ReadListAsync(db, source, ct)).Plan.Entries;
        }
        if (scoped.Count != referenceEntries.Count || referenceEntries.Any(x => !scoped.TryGetValue(x.Entrant.CompetitorId, out var a)
            || Values(a) != x.Entrant.Athlete))
        { throw new DomainValidationException("Entries or competitor details changed. Review the draw before approval."); }
    }
}
