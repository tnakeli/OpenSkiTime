namespace OpenSkiTime.Rewrite.Application;

public sealed record TimingReportPerson(string FirstName = "", string LastName = "", string Nation = "",
    string Email = "", string Phone = "", string Number = "", string Company = "");
public sealed record TimingReportDevice(string Brand = "", string Model = "", string Serial = "", string Homologation = "")
{
    public int? ValidUntilSeason { get; init; }
    public DateTimeOffset? HomologationRetrievedAt { get; init; }
}
public sealed record TimingReportDefaults
{
    public TimingReportDevice TimerA { get; init; } = new();
    public TimingReportDevice TimerB { get; init; } = new();
    public TimingReportDevice? TimerStartA { get; init; }
    public TimingReportDevice? TimerStartB { get; init; }
    public TimingReportDevice StartDevice { get; init; } = new();
    public TimingReportDevice StartClock { get; init; } = new();
    public TimingReportDevice FinishCellsA { get; init; } = new();
    public TimingReportDevice FinishCellsB { get; init; } = new();
    public TimingReportPerson ChiefOfTiming { get; init; } = new();
    public TimingReportPerson Timekeeper { get; init; } = new();
    public string ConnectionA { get; init; } = "Cable";
    public string ConnectionB { get; init; } = "Cable";
    public string Voice { get; init; } = "Radio";
}
public sealed record TimingReportHeader(int Season = 0, string Codex = "", string Nation = "", string Discipline = "",
    string Category = "", string Gender = "", string EventName = "", string Place = "", DateOnly Date = default)
{
    public int? TimingLevel { get; init; }
}
public sealed record TimingReportStamp(long Ticks, int Precision, string SourceReference = "", bool Verified = true);
public sealed record TimingReportBib
{
    public int? Bib { get; init; }
    public TimingReportStamp? AStart { get; init; }
    public TimingReportStamp? AFinish { get; init; }
    public TimingReportStamp? BStart { get; init; }
    public TimingReportStamp? BFinish { get; init; }
    public TimingReportStamp? HandStart { get; init; }
    public TimingReportStamp? HandFinish { get; init; }
    public long? NetHundredths { get; init; }
}
public sealed record TimingReportMissed(int Bib, string Reason, string TimeFrom);
public sealed record TimingReportAssociation(int Run, int Bib, int Channel, TimingReportImageRole Role, TimingReportStamp Stamp);
public sealed record TimingReportRun
{
    public int Run { get; init; }
    public TimingReportBib First { get; init; } = new();
    public TimingReportBib Last { get; init; } = new();
    public int? BestBib { get; init; }
    public long? BestHundredths { get; init; }
    public bool AllResultsA { get; init; } = true;
    public TimingReportMissed[] MissedA { get; init; } = [];
    public string Comment { get; init; } = "";
}
public sealed record TimingReportDraft
{
    public Guid CompetitionId { get; init; }
    public TimingReportHeader Header { get; init; } = new();
    public TimingReportPerson TechnicalDelegate { get; init; } = new();
    public TimingReportDefaults Defaults { get; init; } = new();
    public TimingReportStamp? Sync { get; init; }
    public TimingReportStamp? HandSync { get; init; }
    public TimingReportStamp? SyncCheckA { get; init; }
    public TimingReportStamp? SyncCheckB { get; init; }
    public TimingReportStamp? SyncCheckAStart { get; init; }
    public TimingReportStamp? SyncCheckBStart { get; init; }
    public TimingReportRun[] Runs { get; init; } = [];
    public bool CertifyFis { get; init; }
    public bool Reviewed { get; init; }
    public string SourceFingerprint { get; init; } = "";
    public TimingReportAssociation[] Associations { get; init; } = [];
}
public enum TimingReportImageRole { B, HandStart, HandFinish }
public sealed record TimingReportImage(Guid Id, Guid CompetitionId, TimingReportImageRole Role,
    string FileName, string MediaType, byte[] Bytes, string RecognizedText, string Engine, DateTimeOffset ImportedAt)
{
    public int RunNumber { get; init; } = 1;
    public string DeviceLabel { get; init; } = "";
}
public sealed record SavedTimingReport(Guid CompetitionId, int Revision, TimingReportDraft Values,
    DateTimeOffset SavedAt, string Operator, string Reason);
public sealed record ApprovedTimingReport(Guid Id, Guid CompetitionId, int Revision, int DraftRevision,
    string SourceFingerprint, DateTimeOffset ApprovedAt, string ApprovedBy, string XmlFileName, byte[] Xml);
public sealed record ApproveTimingReportRequest(Guid CompetitionId, int DraftRevision, long ExpectedSeriesRevision,
    string ApprovedBy, DateTimeOffset At, string SoftwareVersion);
public sealed record TimingReportSubmission(Guid ApprovalId, Guid Uuid, string Status, string Response, DateTimeOffset At);
public interface ITimingReportStore
{
    Task<SavedTimingReport?> ReadTimingReportAsync(Guid competitionId, CancellationToken ct = default);
    Task<IReadOnlyList<SavedTimingReport>> ReadTimingReportHistoryAsync(Guid competitionId, CancellationToken ct = default);
    Task<long> SaveTimingReportAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct = default);
    Task<long> ApplyTimingReportImportAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct = default);
    Task<IReadOnlyList<TimingReportImage>> ReadTimingReportImagesAsync(Guid competitionId, CancellationToken ct = default);
    Task<long> SaveTimingReportImagesAsync(IReadOnlyList<TimingReportImage> images, long expectedSeriesRevision,
        CancellationToken ct = default);
    Task<IReadOnlyList<ApprovedTimingReport>> ReadApprovedTimingReportsAsync(Guid competitionId, CancellationToken ct = default);
    Task<ApprovedTimingReport> ApproveTimingReportAsync(ApproveTimingReportRequest request, ITimingDecoderFactory decoders, CancellationToken ct = default);
    Task<TimingReportSubmission?> ReadTimingReportSubmissionAsync(Guid approvalId, CancellationToken ct = default);
    Task<long> SaveTimingReportSubmissionAsync(TimingReportSubmission submission, long expectedSeriesRevision, CancellationToken ct = default);
}
