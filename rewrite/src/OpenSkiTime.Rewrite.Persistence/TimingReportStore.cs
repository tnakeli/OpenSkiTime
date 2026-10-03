using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed class TimingReportRow
{
    public Guid CompetitionId { get; set; }
    public int Revision { get; set; }
    public string ValuesJson { get; set; } = "";
    public DateTimeOffset SavedAt { get; set; }
    public string Operator { get; set; } = "";
    public string Reason { get; set; } = "";
}
internal sealed class TimingReportImageRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public TimingReportImageRole Role { get; set; }
    public string FileName { get; set; } = "";
    public string MediaType { get; set; } = "";
    public byte[] Bytes { get; set; } = [];
    public string RecognizedText { get; set; } = "";
    public string Engine { get; set; } = "";
    public DateTimeOffset ImportedAt { get; set; }
    public int RunNumber { get; set; }
    public string DeviceLabel { get; set; } = "";
}
internal sealed class ApprovedTimingReportRow
{
    public Guid Id { get; set; }
    public Guid CompetitionId { get; set; }
    public int Revision { get; set; }
    public int DraftRevision { get; set; }
    public string SourceFingerprint { get; set; } = "";
    public DateTimeOffset ApprovedAt { get; set; }
    public string ApprovedBy { get; set; } = "";
    public string XmlFileName { get; set; } = "";
    public byte[] Xml { get; set; } = [];
}
internal sealed class TimingReportSubmissionRow
{
    public long Id { get; set; }
    public Guid ApprovalId { get; set; }
    public Guid Uuid { get; set; }
    public string Status { get; set; } = "";
    public string Response { get; set; } = "";
    public DateTimeOffset At { get; set; }
}
internal sealed partial class SqliteSeriesFileSession : ITimingReportStore
{
    public async Task<IReadOnlyList<SavedTimingReport>> ReadTimingReportHistoryAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
        return (await db.Set<TimingReportRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .OrderBy(x => x.Revision).ToArrayAsync(ct)).Select(Report).ToArray();
    }
    public async Task<TimingReportSubmission?> ReadTimingReportSubmissionAsync(Guid approvalId, CancellationToken ct = default)
    {
        CheckOpen();
        await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
        var row = await db.Set<TimingReportSubmissionRow>().AsNoTracking().Where(x => x.ApprovalId == approvalId).OrderByDescending(x => x.Id).FirstOrDefaultAsync(ct);
        return row is null ? null : new(row.ApprovalId, row.Uuid, row.Status, row.Response, row.At);
    }
    public async Task<long> SaveTimingReportSubmissionAsync(TimingReportSubmission submission, long expectedSeriesRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.ApprovalId == Guid.Empty || submission.Uuid == Guid.Empty || string.IsNullOrWhiteSpace(submission.Status)
            || submission.Response.Length > 1_000_000 || submission.Status.Length > 20_000)
        { throw new DomainValidationException("The timing report submission status is invalid."); }
        var result = await WriteDeskAsync(async (db, series) =>
        {
            if (!await db.Set<ApprovedTimingReportRow>().AnyAsync(x => x.Id == submission.ApprovalId, ct))
            { throw new DomainValidationException("The approved timing report no longer exists."); }
            db.Add(new TimingReportSubmissionRow { ApprovalId = submission.ApprovalId, Uuid = submission.Uuid,
                Status = submission.Status, Response = submission.Response, At = submission.At }); return 0;
        }, expectedSeriesRevision, ct, allowCaptureOwner: true);
        return result.Revision;
    }
    public async Task<SavedTimingReport?> ReadTimingReportAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
        var row = await db.Set<TimingReportRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId).OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
        return row is null ? null : Report(row);
    }
    public Task<long> SaveTimingReportAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct = default)
        => SaveTimingReportCoreAsync(draft, expectedSeriesRevision, operatorName, reason, at, ct);
    public Task<long> ApplyTimingReportImportAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct = default)
        => SaveTimingReportCoreAsync(draft, expectedSeriesRevision, operatorName, reason, at, ct);
    private async Task<long> SaveTimingReportCoreAsync(TimingReportDraft draft, long expectedSeriesRevision, string operatorName,
        string reason, DateTimeOffset at, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(operatorName) || string.IsNullOrWhiteSpace(reason))
        { throw new DomainValidationException("Name the operator and reason for the timing report change."); }
        // Capture one immutable representation before awaiting; callers cannot mutate array contents mid-commit.
        var json = JsonSerializer.Serialize(draft);
        if (json.Length > 2_000_000) { throw new DomainValidationException("Timing report data is too large."); }
        var saved = await WriteDeskAsync(async (db, series) =>
        {
            if (!await db.Competitions.AnyAsync(x => x.Id == draft.CompetitionId, ct)) { throw new DomainValidationException("Choose an existing competition."); }
            var revision = (await db.Set<TimingReportRow>().Where(x => x.CompetitionId == draft.CompetitionId).MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1;
            db.Add(new TimingReportRow { CompetitionId = draft.CompetitionId, Revision = revision, ValuesJson = json,
                SavedAt = at, Operator = operatorName.Trim(), Reason = reason.Trim() });
            return revision;
        }, expectedSeriesRevision, ct, allowCaptureOwner: true);
        return saved.Revision;
    }
    public async Task<IReadOnlyList<TimingReportImage>> ReadTimingReportImagesAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
        return (await db.Set<TimingReportImageRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId).ToArrayAsync(ct))
            .OrderBy(x => x.ImportedAt).ThenBy(x => x.Id).Select(x => new TimingReportImage(x.Id, x.CompetitionId, x.Role,
                x.FileName, x.MediaType, x.Bytes, x.RecognizedText, x.Engine, x.ImportedAt) { RunNumber = x.RunNumber, DeviceLabel = x.DeviceLabel }).ToArray();
    }
    public async Task<long> SaveTimingReportImagesAsync(IReadOnlyList<TimingReportImage> images, long expectedSeriesRevision,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count is < 1 or > 100 || images.Select(x => x.Id).Distinct().Count() != images.Count
            || images.Any(x => x.Id == Guid.Empty || x.Bytes.Length is 0 or > 25_000_000 || !Enum.IsDefined(x.Role)
                || x.RunNumber is < 1 or > 3 || string.IsNullOrWhiteSpace(x.FileName) || x.FileName != Path.GetFileName(x.FileName) || x.RecognizedText.Length > 2_000_000)
            || images.Sum(x => (long)x.Bytes.Length) > 100_000_000)
        { throw new DomainValidationException("Import 1–100 images, at most 25 MB each and 100 MB per batch."); }
        var rows = images.Select(x => new TimingReportImageRow { Id = x.Id, CompetitionId = x.CompetitionId, Role = x.Role,
            FileName = x.FileName, MediaType = x.MediaType, Bytes = x.Bytes.ToArray(), RecognizedText = x.RecognizedText,
            Engine = x.Engine, ImportedAt = x.ImportedAt, RunNumber = x.RunNumber, DeviceLabel = x.DeviceLabel }).ToArray();
        await _captureOwnership.WaitAsync(ct);
        try
        {
            var saved = await WriteDeskAsync(async (db, series) =>
            {
                var ids = rows.Select(x => x.CompetitionId).Distinct().ToArray();
                if (await db.Competitions.CountAsync(x => ids.Contains(x.Id), ct) != ids.Length) { throw new DomainValidationException("An image competition no longer exists."); }
                db.AddRange(rows); return rows.Length;
            }, expectedSeriesRevision, ct, allowCaptureOwner: true);
            return saved.Revision;
        }
        finally { _captureOwnership.Release(); }
    }

    private HashSet<Guid> ActiveReportSessions() => _activeCaptureId is { } id ? [id] : [];

    public async Task<IReadOnlyList<ApprovedTimingReport>> ReadApprovedTimingReportsAsync(Guid competitionId, CancellationToken ct = default)
    {
        CheckOpen();
        await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
        return (await db.Set<ApprovedTimingReportRow>().AsNoTracking().Where(x => x.CompetitionId == competitionId)
            .OrderBy(x => x.Revision).ToArrayAsync(ct)).Select(Approval).ToArray();
    }
    public async Task<ApprovedTimingReport> ApproveTimingReportAsync(ApproveTimingReportRequest request, ITimingDecoderFactory decoders, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(decoders);
        if (string.IsNullOrWhiteSpace(request.ApprovedBy)) { throw new DomainValidationException("Name the approving operator."); }
        await _captureOwnership.WaitAsync(ct);
        try
        {
        var saved = await WriteDeskAsync(async (db, series) =>
        {
            var row = await db.Set<TimingReportRow>().Where(x => x.CompetitionId == request.CompetitionId)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct)
                ?? throw new DomainValidationException("Save the timing report before approving.");
            if (row.Revision != request.DraftRevision) { throw new DomainValidationException("Timing report changed; review the latest draft."); }
            var draft = Report(row).Values;
            var competition = await db.Competitions.SingleAsync(x => x.Id == request.CompetitionId, ct);
            var discipline = competition.Discipline switch { Discipline.Downhill => "DH", Discipline.SuperG => "SG", Discipline.GiantSlalom => "GS", Discipline.Slalom => "SL", _ => "" };
            if (draft.Header.Date != competition.Date || draft.Header.Discipline != discipline || draft.Header.Codex != competition.FisCode)
            { throw new DomainValidationException("Race metadata changed. Refresh the timing report header before approving."); }
            if (draft.Runs.Length != competition.RunCount || !draft.Runs.Select(x => x.Run).Order().SequenceEqual(Enumerable.Range(1, competition.RunCount)))
            { throw new DomainValidationException("Include every competition run before approving."); }
            var runIds = await db.Runs.Where(x => x.CompetitionId == request.CompetitionId).Select(x => x.Id).ToArrayAsync(ct);
            var lists = await db.StartLists.Where(x => runIds.Contains(x.RunId)).ToArrayAsync(ct);
            var latest = lists.GroupBy(x => x.RunId).Select(x => x.MaxBy(y => y.Revision)!).ToArray();
            var sources = new List<TimingReplayData>();
            foreach (var list in latest)
            {
                var data = await ReadTimingAsync(list.Id, ct);
                if ((data.List.Plan.Gender == Gender.Male ? "M" : "W") != draft.Header.Gender) { continue; }
                if (data.Sessions.Count == 0 || data.Sessions.Any(x => (!x.CleanStop || x.StoppedAt is null) && x.Id != _activeCaptureId))
                { throw new DomainValidationException("Resolve interrupted timing capture before approving the timing report."); }
                if (data.Sessions.Any(x => x.Options.Simulation))
                { throw new DomainValidationException("Simulation timing cannot be certified as an official timing report."); }
                sources.Add(data);
            }
            if (sources.Count != competition.RunCount || draft.SourceFingerprint != TimingReportProjection.Fingerprint(sources))
            { throw new DomainValidationException("Timing data changed since report review. Refresh A data and review again."); }
            var xml = TimingReportXml.Create(draft, request.SoftwareVersion);
            await VerifyReportEvidenceAsync(db, draft, sources, decoders, ct);
            var approved = new ApprovedTimingReportRow { Id = Guid.NewGuid(), CompetitionId = request.CompetitionId,
                Revision = (await db.Set<ApprovedTimingReportRow>().Where(x => x.CompetitionId == request.CompetitionId).MaxAsync(x => (int?)x.Revision, ct) ?? 0) + 1,
                DraftRevision = row.Revision, SourceFingerprint = draft.SourceFingerprint, ApprovedAt = request.At,
                ApprovedBy = request.ApprovedBy.Trim(), XmlFileName = TimingReportXml.FileName(draft), Xml = xml };
            db.Add(approved); return Approval(approved);
        }, request.ExpectedSeriesRevision, ct, allowCaptureOwner: true);
        return saved.Value;
        }
        finally { _captureOwnership.Release(); }
    }
    private async Task VerifyReportEvidenceAsync(SeriesDbContext db, TimingReportDraft draft,
        IReadOnlyList<TimingReplayData> sources, ITimingDecoderFactory decoders, CancellationToken ct)
    {
        var images = await db.Set<TimingReportImageRow>().AsNoTracking().Where(x => x.CompetitionId == draft.CompetitionId).ToArrayAsync(ct);
        foreach (var source in sources)
        {
            var snapshot = TimingReplay.Restore(source, decoders, ActiveReportSessions());
            if (!snapshot.Complete || snapshot.Unresolved > 0)
            { throw new DomainValidationException("Resolve all A timing and classifications before approving the report."); }
            var projected = TimingReportProjection.FromTiming(source, snapshot);
            var report = draft.Runs.Single(x => x.Run == projected.Run);
            static bool SameA(TimingReportBib a, TimingReportBib b) => a.Bib == b.Bib && a.AStart == b.AStart
                && a.AFinish == b.AFinish && a.NetHundredths == b.NetHundredths;
            if (!SameA(report.First, projected.First) || !SameA(report.Last, projected.Last)
                || report.BestBib != projected.BestBib || report.BestHundredths != projected.BestHundredths
                || projected.MissedA.Any(m => !report.MissedA.Any(r => r.Bib == m.Bib))
                || report.MissedA.Any(m => !snapshot.Results.Any(r => r.Bib == m.Bib)))
            { throw new DomainValidationException("Report A samples, net times or failure declarations differ from the current timing calculation. Refresh A data."); }
            var auxiliary = await ReadAuxiliaryTimingAsync(source.List.Id, ct);
            var observations = auxiliary.Decode(decoders);
            foreach (var association in draft.Associations.Where(x => x.Run == report.Run))
            {
                if (!snapshot.Results.Any(x => x.Bib == association.Bib)) { throw new DomainValidationException("Report evidence refers to an unknown competitor."); }
                var stamp = association.Stamp;
                if (stamp.SourceReference == "manual") { continue; }
                if (stamp.SourceReference.StartsWith("image:", StringComparison.Ordinal))
                {
                    var image = images.FirstOrDefault(x => stamp.SourceReference.StartsWith($"image:{x.Id:D}:", StringComparison.Ordinal));
                    if (image is null || image.RunNumber != association.Run || image.Role != association.Role)
                    { throw new DomainValidationException("Report image evidence does not belong to this competition, run and source role."); }
                    var parsed = image.RecognizedText.Split('\n').SelectMany((line, i) => TimingEvidenceMatching.ParseLine($"image:{image.Id:D}:{i}", line,
                        draft.Header.Date, image.Role == TimingReportImageRole.B ? null : image.Role == TimingReportImageRole.HandStart ? 0 : 1))
                        .SingleOrDefault(x => x.Key == stamp.SourceReference);
                    if (parsed is null || parsed.Precision != stamp.Precision || parsed.Channel is { } channel && channel != association.Channel
                        || Math.Abs(parsed.Ticks - stamp.Ticks) is not (0 or TimeSpan.TicksPerDay))
                    { throw new DomainValidationException("Report image timestamp differs from its saved source. Enter an audited manual correction instead."); }
                }
                else
                {
                    var observed = observations.SingleOrDefault(x => x.Observation.Key == stamp.SourceReference);
                    var competitor = snapshot.Results.Single(x => x.Bib == association.Bib);
                    var aKey = association.Channel == 0 ? competitor.StartKey : competitor.FinishKey;
                    var aObservation = snapshot.Observations.SingleOrDefault(x => x.Observation.Key == aKey)?.Observation;
                    var normalized = aObservation is null || observed is null ? null
                        : AuxiliaryClockComparison.Normalize(AuxiliaryClockComparison.UsesUtc(aObservation), [observed]).Observations.SingleOrDefault();
                    if (observed is null || (int)observed.Role != (int)association.Role || observed.Observation.Channel != association.Channel
                        || normalized?.DeviceTicks != stamp.Ticks || observed.Observation.Precision != stamp.Precision)
                    { throw new DomainValidationException("Report device timestamp does not match a saved source in this run and role."); }
                    if (auxiliary.Sessions.Single(x => x.Capture.Id == observed.Observation.SessionId).Capture.Options.Simulation)
                    { throw new DomainValidationException("Simulated backup evidence cannot be certified in a timing report."); }
                }
            }
            foreach (var sample in new[] { report.First, report.Last })
            {
                TimingReportStamp? Find(TimingReportImageRole role, int channel) => draft.Associations.SingleOrDefault(x => x.Run == report.Run
                    && x.Bib == sample.Bib && x.Role == role && x.Channel == channel)?.Stamp;
                if (sample.BStart != Find(TimingReportImageRole.B, 0) || sample.BFinish != Find(TimingReportImageRole.B, 1)
                    || sample.HandStart != Find(TimingReportImageRole.HandStart, 0) || sample.HandFinish != Find(TimingReportImageRole.HandFinish, 1))
                { throw new DomainValidationException("Report B/hand samples differ from their reviewed associations."); }
            }
        }
    }
    private static SavedTimingReport Report(TimingReportRow row)
    {
        try { return new(row.CompetitionId, row.Revision, JsonSerializer.Deserialize<TimingReportDraft>(row.ValuesJson)
            ?? throw new SeriesFileException("Timing report is unreadable."), row.SavedAt, row.Operator, row.Reason); }
        catch (JsonException ex) { throw new SeriesFileException("Timing report is unreadable. Restore a backup.", ex); }
    }
    private static ApprovedTimingReport Approval(ApprovedTimingReportRow x) => new(x.Id, x.CompetitionId, x.Revision,
        x.DraftRevision, x.SourceFingerprint, x.ApprovedAt, x.ApprovedBy, x.XmlFileName, x.Xml);
}
