using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed record FissiRaceRow(FissiRaceOutcome Outcome)
{
    // Counts are shown only for a competition whose registrations were read.
    private bool IsRead => Outcome.Registered is not null;
    public string Race => Outcome.ShortLabel;
    public string Codex => Outcome.Codex ?? "—";
    public string Date => Outcome.Date?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "—";
    public string Registered => Number(Outcome.Registered ?? 0);
    public string New => Number(Outcome.New);
    public string Added => Number(Outcome.Added);
    public string Removed => Number(Outcome.Removed);
    public string Status => Outcome.StatusText;

    private string Number(int value) => IsRead ? value.ToString(CultureInfo.InvariantCulture) : "—";
}

public sealed partial class MainViewModel
{
    // The API key is sent in a header, so the API client never follows a redirect to another site.
    private readonly HttpClient _fissiApiHttp = informationHttp ?? new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(20) };
    [ObservableProperty] private bool _isFissiApiPanelOpen;
    [ObservableProperty] private bool _isFissiBusy;
    public ObservableCollection<FissiRaceRow> FissiApiRaces { get; } = [];
    public bool HasFissiApiRaces => FissiApiRaces.Count > 0;

    private void ClearFissiApiRaces()
    {
        FissiApiRaces.Clear();
        OnPropertyChanged(nameof(HasFissiApiRaces));
    }

    private string ReadFissiApiKey()
    {
        // Not a connection problem: the key could not be read on this computer.
        try { return _fisStore.ReadFissiApiKey() ?? string.Empty; }
        catch (IOException ex) { throw new DomainValidationException(ex.Message); }
    }

    // Reads the registrations of every competition and stages the differences; Save keeps them. The registrations are
    // compared with the saved competitors, so pending edits are saved or discarded first.
    [RelayCommand]
    private async Task UpdateFromFissiApiAsync()
    {
        if (IsFissiBusy) { return; }
        IsFissiBusy = true;
        try
        {
            await TimingListeningTask; // timing of the view just left has ended before this saves
            var series = _current ?? throw new SeriesFileException("Open an event series first.");
            var path = workspace.FilePath;
            EnsureDeskClean();
            if (series.Competitions.Count == 0) { throw new DomainValidationException("Add the competitions of the event series first."); }
            ImportWarnings.Clear();
            ClearFissiApiRaces();
            SetStatus("Reading registrations from regs.fissi.fi…");
            var (locked, _, _) = await ReadStartListLocksAsync(series.Competitions);
            var registrations = await new FissiApiClient(_fissiApiHttp, ReadFissiApiKey()).GetAsync(series.Competitions, locked);
            if (workspace.FilePath != path || _current?.Id != series.Id) { return; }
            // Start lists, competitors and competitions may have changed while the registrations were read.
            var (lockedCompetitions, lockedCompetitors, revisions) = await ReadStartListLocksAsync(series.Competitions);
            var desk = await workspace.ReadCompetitorDeskAsync();
            var saved = await workspace.ReadAsync();
            if (workspace.FilePath != path || _current is not { } current || current.Id != series.Id) { return; }
            EnsureDeskClean();
            // Staged changes are reviewed and saved in Competitors; they must not block another view.
            if (!IsCompetitorsSection)
            { throw new DomainValidationException("Competitors was left during the regs.fissi.fi update. Nothing was staged; update again in Competitors."); }
            // One revision for every start list, the desk and the series: no start list was saved between these reads.
            if (revisions.Any(x => x != desk.Revision) || saved.Revision != desk.Revision || _desk is null || !SameEntries(_desk, desk)
                || !current.Competitions.SequenceEqual(series.Competitions) || !saved.Competitions.SequenceEqual(series.Competitions))
            { throw new DomainValidationException("The event series changed during the update. Nothing was staged; update again."); }
            var plan = FissiRegistrationPlan.Build(saved, desk, registrations, lockedCompetitions, lockedCompetitors, _fisList?.Athletes);
            // Save is checked against the revision the plan was made for.
            _current = saved;
            StageFissiPlan(plan);
        }
        catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException
            or HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or PlatformNotSupportedException)
        {
            SetStatus(ex is HttpRequestException or IOException or OperationCanceledException
                ? "regs.fissi.fi cannot be reached or timed out. Nothing was changed; check the connection and try again." : ex.Message, error: true);
        }
        finally { IsFissiBusy = false; }
    }

    private void StageFissiPlan(FissiRegistrationPlan plan)
    {
        foreach (var warning in plan.Warnings) { ImportWarnings.Add(warning); }
        _suspendDeskChangeLog = true;
        try { foreach (var item in plan.Rows) { StagePastedRow(item); } }
        finally { _suspendDeskChangeLog = false; }
        foreach (var race in plan.Races) { FissiApiRaces.Add(new FissiRaceRow(race)); }
        OnPropertyChanged(nameof(HasFissiApiRaces));
        RefreshVisibleCompetitors();
        RebuildChangeLog();
        var read = plan.Races.Count(x => x.Registered is not null);
        var created = plan.Rows.Count(x => x.IsNew);
        var summary = $"regs.fissi.fi: {read} of {plan.Races.Count} competitions read, {created} new competitors, "
            + $"{plan.Rows.Count - created} changed, {plan.Races.Sum(x => x.Added)} entries added, {plan.Races.Sum(x => x.Removed)} removed."
            + (plan.Warnings.Count > 0 ? $" {plan.Warnings.Count} warnings." : "");
        SetStatus(summary + (plan.Rows.Count > 0 ? " Review the highlighted cells, then Save." : " Nothing to save."));
    }

    // A competition with a saved start list keeps its entries, and its starters keep their details.
    private async Task<(HashSet<Guid> Competitions, HashSet<Guid> Competitors, HashSet<long> Revisions)> ReadStartListLocksAsync(
        IReadOnlyList<CompetitionDetails> competitions)
    {
        var revisions = new HashSet<long>();
        var locked = new HashSet<Guid>();
        var starters = new HashSet<Guid>();
        foreach (var competition in competitions)
        {
            var lists = await workspace.ReadStartListsAsync(competition.Id);
            revisions.Add(lists.SeriesRevision);
            if (lists.Revisions.Count > 0) { locked.Add(competition.Id); }
            starters.UnionWith(lists.Revisions.SelectMany(x => x.Plan.Entries).Select(x => x.Entrant.CompetitorId));
        }
        return (locked, starters, revisions);
    }

    // The grid rows still show the saved competitors and entries the plan is compared with.
    private static bool SameEntries(CompetitorDeskDetails grid, CompetitorDeskDetails saved)
        => grid.SeriesId == saved.SeriesId && grid.Competitors.ToHashSet().SetEquals(saved.Competitors)
            && Entered(grid).SetEquals(Entered(saved));

    private static HashSet<(Guid, Guid)> Entered(CompetitorDeskDetails desk)
        => desk.Participations.Where(x => x.Participates).Select(x => (x.CompetitorId, x.CompetitionId)).ToHashSet();
}
