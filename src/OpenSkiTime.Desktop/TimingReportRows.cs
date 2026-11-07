using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public sealed partial class TimingReportEvidenceRow : ObservableObject
{
    public int Run { get; init; }
    public int Bib { get; init; }
    public string Name { get; init; } = "";
    public string Sample { get; init; } = "";
    public TimingReportStamp? AStartStamp { get; init; }
    public TimingReportStamp? AFinishStamp { get; init; }
    public string AStart => Format(AStartStamp);
    public string AFinish => Format(AFinishStamp);
    public string Net { get; init; } = "";
    public long? NetHundredths { get; init; }
    public TimingReportStamp? OriginalBStart { get; set; }
    public TimingReportStamp? OriginalBFinish { get; set; }
    public TimingReportStamp? OriginalHandStart { get; set; }
    public TimingReportStamp? OriginalHandFinish { get; set; }
    [ObservableProperty] private string _bStart = "";
    [ObservableProperty] private string _bFinish = "";
    [ObservableProperty] private string _handStart = "";
    [ObservableProperty] private string _handFinish = "";
    public static string Format(TimingReportStamp? stamp) => stamp is null ? "" : TimingReportXml.FormatStamp(stamp);

    // What the typed or read time says against System A: shown on the cell as it is typed. Unreadable text is not judged here;
    // saving reports it.
    public EvidenceFinding? BStartIssue => Judge(BStart, OriginalBStart, AStartStamp, false);
    public EvidenceFinding? BFinishIssue => Worst(Judge(BFinish, OriginalBFinish, AFinishStamp, false), JudgeNet(BStart, OriginalBStart, BFinish, OriginalBFinish, false));
    public EvidenceFinding? HandStartIssue => Judge(HandStart, OriginalHandStart, AStartStamp, true);
    public EvidenceFinding? HandFinishIssue => Worst(Judge(HandFinish, OriginalHandFinish, AFinishStamp, true), JudgeNet(HandStart, OriginalHandStart, HandFinish, OriginalHandFinish, true));
    public IEnumerable<(string Cell, EvidenceFinding Finding)> Issues()
    {
        foreach (var (cell, finding) in new[] { ("B start", BStartIssue), ("B finish", BFinishIssue), ("hand start", HandStartIssue), ("hand finish", HandFinishIssue) })
        { if (finding is not null) { yield return (cell, finding); } }
    }
    partial void OnBStartChanged(string value) => NotifyIssues();
    partial void OnBFinishChanged(string value) => NotifyIssues();
    partial void OnHandStartChanged(string value) => NotifyIssues();
    partial void OnHandFinishChanged(string value) => NotifyIssues();
    private void NotifyIssues()
    {
        OnPropertyChanged(nameof(BStartIssue)); OnPropertyChanged(nameof(BFinishIssue));
        OnPropertyChanged(nameof(HandStartIssue)); OnPropertyChanged(nameof(HandFinishIssue));
    }
    private static EvidenceFinding? Worst(EvidenceFinding? a, EvidenceFinding? b)
        => a is null ? b : b is null ? a : b.Severity > a.Severity ? b : a;
    private TimingReportStamp? Parse(string text, TimingReportStamp? original, TimingReportStamp? reference)
    {
        try { return Read(text, original, reference is null ? default : DateOnly.FromDateTime(new(reference.Ticks))); }
        catch (DomainValidationException) { return null; }
    }
    private EvidenceFinding? Judge(string text, TimingReportStamp? original, TimingReportStamp? a, bool hand)
        => TimingEvidenceCheck.Compare(a, a is null ? null : Parse(text, original, a), hand);
    private EvidenceFinding? JudgeNet(string startText, TimingReportStamp? startOriginal, string finishText, TimingReportStamp? finishOriginal, bool hand)
        => TimingEvidenceCheck.CompareNet(NetHundredths, Parse(startText, startOriginal, AStartStamp), Parse(finishText, finishOriginal, AFinishStamp), hand);
    public TimingReportStamp? Read(string text, TimingReportStamp? original, DateOnly date)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text == Format(original)) { return original; }
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        var input = text.Trim(); var wholeSeconds = input.Length == 8;
        if (!TimingTime.TryTimeOfDay(wholeSeconds ? input + ".0" : input, out var ticks, out var precision))
        { throw new DomainValidationException($"Run {Run}, bib {Bib}: use HH:mm:ss with 1-7 decimal places."); }
        // Manual entry remains on the date of the corresponding source observation, including midnight runs.
        return new((original is null ? date.ToDateTime(TimeOnly.MinValue).Ticks
            : original.Ticks / TimeSpan.TicksPerDay * TimeSpan.TicksPerDay) + ticks, wholeSeconds ? 0 : precision, "manual");
    }
}

public sealed partial class TimingReportRunEditor : ObservableObject
{
    public TimingReportRun Source { get; init; } = new();
    public int Run => Source.Run;
    [ObservableProperty] private TimingReportEvidenceRow? _first;
    [ObservableProperty] private TimingReportEvidenceRow? _last;
    public string BestUnavailableReason { get; init; } = "no eligible result";
    public string BestExplanation { get; init; } = "Fastest classified result, including the active audited replacement time.";
    public string Best => Source.BestBib is { } bib ? $"Best A: bib {bib} / {TimingTime.Format(Source.BestHundredths)}" : $"Best A: {BestUnavailableReason}";
    public string BestBib => Source.BestBib?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string BestTime => Source.BestBib is not null ? TimingTime.Format(Source.BestHundredths) : BestUnavailableReason;

    public static TimingReportRunEditor FromTiming(TimingReportRun run, TimingSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(run); ArgumentNullException.ThrowIfNull(snapshot);
        return new()
        {
            Source = run, Comment = run.Comment, AllResultsA = run.AllResultsA,
            BestUnavailableReason = "no classified finisher",
            BestExplanation = run.BestBib is not null
                ? "Fastest classified result, including manual impulses and the active audited replacement time."
                : "No competitor has a classified finished result with a time in this run."
        };
    }
    [ObservableProperty] private string _comment = "";
    [ObservableProperty] private bool _allResultsA = true;

    // The implausible B and hand times of the two sampled finishers, summarised under the run.
    public string EvidenceIssues => string.Join(Environment.NewLine, new[] { First, Last }.Where(x => x is not null).SelectMany(row => row!.Issues()
        .Select(i => $"Bib {row.Bib} {i.Cell}: {i.Finding.Message}")));
    public bool HasEvidenceIssues => EvidenceIssues.Length > 0;
    public bool HasEvidenceError => new[] { First, Last }.Any(row => row is not null && row.Issues().Any(i => i.Finding.Severity == EvidenceSeverity.Error));
    public bool HasEvidenceWarningOnly => HasEvidenceIssues && !HasEvidenceError;
    partial void OnFirstChanged(TimingReportEvidenceRow? oldValue, TimingReportEvidenceRow? newValue) => Track(oldValue, newValue);
    partial void OnLastChanged(TimingReportEvidenceRow? oldValue, TimingReportEvidenceRow? newValue) => Track(oldValue, newValue);
    private void Track(TimingReportEvidenceRow? oldRow, TimingReportEvidenceRow? newRow)
    {
        if (oldRow is not null) { oldRow.PropertyChanged -= RowChanged; }
        if (newRow is not null) { newRow.PropertyChanged += RowChanged; }
        NotifyIssues();
    }
    private void RowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    { if (args.PropertyName is { } name && name.EndsWith("Issue", StringComparison.Ordinal)) { NotifyIssues(); } }
    private void NotifyIssues()
    {
        OnPropertyChanged(nameof(EvidenceIssues)); OnPropertyChanged(nameof(HasEvidenceIssues));
        OnPropertyChanged(nameof(HasEvidenceError)); OnPropertyChanged(nameof(HasEvidenceWarningOnly));
    }
}

public sealed partial class TimingReportMissedEditor : ObservableObject
{
    [ObservableProperty] private int _run;
    [ObservableProperty] private int _bib;
    [ObservableProperty] private IReadOnlyList<int> _runs = [];
    public IReadOnlyList<string> Reasons { get; } = ["Batteries", "Snow obscuration", "Wire break", "Photocell alignment", "Other"];
    public IReadOnlyList<string> Sources { get; } = ["System B", "Manual"];
    [ObservableProperty] private string _reason = "";
    [ObservableProperty] private string _timeFrom = "";
}

public sealed partial class TimingReportImportRow : ObservableObject
{
    public TimingReportEvidenceRow Target { get; init; } = null!;
    public int Run => Target.Run;
    public int Bib => Target.Bib;
    public string Sample => Target.Sample.Trim();
    public int Channel { get; init; }
    public string Position => Channel == 0 ? "Start" : "Finish";
    public TimingReportImageRole Role { get; init; }
    public TimingReportStamp? Stamp { get; init; }
    public string A => Channel == 0 ? Target.AStart : Target.AFinish;
    public string Proposed => TimingReportEvidenceRow.Format(Stamp);
    public string Current => Role == TimingReportImageRole.B ? Channel == 0 ? Target.BStart : Target.BFinish
        : Role == TimingReportImageRole.HandStart ? Target.HandStart : Target.HandFinish;
    public string Difference { get; init; } = "";
    public string State { get; init; } = "";
    public string SourceText { get; init; } = "";
    public Guid? ImageId { get; init; }
    public bool CanAccept => Stamp is not null;
    [ObservableProperty] private bool _accept;
}
