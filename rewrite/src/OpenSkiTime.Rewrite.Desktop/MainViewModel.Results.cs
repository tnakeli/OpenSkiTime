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
    string RacePoints, string Note);

public sealed partial class MainViewModel
{
    private FisRaceResult? _resultRace;
    private FisPenaltyResult? _reviewedPenalty;
    private string? _resultFingerprint;
    private int _resultsLoad;
    public ObservableCollection<ResultReviewRow> ResultRows { get; } = [];
    public ObservableCollection<PenaltyReviewRow> ResultBestClassified { get; } = [];
    public ObservableCollection<PenaltyReviewRow> ResultBestStarted { get; } = [];
    public ObservableCollection<ApprovedResult> ResultApprovals { get; } = [];
    [ObservableProperty] private CompetitionDetails? _resultsCompetition;
    [ObservableProperty] private string _resultsState = "Choose a FIS competition.";
    [ObservableProperty] private string _resultsPointsList = string.Empty;
    [ObservableProperty] private string _resultsPenaltySummary = string.Empty;
    [ObservableProperty] private string _resultsMinimum = string.Empty;
    [ObservableProperty] private string _resultsMaximum = string.Empty;
    [ObservableProperty] private string _resultsAdder = string.Empty;
    [ObservableProperty] private string _resultsCategory = string.Empty;
    [ObservableProperty] private ApprovedResult? _selectedResultApproval;
    public bool ResultsHasSecondRun => ResultsCompetition?.Values.RunCount == 2;
    public bool ResultsIsFis => ResultsCompetition?.Values.RaceType == RaceType.Fis;
    public bool ResultsReady => _resultRace is not null && _reviewedPenalty is not null;

    partial void OnResultsMinimumChanged(string value) => InvalidatePenaltyReview();
    partial void OnResultsMaximumChanged(string value) => InvalidatePenaltyReview();
    partial void OnResultsAdderChanged(string value) => InvalidatePenaltyReview();

    private void InvalidatePenaltyReview()
    {
        _reviewedPenalty = null;
        ResultsPenaltySummary = string.Empty;
        ResultBestClassified.Clear(); ResultBestStarted.Clear();
        OnPropertyChanged(nameof(ResultsReady));
    }

    partial void OnResultsCompetitionChanged(CompetitionDetails? value)
    {
        OnPropertyChanged(nameof(ResultsHasSecondRun));
        OnPropertyChanged(nameof(ResultsIsFis));
        OnPropertyChanged(nameof(WindowTitle));
        if (IsResultsSection) { _ = LoadResultsAsync(); }
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
        _reviewedPenalty = null;
        ResultRows.Clear(); ResultBestClassified.Clear(); ResultBestStarted.Clear(); ResultApprovals.Clear();
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
            ResultsPointsList = _fisList?.ListCode == pointsSource.Code ? _fisList.DisplayName : $"{pointsSource.Code}: FIS points list (drawn snapshot)";
            ResultsPointsValidity = $"Effective {pointsSource.ValidFrom:yyyy-MM-dd}–{pointsSource.ValidTo:yyyy-MM-dd}";
            ResultsStatistics = $"Number of competitors {_resultRace.Rows.Count}, number of NSA {_resultRace.Rows.Select(x => x.Entry.Entrant.Athlete.Nation).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} · F = {FisPenalty.FValue(competition.Values.Discipline)} (2026/27 rules)";
            PopulateResultRows();
            var unresolved = firstTiming.Unresolved + (secondTiming?.Unresolved ?? 0);
            ResultsState = $"{_resultRace.Rows.Count(x => x.Status == TimingStatus.Finished)} classified · {_resultRace.Rows.Count(x => x.Status != TimingStatus.Finished)} not classified. Review penalty and race information with the TD."
                + (unresolved > 0 ? $" {unresolved} extra timestamp(s) remain unassigned or need review in Timing; original input is preserved." : "");
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
            _reviewedPenalty = penalty;
            PopulateResultRows(penalty);
            ResultBestClassified.Clear(); ResultBestStarted.Clear();
            foreach (var item in penalty.BestClassified) { ResultBestClassified.Add(PenaltyRow(item)); }
            foreach (var item in penalty.BestStarted) { ResultBestStarted.Add(PenaltyRow(item)); }
            ResultsPenaltySummary = string.Create(CultureInfo.InvariantCulture,
                $"A {penalty.SumA:0.00} + B {penalty.SumB:0.00} − C {penalty.SumC:0.00} = {penalty.Calculated:0.00} · adder {penalty.Rules.Adder:0.00} · applied {penalty.Applied:0.00}")
                + (penalty.DoubleMinimum ? " · double-maximum minimum applies" : "");
            OnPropertyChanged(nameof(ResultsReady));
            return Task.CompletedTask;
        });
    }

    private static PenaltyReviewRow PenaltyRow(PenaltySelection item) => new(item.Competitor.Bib,
        item.Competitor.Entry.Entrant.Athlete.Surname + " " + item.Competitor.Entry.Entrant.Athlete.FirstName,
        item.Competitor.ListedPoints?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—",
        item.UsedPoints.ToString("0.00", CultureInfo.InvariantCulture),
        item.RacePoints?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—",
        item.SubstitutedMaximum ? "Maximum substituted" : "");

    private FisPenaltyResult CalculateCurrentPenalty()
    {
        if (_resultRace is null || ResultsCompetition is null) { throw new DomainValidationException("Complete timing before calculating the penalty."); }
        static decimal Parse(string value, string label) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
            ? parsed : throw new DomainValidationException($"Enter {label} as a decimal number (use a dot).");
        var rules = new PenaltyRuleValues(Parse(ResultsMinimum, "category minimum"), Parse(ResultsMaximum, "category maximum"),
            Parse(ResultsAdder, "category adder"));
        return FisPenalty.Calculate(ResultsCompetition.Values.Discipline, _resultRace.PenaltyCompetitors, rules);
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
