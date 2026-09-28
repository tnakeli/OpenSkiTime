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
    [ObservableProperty] private string _resultsTdFirstName = string.Empty;
    [ObservableProperty] private string _resultsTdLastName = string.Empty;
    [ObservableProperty] private string _resultsTdNation = string.Empty;
    [ObservableProperty] private string _resultsChiefFirstName = string.Empty;
    [ObservableProperty] private string _resultsChiefLastName = string.Empty;
    [ObservableProperty] private string _resultsChiefNation = string.Empty;
    [ObservableProperty] private string _resultsSetter1FirstName = string.Empty;
    [ObservableProperty] private string _resultsSetter1LastName = string.Empty;
    [ObservableProperty] private string _resultsSetter1Nation = string.Empty;
    [ObservableProperty] private string _resultsRun1Gates = string.Empty;
    [ObservableProperty] private string _resultsRun1Turns = string.Empty;
    [ObservableProperty] private string _resultsRun1Start = string.Empty;
    [ObservableProperty] private string _resultsSetter2FirstName = string.Empty;
    [ObservableProperty] private string _resultsSetter2LastName = string.Empty;
    [ObservableProperty] private string _resultsSetter2Nation = string.Empty;
    [ObservableProperty] private string _resultsRun2Gates = string.Empty;
    [ObservableProperty] private string _resultsRun2Turns = string.Empty;
    [ObservableProperty] private string _resultsRun2Start = string.Empty;
    [ObservableProperty] private ApprovedResult? _selectedResultApproval;
    public bool ResultsHasSecondRun => ResultsCompetition?.Values.RunCount == 2;
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
        OnPropertyChanged(nameof(WindowTitle));
        if (IsResultsSection) { _ = LoadResultsAsync(); }
    }

    [RelayCommand]
    private async Task LoadResultsAsync()
    {
        var load = ++_resultsLoad;
        _resultRace = null; _resultFingerprint = null;
        _reviewedPenalty = null;
        ResultRows.Clear(); ResultBestClassified.Clear(); ResultBestStarted.Clear(); ResultApprovals.Clear();
        ResultsPenaltySummary = string.Empty;
        var competition = ResultsCompetition;
        if (competition is null)
        {
            ResultsState = "No FIS competition in this series. Create one in Competitions with Race type Fis and a FIS code before drawing its start list.";
            OnPropertyChanged(nameof(ResultsReady));
            return;
        }
        ResultsState = "Loading timing and approved revisions…";
        await GuardAsync(async () =>
        {
            _current = await workspace.ReadAsync();
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
            ResultsPointsList = $"FIS list {_resultRace.FirstList.Plan.PointsList.Code} · valid {_resultRace.FirstList.Plan.PointsList.ValidFrom:yyyy-MM-dd} – {_resultRace.FirstList.Plan.PointsList.ValidTo:yyyy-MM-dd}";
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
            var penalty = CalculateCurrentPenalty();
            if (_reviewedPenalty is null || penalty.Rules != _reviewedPenalty.Rules
                || penalty.Calculated != _reviewedPenalty.Calculated || penalty.Applied != _reviewedPenalty.Applied)
            { throw new DomainValidationException("Calculate and review the current penalty with the TD before approving results."); }
            var metadata = new FisXmlDetails(ResultsCategory,
                new(ResultsTdFirstName, ResultsTdLastName, ResultsTdNation),
                new(ResultsChiefFirstName, ResultsChiefLastName, ResultsChiefNation),
                ResultsHasSecondRun
                    ? [RunDetails(ResultsRun1Gates, ResultsRun1Turns, ResultsRun1Start,
                        ResultsSetter1FirstName, ResultsSetter1LastName, ResultsSetter1Nation),
                       RunDetails(ResultsRun2Gates, ResultsRun2Turns, ResultsRun2Start,
                        ResultsSetter2FirstName, ResultsSetter2LastName, ResultsSetter2Nation)]
                    : [RunDetails(ResultsRun1Gates, ResultsRun1Turns, ResultsRun1Start,
                        ResultsSetter1FirstName, ResultsSetter1LastName, ResultsSetter1Nation)]);
            var xmlRace = _resultRace with { FirstList = _resultRace.FirstList with
            { Plan = _resultRace.FirstList.Plan with { Competition = ResultsCompetition.Values } } };
            var xml = FisResultXml.Create(xmlRace, _current.Values, penalty, metadata);
            var saved = await workspace.ApproveResultAsync(new(ResultsCompetition.Id, _resultRace.FirstList.Id,
                _resultRace.SecondList?.Id, _resultFingerprint, _current.Revision,
                ResultsTdFirstName.Trim() + " " + ResultsTdLastName.Trim(),
                FisResultXml.FileName(xmlRace, _current.Values), xml, penalty.Calculated, penalty.Applied));
            _current = _current with { Revision = _current.Revision + 1 };
            ResultApprovals.Insert(0, saved); SelectedResultApproval = saved;
            SetStatus($"TD approval saved as immutable revision {saved.Revision}. Export {saved.XmlFileName} when ready.");
        });
    }

    private static FisRunXmlDetails RunDetails(string gates, string turns, string start,
        string first, string last, string nation)
        => new(int.TryParse(gates, CultureInfo.InvariantCulture, out var g) ? g : 0,
            int.TryParse(turns, CultureInfo.InvariantCulture, out var t) ? t : 0, start,
            new(first, last, nation));

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
