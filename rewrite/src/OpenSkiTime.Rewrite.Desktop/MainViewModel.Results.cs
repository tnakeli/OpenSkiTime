using System.Collections.ObjectModel;
using System.Globalization;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record ResultReviewRow(string Rank, int Bib, string Code, string Name, string Run1,
    string Run2, string Total, string Status, string RacePoints);
public sealed record PenaltyReviewRow(int Bib, string Name, string Listed, string Used,
    string RacePoints, string Note, string Rank = "", string Code = "", string Year = "", string Nation = "", string Status = "", string PenaltyRacePoints = "");

public sealed partial class MainViewModel
{
    private FisRaceResult? _resultRace;
    private FisPenaltyResult? _reviewedPenalty;
    private string? _resultFingerprint;
    private int _resultsLoad;
    private FisPenaltyProfile? _resultsRuleProfile;
    public ObservableCollection<ResultReviewRow> ResultRows { get; } = [];
    public ObservableCollection<PenaltyReviewRow> ResultBestClassified { get; } = [];
    public ObservableCollection<PenaltyReviewRow> ResultBestStarted { get; } = [];
    public ObservableCollection<PenaltyReviewRow> ResultTopTen { get; } = [];
    public ObservableCollection<ApprovedResult> ResultApprovals { get; } = [];
    [ObservableProperty] private CompetitionDetails? _resultsCompetition;
    [ObservableProperty] private string _resultsState = "Choose a FIS competition.";
    [ObservableProperty] private string _resultsPointsList = string.Empty;
    [ObservableProperty] private string _resultsPenaltySummary = string.Empty;
    [ObservableProperty] private string _resultsRuleSource = string.Empty;
    [ObservableProperty] private string _resultsRuleValues = string.Empty;
    [ObservableProperty] private string _resultsCategory = string.Empty;
    [ObservableProperty] private ApprovedResult? _selectedResultApproval;
    public bool ResultsHasSecondRun => ResultsCompetition?.Values.RunCount == 2;
    public bool ResultsIsFis => ResultsCompetition?.Values.RaceType == RaceType.Fis;
    public bool ResultsReady => _resultRace is not null && _reviewedPenalty is not null;

    partial void OnResultsCompetitionChanged(CompetitionDetails? value)
    {
        OnPropertyChanged(nameof(ResultsHasSecondRun));
        OnPropertyChanged(nameof(ResultsIsFis));
        OnPropertyChanged(nameof(WindowTitle));
        if (IsResultsSection)
        {
            if (value is not null) { SetActiveRace(value, _activeRaceId == value.Id ? _activeRaceRun : 1, _activeRaceSection); }
            _ = LoadResultsAsync();
        }
        else if (value is not null && !workspace.IsOpen)
        {
            ResultsJury.Clear(); ResultsRuns.Clear();
            var empty = RaceInformation.Empty(value.Values);
            foreach (var row in empty.Jury.Where(x => x.Function != "TechnicalDelegate")) { ResultsJury.Add(new(row.Function, row.Person)); }
            foreach (var row in empty.Runs) { ResultsRuns.Add(new(row)); }
        }
    }

    [RelayCommand]
    private async Task LoadResultsAsync()
    {
        var load = ++_resultsLoad;
        _resultRace = null; _resultFingerprint = null;
        _resultsRuleProfile = null; ResultsRuleSource = ""; ResultsRuleValues = "";
        _reviewedPenalty = null;
        ResultRows.Clear(); ResultBestClassified.Clear(); ResultBestStarted.Clear(); ResultApprovals.Clear();
        ResultTopTen.Clear();
        ResultsPenaltySummary = string.Empty;
        ResultsStatistics = string.Empty;
        var competition = ResultsCompetition;
        if (competition is null)
        {
            RememberInformationDraft(); StopInformationTracking(); _informationKey = null;
            ResultsJury.Clear(); ResultsRuns.Clear(); ResultsCategory = "";
            ResultsPointsList = ""; ResultsPointsValidity = "";
            ResultsState = "No FIS competition in this series. Check FIS competition and enter a FIS codex in Competitions; existing start lists are preserved.";
            OnPropertyChanged(nameof(ResultsReady));
            return;
        }
        ResultsState = "Loading timing and approved revisions…";
        await GuardAsync(async () =>
        {
            _current = await workspace.ReadAsync();
            if (load != _resultsLoad) { return; }
            var currentCompetition = _current.Competitions.FirstOrDefault(x => x.Id == competition.Id);
            if (currentCompetition is null || currentCompetition.Values != competition.Values)
            {
                ResultsState = "Competition details changed. Reopen Results to review the current race.";
                return;
            }
            if (currentCompetition.Values.RaceType != RaceType.Fis)
            {
                ResultsState = $"{competition.Values.ShortLabel} is a {competition.Values.RaceType} race, not a FIS competition. Select a FIS race in Competitions.";
                return;
            }
            await LoadRaceInformationAsync(competition, load);
            if (load != _resultsLoad) { return; }
            var desk = await workspace.ReadStartListsAsync(competition.Id);
            var first = desk.Revisions.Where(x => x.Plan.RunNumber == 1).OrderByDescending(x => x.Revision).FirstOrDefault();
            if (first is null) { ResultsState = "Draw Run 1 before preparing results."; return; }
            var second = competition.Values.RunCount == 2 ? desk.Revisions.Where(x => x.Plan.RunNumber == 2)
                .OrderByDescending(x => x.Revision).FirstOrDefault() : null;
            if (competition.Values.RunCount == 2 && second is null)
            { ResultsState = "Create the Run 2 start list and complete both runs before preparing results."; return; }
            var firstData = await workspace.ReadTimingAsync(first.Id);
            var secondData = second is null ? null : await workspace.ReadTimingAsync(second.Id);
            var approvals = await workspace.ReadApprovedResultsAsync(competition.Id);
            if (load != _resultsLoad) { return; }
            foreach (var approval in approvals.Reverse()) { ResultApprovals.Add(approval); }
            SelectedResultApproval = ResultApprovals.FirstOrDefault();
            if (firstData.Sessions.Count == 0 || secondData is { Sessions.Count: 0 })
            { ResultsState = "Timing capture is missing for a run. Complete the race before preparing results."; return; }
            ResultsState = "Checking each starter's result. Check the status bar for details.";
            var firstTiming = TimingReplay.Restore(firstData, new Devices.AlgeDecoderFactory());
            var secondTiming = secondData is null ? null : TimingReplay.Restore(secondData, new Devices.AlgeDecoderFactory());
            _resultRace = FisRaceResults.Assemble(firstData.List, firstTiming, secondData?.List, secondTiming);
            _resultFingerprint = ResultSourceFingerprint.Create(firstData, secondData);
            var pointsSource = _resultRace.FirstList.Plan.PointsList;
            EnsureFisListLoaded();
            ResultsPointsList = _fisList?.ListCode == pointsSource.Code ? _fisList.DisplayName : $"{pointsSource.Code}: FIS points list (drawn snapshot)";
            ResultsPointsValidity = $"Effective {pointsSource.ValidFrom:yyyy-MM-dd}–{pointsSource.ValidTo:yyyy-MM-dd}";
            ResultsStatistics = $"Number of competitors {_resultRace.Rows.Count}, number of NSA {_resultRace.Rows.Select(x => x.Entry.Entrant.Athlete.Nation).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}";
            PopulateResultRows();
            var unresolved = firstTiming.Unresolved + (secondTiming?.Unresolved ?? 0);
            ResultsState = $"{_resultRace.Rows.Count(x => x.Status == TimingStatus.Finished)} classified · {_resultRace.Rows.Count(x => x.Status != TimingStatus.Finished)} not classified. Review penalty and race information with the TD."
                + (unresolved > 0 ? $" {unresolved} extra timestamp(s) remain unassigned or need review in Timing; original input is preserved." : "");
            try
            {
                var tables = pointsSource.PenaltyRules;
                if (tables is null && _fisList is { } list && list.ListCode == pointsSource.Code
                    && list.ValidFrom == pointsSource.ValidFrom && list.ValidTo == pointsSource.ValidTo)
                { tables = list.PenaltyRules; }
                if (tables is null)
                { throw new DomainValidationException("Penalty rules are missing from the drawn list. Download that same FIS points list in Competitors and refresh Results."); }
                if (competition.Values.Date < pointsSource.ValidFrom || competition.Values.Date > pointsSource.ValidTo)
                { throw new DomainValidationException("The drawn FIS points list is not valid on race day. Review the draw and points list."); }
                // This calculation algorithm has been reviewed against the 2026/27 edition.
                if (tables.Season != 2027)
                { throw new DomainValidationException("The penalty calculation rules for this season have not been reviewed."); }
                _resultsRuleProfile = tables.Resolve(competition.Values.Calendar?.Category ?? ResultsCategory,
                    competition.Values.Discipline, _resultRace.FirstList.Plan.Gender);
                var profile = _resultsRuleProfile;
                ResultsRuleSource = $"FIS list {pointsSource.Code} · valid {pointsSource.ValidFrom:yyyy-MM-dd}–{pointsSource.ValidTo:yyyy-MM-dd} · {profile.Category} · race level {profile.RaceLevel} · {_resultRace.FirstList.Plan.Gender} · FIS Points Rules 2026/27 §§4.4–4.5, 4.9";
                ResultsRuleValues = string.Create(CultureInfo.InvariantCulture,
                    $"F {profile.FValue}   Points cap {profile.MaximumPoints:0.00}   Correction (Z) {profile.Correction:0.00}   Category adder {profile.Adder:0.00}   Minimum {profile.Minimum:0.00}   Maximum {profile.Maximum:0.00}");
                ReviewPenalty(CalculateCurrentPenalty());
            }
            catch (DomainValidationException ex)
            {
                ResultsRuleSource = "Penalty unavailable: " + ex.Message;
                ResultsState += " " + ex.Message;
            }
        });
        OnPropertyChanged(nameof(ResultsReady));
    }

    private void PopulateResultRows(FisPenaltyResult? penalty = null)
    {
        ResultRows.Clear();
        if (_resultRace is null) { return; }
        foreach (var row in _resultRace.Rows.OrderBy(x => x.Rank is null).ThenBy(x => x.Rank).ThenBy(x => x.Entry.Position))
        {
            var athlete = row.Entry.Entrant.Athlete;
            ResultRows.Add(new(row.Rank?.ToString(CultureInfo.InvariantCulture) ?? "—", row.Entry.Bib,
                athlete.FederationCode ?? "", athlete.Surname + " " + athlete.FirstName,
                TimingTime.Format(row.Run1Hundredths), TimingTime.Format(row.Run2Hundredths),
                TimingTime.Format(row.TotalHundredths), row.Status == TimingStatus.Finished ? "Finished"
                    : row.Status + " " + row.StatusRun.ToString(CultureInfo.InvariantCulture),
                penalty is not null && penalty.RacePoints.TryGetValue(row.Entry.Bib, out var points)
                    ? points.ToString("0.00", CultureInfo.InvariantCulture) : ""));
        }
    }

    [RelayCommand]
    private async Task CalculateResultsPenaltyAsync()
    {
        await GuardAsync(() =>
        {
            var penalty = CalculateCurrentPenalty();
            ReviewPenalty(penalty);
            return Task.CompletedTask;
        });
    }

    private void ReviewPenalty(FisPenaltyResult penalty)
    {
        _reviewedPenalty = penalty;
        PopulateResultRows(penalty);
        ResultBestClassified.Clear(); ResultBestStarted.Clear();
        foreach (var item in penalty.BestClassified) { ResultBestClassified.Add(PenaltyRow(item)); }
        foreach (var item in penalty.BestStarted) { ResultBestStarted.Add(PenaltyRow(item)); }
        ResultTopTen.Clear();
        foreach (var row in _resultRace!.PenaltyCompetitors.Where(x => x.Status == TimingStatus.Finished && x.Rank <= 10).OrderBy(x => x.Rank).ThenBy(x => x.Bib))
        {
            var selected = penalty.BestClassified.FirstOrDefault(x => x.Competitor.Bib == row.Bib);
            var item = new PenaltySelection(row, selected?.UsedPoints ?? 0, penalty.RacePoints[row.Bib], selected?.SubstitutedMaximum ?? false);
            ResultTopTen.Add(PenaltyRow(item) with { Used = selected is null ? "—" : selected.UsedPoints.ToString("0.00", CultureInfo.InvariantCulture),
                PenaltyRacePoints = selected?.RacePoints?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—",
                Note = selected is null ? "" : "Best 5" + (selected.SubstitutedMaximum ? " · list cap" : "")
                    + (selected.RacePoints < penalty.RacePoints[row.Bib] ? " · race cap" : "") });
        }
        ResultsPenaltySummary = string.Create(CultureInfo.InvariantCulture,
            $"A · best 5 in top 10: {penalty.SumA:0.00}    B · best 5 starters: {penalty.SumB:0.00}    C · corresponding capped race points: {penalty.SumC:0.00}\nCalculated penalty (A + B − C) / 10 = {penalty.Calculated:0.00}\nCorrection (Z): {penalty.Rules.Correction:0.00}    Category adder: {penalty.Rules.Adder:0.00}    Minimum: {penalty.Rules.Minimum:0.00}    Maximum: {penalty.Rules.Maximum:0.00}\nApplied penalty: {penalty.Applied:0.00}")
            + (penalty.DoubleMinimum ? string.Create(CultureInfo.InvariantCulture,
                $" · effective minimum {Math.Max(penalty.Rules.Minimum, 2m * penalty.MaximumPoints):0.00} (double points cap)") : "");
        OnPropertyChanged(nameof(ResultsReady));
    }

    private static PenaltyReviewRow PenaltyRow(PenaltySelection item) => new(item.Competitor.Bib,
        item.Competitor.Entry.Entrant.Athlete.Surname + " " + item.Competitor.Entry.Entrant.Athlete.FirstName,
        item.Competitor.ListedPoints?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—",
        item.UsedPoints.ToString("0.00", CultureInfo.InvariantCulture),
        item.RacePoints?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—",
        item.SubstitutedMaximum ? "Maximum substituted" : "", item.Competitor.Rank?.ToString(CultureInfo.InvariantCulture) ?? "—",
        item.Competitor.Entry.Entrant.Athlete.FederationCode ?? "",
        item.Competitor.Entry.Entrant.Athlete.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? "",
        item.Competitor.Entry.Entrant.Athlete.Nation ?? "", item.Competitor.Status.ToString());

    private FisPenaltyResult CalculateCurrentPenalty()
    {
        if (_resultRace is null || ResultsCompetition is null) { throw new DomainValidationException("Complete timing before calculating the penalty."); }
        var profile = _resultsRuleProfile ?? throw new DomainValidationException("Load the matching FIS list rule values before reviewing the penalty.");
        return FisPenalty.Calculate(profile, _resultRace.PenaltyCompetitors);
    }

    [RelayCommand]
    private async Task ApproveFisResultsAsync()
    {
        await GuardAsync(async () =>
        {
            if (_resultRace is null || _resultFingerprint is null || ResultsCompetition is null || _current is null)
            { throw new DomainValidationException("Reload complete race results first."); }
            if (!await FlushRaceInformationAsync()) { throw new DomainValidationException("Resolve unsaved race information before approving results."); }
            var penalty = CalculateCurrentPenalty();
            if (_reviewedPenalty is null || penalty.Rules != _reviewedPenalty.Rules
                || penalty.FValue != _reviewedPenalty.FValue || penalty.MaximumPoints != _reviewedPenalty.MaximumPoints
                || penalty.Calculated != _reviewedPenalty.Calculated || penalty.Applied != _reviewedPenalty.Applied)
            { throw new DomainValidationException("Calculate and review the current penalty with the TD before approving results."); }
            var information = CurrentRaceInformation();
            var td = information.Jury.Single(x => x.Function == "TechnicalDelegate").Person;
            var chief = information.Jury.Single(x => x.Function == "ChiefRace").Person;
            var metadata = new FisXmlDetails(information.Category, td, chief,
                information.Runs.Select(x => new FisRunXmlDetails(x.Gates ?? 0, x.TurningGates ?? 0, x.StartTime, x.CourseSetter)).ToArray(), information);
            var xmlRace = _resultRace with { FirstList = _resultRace.FirstList with
            { Plan = _resultRace.FirstList.Plan with { Competition = ResultsCompetition.Values } } };
            var xml = FisResultXml.Create(xmlRace, _current.Values, penalty, metadata);
            var approvedCompetitionId = ResultsCompetition.Id;
            var approvedPath = workspace.FilePath;
            var expectedRevision = _current.Revision;
            var saved = await workspace.ApproveResultAsync(new(ResultsCompetition.Id, _resultRace.FirstList.Id,
                _resultRace.SecondList?.Id, _resultFingerprint, _current.Revision,
                td.FirstName + " " + td.LastName,
                FisResultXml.FileName(xmlRace, _current.Values), xml, penalty.Calculated, penalty.Applied, information));
            if (workspace.FilePath == approvedPath && _current is not null)
            { _current = _current with { Revision = Math.Max(_current.Revision, expectedRevision + 1) }; }
            if (workspace.FilePath == approvedPath && ResultsCompetition?.Id == approvedCompetitionId)
            { ResultApprovals.Insert(0, saved); SelectedResultApproval = saved; }
            SetStatus($"TD approval saved as immutable revision {saved.Revision}. Export {saved.XmlFileName} when ready.");
        });
    }

    [RelayCommand]
    private async Task ExportApprovedXmlAsync()
    {
        await GuardAsync(async () =>
        {
            if (SelectedResultApproval is not { } approval) { return; }
            var path = await dialogs.ChooseResultXmlExportAsync(approval.XmlFileName);
            if (path is null) { return; }
            await File.WriteAllBytesAsync(path, approval.Xml);
            SetStatus($"Approved XML revision {approval.Revision} exported to {path}.");
        });
    }
}
