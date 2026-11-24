using System.Globalization;
using System.Text;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

public enum FissiRaceStatus
{
    Ready, DateDiffers, NoneRegistered, NoCodex, NotFound, RaceDiffers, SeveralMatches, StartListSaved,
}

/// <summary>The registration page found for a competition; the page is present only when its athletes may be used.</summary>
public sealed record FissiCompetition(Guid CompetitionId, string? Codex, FissiRaceStatus Status, FissiRacePage? Page = null, string Gender = "");

/// <summary>Reads the registrations of a series' competitions from the public pages of regs.fissi.fi.</summary>
public sealed class FissiRegistrationClient(HttpClient client)
{
    private const string Host = "regs.fissi.fi";
    private const int MaximumBytes = 5_000_000;
    private static readonly TimeSpan s_requestTimeout = TimeSpan.FromSeconds(20);
    private static readonly UTF8Encoding s_utf8 = new(false, throwOnInvalidBytes: true);

    /// <summary>
    /// Finds the race of every competition by codex, season, gender and discipline and reads its registrations.
    /// Competitions in <paramref name="locked"/> are not looked up. Fails as a whole when a page cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<FissiCompetition>> GetAsync(IReadOnlyList<CompetitionDetails> competitions,
        IReadOnlySet<Guid> locked, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(competitions); ArgumentNullException.ThrowIfNull(locked);
        var lists = new Dictionary<bool, IReadOnlyList<FissiRace>>();
        var pages = new Dictionary<int, FissiRacePage>();
        var found = new List<(FissiCompetition Competition, int RaceId)>();
        foreach (var competition in competitions)
        {
            var codex = Codex(competition.Values.FisCode);
            if (codex is null) { found.Add((new(competition.Id, competition.Values.FisCode, FissiRaceStatus.NoCodex), 0)); continue; }
            if (locked.Contains(competition.Id)) { found.Add((new(competition.Id, codex, FissiRaceStatus.StartListSaved), 0)); continue; }
            found.Add(await FindAsync(competition, codex));
        }
        // One registration list cannot belong to two competitions.
        var shared = found.Where(x => x.Competition.Page is not null).GroupBy(x => x.RaceId).Where(x => x.Count() > 1).Select(x => x.Key).ToHashSet();
        return found.Select(x => x.Competition.Page is not null && shared.Contains(x.RaceId)
            ? x.Competition with { Status = FissiRaceStatus.SeveralMatches, Page = null } : x.Competition).ToArray();

        async Task<(FissiCompetition, int)> FindAsync(CompetitionDetails competition, string codex)
        {
            var status = FissiRaceStatus.NotFound;
            // Upcoming races first; the past list is read only for a race that is not among them.
            foreach (var past in new[] { false, true })
            {
                if (!lists.TryGetValue(past, out var races))
                {
                    races = FissiRegistrationHtml.ReadRaces(await GetTextAsync($"index.php?lang=en&showPast={(past ? 1 : 0)}", ct));
                    lists.Add(past, races);
                }
                var sameCodex = races.Where(x => x.Codex == codex).DistinctBy(x => x.RaceId).ToArray();
                var candidates = sameCodex.Where(x => SameRace(competition.Values, x)).ToArray();
                if (sameCodex.Length > 0 && candidates.Length == 0) { status = FissiRaceStatus.RaceDiffers; }
                var matches = new List<(FissiRace Race, FissiRacePage Page)>();
                foreach (var race in candidates)
                {
                    if (!pages.TryGetValue(race.RaceId, out var page))
                    {
                        page = FissiRegistrationHtml.ReadRace(await GetTextAsync(
                            $"registered_athletes.php?race_id={race.RaceId.ToString(CultureInfo.InvariantCulture)}&lang=en", ct));
                        if (page.Codex != race.Codex)
                        { throw new DomainValidationException($"regs.fissi.fi shows another codex than {codex} on the registration page. Nothing was changed."); }
                        pages.Add(race.RaceId, page);
                    }
                    // FIS uses the same codexes again every season.
                    if (FisSeason.FromDate(page.Date) == FisSeason.FromDate(competition.Values.Date)) { matches.Add((race, page)); }
                }
                if (matches.Count > 1) { return (new(competition.Id, codex, FissiRaceStatus.SeveralMatches), 0); }
                if (matches.Count == 1)
                {
                    var (race, page) = matches[0];
                    return (new(competition.Id, codex, page.Athletes.Count == 0 ? FissiRaceStatus.NoneRegistered
                        : page.Date == competition.Values.Date ? FissiRaceStatus.Ready : FissiRaceStatus.DateDiffers, page, race.Gender), race.RaceId);
                }
            }
            return (new(competition.Id, codex, status), 0);
        }
    }

    private static string? Codex(string? value) => value?.Trim() is { Length: > 0 and <= 4 } text && text.All(char.IsAsciiDigit)
        ? text.PadLeft(4, '0') : null;

    // Compared only when both sides tell it.
    private static bool SameRace(CompetitionValues competition, FissiRace race)
    {
        var gender = CompetitionGenderRule.Required(competition.Calendar?.Gender);
        var raceGender = CompetitionGenderRule.Required(race.Gender);
        var discipline = competition.Discipline switch
        {
            Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG",
            Discipline.Downhill => "DH", Discipline.AlpineCombined => "AC", _ => string.Empty,
        };
        return (gender is null || raceGender is null || gender == raceGender)
            && (discipline.Length == 0 || race.Discipline is not ("SL" or "GS" or "SG" or "DH" or "AC") || race.Discipline == discipline);
    }

    private async Task<string> GetTextAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_requestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{Host}/public/{path}");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        { throw new DomainValidationException($"regs.fissi.fi lookup failed (HTTP {(int)response.StatusCode}). Nothing was changed."); }
        if (response.RequestMessage?.RequestUri is { } final && (final.Scheme != Uri.UriSchemeHttps || !final.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)))
        { throw new DomainValidationException("regs.fissi.fi redirected to another site. Nothing was changed."); }
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = await input.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + count > MaximumBytes) { throw new DomainValidationException("The regs.fissi.fi page is unexpectedly large. Nothing was changed."); }
            await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token);
        }
        try { return s_utf8.GetString(output.GetBuffer(), 0, (int)output.Length); }
        catch (DecoderFallbackException) { throw new DomainValidationException("The regs.fissi.fi page has unreadable characters. Nothing was changed."); }
    }
}
