using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

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
    public TimingReportStamp? OriginalBStart { get; set; }
    public TimingReportStamp? OriginalBFinish { get; set; }
    public TimingReportStamp? OriginalHandStart { get; set; }
    public TimingReportStamp? OriginalHandFinish { get; set; }
    [ObservableProperty] private string _bStart = "";
    [ObservableProperty] private string _bFinish = "";
    [ObservableProperty] private string _handStart = "";
    [ObservableProperty] private string _handFinish = "";
    public static string Format(TimingReportStamp? stamp) => stamp is null ? "" : TimingReportXml.FormatStamp(stamp);
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
