using System.Globalization;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Each scope contains the ones before it.
public enum SkiAlpProScope { Competitors, Run1Starters, Run1Times, Run2Starters, Run2Times }

/// <summary>TargetCompetitionId null creates a competition with NewCodex.</summary>
public sealed record SkiAlpProImportRequest(SkiAlpProRace Race, SkiAlpProScope Scope, SeriesDetails Series, CompetitorDeskDetails Desk,
    Guid? TargetCompetitionId, string NewCodex, IReadOnlyList<FisPointsListInfo> KnownLists, string Operator, DateTimeOffset At);

/// <summary>
/// Builds a test import from a Ski-Alp-Pro file: entries, start lists in the file's order and bibs, and the file's impulses
/// and classifications. Ids, clock and source are supplied; nothing is written here.
/// </summary>
public static class SkiAlpProImport
{
    public const string Device = "Ski-Alp-Pro import";
    private const string UnnamedPointsList = "Ski-Alp-Pro";

    public static string ScopeLabel(SkiAlpProScope scope) => scope switch
    {
        SkiAlpProScope.Competitors => "Competitors only",
        SkiAlpProScope.Run1Starters => "Run 1 starters (order and bibs)",
        SkiAlpProScope.Run1Times => "Run 1 starters and times",
        SkiAlpProScope.Run2Starters => "Run 1 with times, Run 2 starters",
        _ => "Run 1 and Run 2 with times"
    };

    /// <summary>The competition of the series with the file's codex, or null.</summary>
    public static CompetitionDetails? Target(SkiAlpProRace race, IEnumerable<CompetitionDetails> competitions)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(competitions);
        var matches = competitions.Where(x => SkiAlpProCodex.Same(x.Values.FisCode, race.Codex)).ToArray();
        // The same codex returns every season; the file's date decides between several.
        return matches.Length == 1 ? matches[0] : matches.FirstOrDefault(x => x.Values.Date == race.Date) ?? matches.FirstOrDefault();
    }

    /// <summary>The scopes this file has data for, in order.</summary>
    public static IReadOnlyList<SkiAlpProScope> AvailableScopes(SkiAlpProRace race)
    {
        ArgumentNullException.ThrowIfNull(race);
        var scopes = new List<SkiAlpProScope>();
        if (race.Competitors.Count == 0) { return scopes; }
        scopes.Add(SkiAlpProScope.Competitors);
        if (race.Run(1).StartBibs.Count == 0) { return scopes; }
        scopes.Add(SkiAlpProScope.Run1Starters);
        if (!HasTiming(race.Run(1))) { return scopes; }
        scopes.Add(SkiAlpProScope.Run1Times);
        if (race.RunCount < 2 || race.Run(2).StartBibs.Count == 0) { return scopes; }
        scopes.Add(SkiAlpProScope.Run2Starters);
        if (HasTiming(race.Run(2))) { scopes.Add(SkiAlpProScope.Run2Times); }
        return scopes;
    }

    private static bool HasTiming(SkiAlpProRun run)
    {
        var starters = run.StartBibs.ToHashSet();
        return run.Results.Any(x => starters.Contains(x.Bib) && (x.HasTime || x.Classification is not null))
            || run.Records.Any(x => x.Status == 0 && starters.Contains(x.Bib));
    }

    public static CompetitionValues NewCompetition(SkiAlpProRace race, string codex, IEnumerable<string> usedLabels)
    {
        ArgumentNullException.ThrowIfNull(race); ArgumentNullException.ThrowIfNull(usedLabels);
        var code = codex?.Trim() ?? "";
        var genders = race.Competitors.Select(x => x.Gender).Distinct().ToArray();
        var single = genders.Length == 1 ? genders[0] : null;
        var fisFormat = race.Discipline is Discipline.Slalom or Discipline.GiantSlalom && race.RunCount == 2
            || race.Discipline is Discipline.Downhill or Discipline.SuperG && race.RunCount == 1;
        var fis = SkiAlpProCodex.Parse(race.CodexText) is not null && single is Gender.Male or Gender.Female && fisFormat;
        var labels = usedLabels.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var baseLabel = (CompetitorFisPoints.DisciplineCode(race.Discipline) + " " + code).Trim();
        if (baseLabel.Length > 16) { baseLabel = baseLabel[..16].Trim(); }
        var label = baseLabel;
        for (var i = 2; labels.Contains(label); i++) { label = $"{baseLabel}-{i.ToString(CultureInfo.InvariantCulture)}"; }
        var calendar = new CompetitionCalendarData(FisSeason.FromDate(race.Date), race.Location, race.Nation, race.Category,
            single switch { Gender.Male => "M", Gender.Female => "W", _ => "A" }, race.TechnicalDelegate, $"Ski-Alp-Pro test import {race.FileName}");
        return new CompetitionValues(race.Title.Length > 0 ? race.Title : ("Ski-Alp-Pro " + code).Trim(), label, race.Date, race.Discipline,
            fis ? RaceType.Fis : RaceType.Club, race.RunCount, race.IntermediateCount, code.Length == 0 ? null : code,
            race.Course.Name, race.Course.Start, race.Course.Finish, race.Course.Drop, race.Course.Homologation, calendar, race.Course.Length).Validated();
    }

    public static SkiAlpProCommit Build(SkiAlpProImportRequest request, ITimingDecoderFactory decoders, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(decoders); ArgumentNullException.ThrowIfNull(newId);
        var (race, scope) = (request.Race, request.Scope);
        if (string.IsNullOrWhiteSpace(request.Operator)) { throw new DomainValidationException("An operator identity is required."); }
        if (!AvailableScopes(race).Contains(scope)) { throw new DomainValidationException($"The file has no data for: {ScopeLabel(scope)}."); }
        var notices = new List<string>(race.Notices);
        var target = request.TargetCompetitionId is { } id
            ? request.Series.Competitions.FirstOrDefault(x => x.Id == id) ?? throw new DomainValidationException("The competition no longer exists in this event series.")
            : null;
        var competition = target?.Values ?? NewCompetition(race, request.NewCodex, request.Series.Competitions.Select(x => x.Values.ShortLabel));
        if (target is null && competition.FisCode is { } code && request.Series.Competitions.Any(x => SkiAlpProCodex.Same(x.Values.FisCode, code)))
        { throw new DomainValidationException($"Codex {code} is already in this event series. Choose that competition as the target."); }
        if (target is not null)
        {
            if (competition.Discipline != race.Discipline)
            { notices.Add($"The file is {race.Discipline.DisplayName()}, {competition.ShortLabel} is {competition.Discipline.DisplayName()}; the competition keeps its own settings."); }
            if (competition.Date != race.Date)
            { notices.Add($"The file is dated {race.Date:dd.MM.yyyy}, {competition.ShortLabel} {competition.Date:dd.MM.yyyy}; the competition keeps its own date."); }
            if (scope >= SkiAlpProScope.Run1Times && race.IntermediateCount > competition.IntermediateCount)
            { competition = competition with { IntermediateCount = race.IntermediateCount }; }
        }
        if (scope >= SkiAlpProScope.Run2Starters && competition.RunCount < 2)
        { throw new DomainValidationException($"{competition.ShortLabel} has one run. Choose Run 1 only."); }

        var competitionId = target?.Id ?? newId();
        var reason = $"Ski-Alp-Pro test import ({ScopeLabel(scope)}) · {race.SourceText}";
        var (athletes, list) = Athletes(request, competition, notices, newId);
        var runs = new List<SkiAlpProRunCommit>();
        if (scope >= SkiAlpProScope.Run1Starters)
        {
            var byBib = athletes.Where(x => x.Bib is not null).ToDictionary(x => x.Bib!.Value);
            var bibs = race.Run(1).StartBibs;
            var starters = bibs.Where(byBib.ContainsKey).Select(x => byBib[x]).ToArray();
            var unknown = bibs.Where(x => !byBib.ContainsKey(x)).ToArray();
            if (unknown.Length > 0) { notices.Add($"Run 1 start list bibs without a competitor are left out: {Text(unknown)}."); }
            // The Run 1 start list decides who is entered.
            var entered = starters.Select(x => x.Id).ToHashSet();
            if (athletes.Any(x => !entered.Contains(x.Id)))
            { notices.Add($"{athletes.Count(x => !entered.Contains(x.Id))} competitor(s) of the file are not on the Run 1 start list and are not entered."); }
            athletes = athletes.Select(x => entered.Contains(x.Id) ? x : x with { Entered = false, Bib = null }).ToArray();
            var plan = FirstRun(request, competitionId, competition, starters, list);
            runs.Add(Run(request, plan, race.Run(1), scope >= SkiAlpProScope.Run1Times, notices, decoders, newId, reason));
        }
        if (scope >= SkiAlpProScope.Run2Starters)
        {
            var revision = Revision(request, runs[0], reason);
            var snapshot = Replay(request, runs[0], revision, decoders);
            var open = snapshot.Results.Where(x => x.Status is TimingStatus.Ready or TimingStatus.OnCourse or TimingStatus.Review).Select(x => x.Bib).ToArray();
            if (open.Length > 0) { throw new DomainValidationException($"Run 1 has no result in the file for bib {Text(open)}. Import Run 1 only."); }
            var plan = SecondRun(request, revision, snapshot.ToRunFinishes(), race.Run(2).StartBibs, notices);
            runs.Add(Run(request, plan, race.Run(2), scope >= SkiAlpProScope.Run2Times, notices, decoders, newId, reason));
        }
        return new(request.Series.Id, request.Series.Revision, competitionId, target is null, competition, athletes, list, runs,
            notices.Distinct().ToArray(), request.Operator.Trim(), reason, request.At);
    }

    // Competitors are matched by code, then by name and birth year when one side has no code. Values of the file replace
    // the saved ones; blank values keep them.
    private static (IReadOnlyList<SkiAlpProAthlete> Athletes, FisPointsListInfo? List) Athletes(SkiAlpProImportRequest request,
        CompetitionValues competition, List<string> notices, Func<Guid> newId)
    {
        var race = request.Race;
        var rows = race.Competitors.Where(x => x.Surname.Length > 0 || x.Code.Length > 0).ToArray();
        if (rows.Length == 0) { throw new DomainValidationException("The file has no competitors."); }
        if (rows.Length < race.Competitors.Count) { notices.Add($"{race.Competitors.Count - rows.Length} row(s) without a name and code are left out."); }
        Distinct(rows.Where(x => x.Code.Length > 0).Select(x => x.Code.ToUpperInvariant()), "code");
        Distinct(rows.Where(x => x.Bib > 0).Select(x => x.Bib.ToString(CultureInfo.InvariantCulture)), "bib");
        static void Distinct(IEnumerable<string> values, string what)
        {
            var twice = values.GroupBy(x => x).FirstOrDefault(x => x.Count() > 1)?.Key;
            if (twice is not null) { throw new DomainValidationException($"The {what} {twice} is used more than once in the file. Correct it in Ski-Alp-Pro."); }
        }
        static string? Value(string text, string? saved) => text.Trim() is { Length: > 0 } value ? value : saved;

        var list = PointsList(request, rows);
        var byCode = request.Desk.Competitors.Where(x => x.Values.FederationCode is not null)
            .ToDictionary(x => x.Values.FederationCode!, StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<Guid>();
        var result = new List<SkiAlpProAthlete>();
        var updated = 0;
        foreach (var row in rows)
        {
            var match = row.Code.Length > 0 ? byCode.GetValueOrDefault(row.Code) : null;
            if (match is null)
            {
                var named = request.Desk.Competitors.Where(x => row.BirthYear is not null && x.Values.BirthYear == row.BirthYear
                    && string.Equals(x.Values.Surname, row.Surname.Trim(), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Values.FirstName, row.FirstName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
                match = named.Length == 1 ? named[0] : null;
                if (match?.Values.FederationCode is { } other && row.Code.Length > 0)
                { throw new DomainValidationException($"{row.Surname} {row.FirstName} has code {other} in this event series and {row.Code} in the file. Correct one of them first."); }
            }
            if (match is not null && !used.Add(match.Id))
            { throw new DomainValidationException($"{row.Surname} {row.FirstName} matches a competitor that another row of the file also matches."); }
            var saved = match?.Values;
            var nation = row.Nation.Trim().ToUpperInvariant();
            if (nation.Length != 3 || !nation.All(char.IsAsciiLetterUpper)) { nation = ""; }
            var values = new CompetitorValues(Value(row.Surname, saved?.Surname) ?? "", Value(row.FirstName, saved?.FirstName) ?? "",
                row.BirthYear ?? saved?.BirthYear, Value(row.Code, saved?.FederationCode), Value(nation, saved?.Nation),
                Value(row.Club, saved?.Club), row.Gender ?? saved?.Gender).Validated(request.Series.Values.EndDate.Year);
            if (saved is not null && saved != values) { updated++; }
            var points = list is null ? null : new CompetitorFisPoints(list.Code, row.Dh, row.Sg, row.Sl, row.Gs, row.Ac).Validated();
            result.Add(new(match?.Id ?? newId(), match is null, values, points, Entered: true, row.Bib > 0 ? row.Bib : null));
        }
        if (updated > 0) { notices.Add($"{updated} competitor(s) already in this event series get the file's details."); }
        if (competition.RaceType == RaceType.Fis && result.Select(x => x.Values.Gender).Distinct().Count() > 1)
        { notices.Add("The file holds competitors of several genders. A FIS competition needs one; use a Club competition for this file."); }
        return (result, list);
    }

    // The points list named in the file. Its published validity is not in the file: a list this computer does not know is
    // valid on the dates of the imported races only and carries no penalty rules.
    private static FisPointsListInfo? PointsList(SkiAlpProImportRequest request, IEnumerable<SkiAlpProCompetitor> rows)
    {
        var code = rows.Select(x => x.PointsList).Where(x => x.Length > 0).GroupBy(x => x).OrderByDescending(x => x.Count()).FirstOrDefault()?.Key
            ?? request.Race.PointsList;
        if (code.Length is 0 or > 20) { return null; }
        var date = request.Race.Date;
        var unknown = new FisPointsListInfo(code, $"List {code} of a Ski-Alp-Pro file", date, date);
        var saved = request.Desk.PointsList(code);
        if (saved is not null && (saved.Name != unknown.Name || saved.PenaltyRules is not null)) { return saved; }
        if (request.KnownLists.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)) is { } known) { return known; }
        return saved is null ? unknown : unknown with { ValidFrom = saved.ValidFrom < date ? saved.ValidFrom : date, ValidTo = saved.ValidTo > date ? saved.ValidTo : date };
    }

    // A normal draw of the same entries (so the list stays verifiable) with the file's order and bibs recorded on top.
    private static StartListPlan FirstRun(SkiAlpProImportRequest request, Guid competitionId, CompetitionValues competition,
        SkiAlpProAthlete[] starters, FisPointsListInfo? list)
    {
        if (starters.Length == 0) { throw new DomainValidationException("No bib of the Run 1 start list has a competitor in the file."); }
        var discipline = CompetitorFisPoints.DisciplineCode(competition.Discipline);
        var seed = "ski-alp-pro:" + (request.Race.TimesFileHash ?? request.Race.FileHash);
        var options = new DrawOptions(FirstBib: starters.Min(x => x.Bib!.Value));
        StartListPlan plan;
        if (competition.RaceType == RaceType.Fis)
        {
            var gender = starters[0].Values.Gender;
            if (gender is not (Gender.Male or Gender.Female) || starters.Any(x => x.Values.Gender != gender))
            { throw new DomainValidationException("A FIS competition needs starters of one gender. Choose a Club competition for this file."); }
            plan = FisStartOrder.FirstRun(competitionId, competition, gender.Value,
                starters.Select(x => new DrawEntrant(x.Id, x.Values, x.Points?.For(discipline))).ToArray(),
                list?.ToSource() ?? new PointsListSource(UnnamedPointsList, request.Race.Date, request.Race.Date), options, seed);
        }
        else
        {
            var entrants = starters.Select(x => new DrawEntrant(x.Id, x.Values, x.Points?.For(discipline),
                request.Desk.Categories.Count == 0 ? "All" : CategoryResolver.Resolve(x.Values, request.Desk.Categories))).ToArray();
            var classes = entrants.Select(x => x.Class).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            var setup = new StartOrderSetup([new("All", classes, new FirstRunSetting(),
                new LaterRunSetting(LaterRunKind.ReverseResults, 30, NonFinishers.DoNotStart), seed)]);
            plan = FlexStartOrder.FirstRun(competitionId, competition, null, entrants, list?.ToSource() ?? PointsListSource.None, options, setup);
        }
        return FisStartOrder.Apply(plan, new(StartListChangeKind.ImportOrder, ChangeReason(request.Race),
            Order: starters.Select(x => new ImportedStart(x.Id, x.Bib!.Value)).ToArray()));
    }

    // Run 2 is the order calculated from the imported Run 1, with the file's order recorded on top.
    private static StartListPlan SecondRun(SkiAlpProImportRequest request, StartListRevision first, IReadOnlyList<RunFinish> finishes,
        IReadOnlyList<int> bibs, List<string> notices)
    {
        var plan = first.Plan.Setup is { } setup ? FlexStartOrder.LaterRun(first, finishes, setup) : FisStartOrder.SecondRun(first, finishes);
        var byBib = plan.Entries.ToDictionary(x => x.Bib, x => x.Entrant.CompetitorId);
        var extra = bibs.Where(x => !byBib.ContainsKey(x)).ToArray();
        if (extra.Length > 0)
        { throw new DomainValidationException($"The file starts bib {Text(extra)} in Run 2 without a Run 1 time. Import Run 1 only."); }
        var missing = plan.Entries.OrderBy(x => x.Position).Select(x => x.Bib).Except(bibs).ToArray();
        if (missing.Length > 0)
        { notices.Add($"Bib {Text(missing)} finished Run 1 but is not on the file's Run 2 start list: placed last without a result."); }
        return FisStartOrder.Apply(plan, new(StartListChangeKind.ImportOrder, ChangeReason(request.Race),
            Order: bibs.Concat(missing).Select(x => new ImportedStart(byBib[x], x)).ToArray()));
    }

    private static string ChangeReason(SkiAlpProRace race)
    {
        var text = "Order of Ski-Alp-Pro file " + (race.TimesFileName ?? race.FileName);
        return text.Length > 200 ? text[..200] : text;
    }

    private static StartListRevision Revision(SkiAlpProImportRequest request, SkiAlpProRunCommit run, string reason)
        => new(run.ListId, 1, request.At, null, request.Operator.Trim(), reason, run.Plan)
        { StartedAt = run.Session is null ? null : request.At, StartedBy = run.Session is null ? null : request.Operator.Trim() };

    private static TimingSnapshot Replay(SkiAlpProImportRequest request, SkiAlpProRunCommit run, StartListRevision revision, ITimingDecoderFactory decoders)
        => TimingReplay.Restore(new(revision, run.Session is null ? [] : [run.Session], run.Packets,
            run.Decisions.Select((x, i) => new TimingAudit(i + 1, run.ListId, request.At, request.Operator, revision.Reason, Before(x), x)).ToArray()), decoders);

    /// <summary>The decision an imported one replaces: none, because an imported run starts without history.</summary>
    public static TimingDecision Before(TimingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return new(decision.Kind, decision.ObservationKey, decision.CompetitorId);
    }

    private static SkiAlpProRunCommit Run(SkiAlpProImportRequest request, StartListPlan plan, SkiAlpProRun source, bool withTimes,
        List<string> notices, ITimingDecoderFactory decoders, Func<Guid> newId, string reason)
    {
        var listId = newId();
        if (!withTimes) { return new(listId, plan, null, [], []); }
        var race = request.Race;
        var file = race.TimesFileName ?? race.FileName;
        var heat = "HEAT" + source.Number.ToString(CultureInfo.InvariantCulture);
        // Simulation: imported times can never be mixed with device timing or sent to FIS as official results.
        var options = new CaptureOptions(Device, file, race.Date, Simulation: true) { Operator = request.Operator.Trim() };
        options.Validate();
        var session = new CaptureSession(newId(), listId, options, request.At, request.At, true);
        // Impulses in the order of their own times; a row whose time cannot be read comes last and stays in the journal.
        var records = source.Records.OrderBy(x => long.TryParse(x.Hour2, NumberStyles.None, CultureInfo.InvariantCulture, out var time) ? time : long.MaxValue)
            .ThenBy(x => x.Position).ThenBy(x => x.Line).ToArray();
        var rows = records.Select(x => new SkiAlpProPacket($"TTIMERECORDS_{heat}_{PositionName(x.Position)}", x.Line, x.Bib, x.Status, x.Position, x.Hour1, x.Hour2, race.TimeAccuracy))
            .Concat(source.Results.Select((x, i) => new SkiAlpProPacket("TTIMEINFOS_" + heat, i + 1, x.Bib, x.Status, null, "", "",
                race.TimeAccuracy, x.TimeMilliseconds, x.Penalty, x.Gate, x.Reason)));
        var packets = rows.Select((x, i) => new RawTimingPacket(session.Id, i + 1, request.At, SkiAlpProPacket.Protocol, file, heat, x.ToBytes())).ToArray();
        var run = new SkiAlpProRunCommit(listId, plan, session, packets, []);
        var revision = Revision(request, run, reason);
        var starters = plan.Entries.ToDictionary(x => x.Bib, x => x.Entrant.CompetitorId);

        // One impulse per starter and position: the last row Ski-Alp-Pro itself counts. Everything else is kept and ignored.
        var chosen = records.Select((x, i) => (Record: x, Sequence: (long)i + 1)).Where(x => x.Record.Status == 0 && starters.ContainsKey(x.Record.Bib))
            .GroupBy(x => (x.Record.Bib, x.Record.Position)).Select(x => x.MaxBy(r => r.Record.Line)).ToDictionary(x => x.Sequence, x => x.Record.Bib);
        var decisions = new List<TimingDecision>();
        foreach (var observation in Replay(request, run, revision, decoders).Observations.Where(x => x.State is "Unassigned" or "Review"))
        {
            var bib = observation.State == "Unassigned" && chosen.TryGetValue(observation.Observation.PacketSequence, out var starter) ? starter : (int?)null;
            decisions.Add(new(DecisionKind.Assignment, observation.Observation.Key, Bib: bib, Ignored: bib is null));
        }
        var results = source.Results.Where(x => starters.ContainsKey(x.Bib)).GroupBy(x => x.Bib).Select(x => x.Last()).ToArray();
        foreach (var result in results.Where(x => x.Classification is not null))
        {
            decisions.Add(new(DecisionKind.Status, CompetitorId: starters[result.Bib], Status: result.Classification,
                Disqualification: result.Classification == TimingStatus.DSQ ? Disqualification(result) : null));
        }
        // The impulses must give the file's own net time (truncated to hundredths); where they do not, for example after a
        // correction in Ski-Alp-Pro, the file's time is recorded as a correction.
        var snapshot = Replay(request, run with { Decisions = decisions }, revision, decoders);
        foreach (var result in results.Where(x => x.HasTime))
        {
            var hundredths = result.TimeMilliseconds / 10;
            var computed = snapshot.Results.First(x => x.Bib == result.Bib);
            if (computed.Status == TimingStatus.Finished && computed.Hundredths == hundredths) { continue; }
            if (hundredths is <= 0 or > 720000) { notices.Add($"Run {source.Number}: bib {result.Bib} has no usable time in the file."); continue; }
            decisions.Add(new(DecisionKind.Time, CompetitorId: starters[result.Bib], Hundredths: hundredths));
        }
        var unknown = results.Where(x => x.IsUnknown).Select(x => x.Bib).ToArray();
        if (unknown.Length > 0) { notices.Add($"Run {source.Number}: bib {Text(unknown)} has a status OpenSkiTime does not know and is left without a classification."); }
        var penalties = results.Where(x => x.Penalty != 0).Select(x => x.Bib).ToArray();
        if (penalties.Length > 0) { notices.Add($"Run {source.Number}: the time penalty of bib {Text(penalties)} is not imported."); }
        return run with { Decisions = decisions };
    }

    private static DisqualificationDetails Disqualification(SkiAlpProResult result)
    {
        var reason = result.Reason.Trim();
        if (reason.Length > 1000) { reason = reason[..1000]; }
        return new(result.Gate is > 0 and <= 1000 ? result.Gate : null, TextRules.IsPortable(reason) ? reason : "");
    }

    private static string PositionName(int position) => position switch
    {
        0 => "START", 1 => "FINISH", _ => "INTER" + (position - 1).ToString(CultureInfo.InvariantCulture)
    };

    private static string Text(IEnumerable<int> bibs)
    {
        var all = bibs.ToArray();
        return string.Join(", ", all.Take(12)) + (all.Length > 12 ? $" and {all.Length - 12} more" : "");
    }
}
