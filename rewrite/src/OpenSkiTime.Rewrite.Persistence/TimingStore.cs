using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Persistence;

internal sealed partial class SqliteSeriesFileSession : ITimingStore
{
    private FileStream? _captureLease;
    private Guid? _activeCaptureId;

    private void RequireIdleCapture()
    {
        using var lease = AcquireIdleWriteLease();
    }

    private FileStream AcquireIdleWriteLease()
    {
        if (_captureLease is not null) { throw new SeriesFileException("Disconnect and drain timing capture before changing registration or competition data."); }
        try
        {
            return new FileStream(FilePath + ".capture.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException ex) { throw new SeriesFileException("Another instance is capturing timing in this event file. Use that instance.", ex); }
    }

    public async Task<TimingReplayData> ReadTimingAsync(Guid listId, CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(transaction, ct);
            var row = await db.StartLists.SingleOrDefaultAsync(x => x.Id == listId, ct)
                ?? throw new DomainValidationException("Draw and save a start list before timing this run.");
            var list = await ReadListAsync(db, row, ct);
            var sessions = (await db.Captures.AsNoTracking().Where(x => x.ListId == listId).ToArrayAsync(ct))
                .OrderBy(x => x.StartedAt).Select(ToCapture).ToArray();
            var ids = sessions.Select(x => x.Id).ToArray();
            var raw = await db.RawPackets.AsNoTracking().Where(x => ids.Contains(x.SessionId)).ToArrayAsync(ct);
            var audit = await db.TimingAudit.AsNoTracking().Where(x => x.ListId == listId).OrderBy(x => x.Id).ToArrayAsync(ct);
            return new(list, sessions, raw.Select(x => new RawTimingPacket(x.SessionId, x.Sequence, x.ReceivedAt,
                x.Protocol, x.Source, x.Stream, x.Bytes)).ToArray(), audit.Select(ToAudit).ToArray());
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        { throw new SeriesFileException("Timing data could not be read. Preserve this file and check storage.", ex); }
    }

    public async Task<CaptureSession> BeginCaptureAsync(Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, CancellationToken ct = default)
        => await BeginCaptureCoreAsync(listId, options, operatorName, at, null, ct);

    public async Task<CaptureSession> SwitchCaptureAsync(Guid previousSessionId, Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, CancellationToken ct = default)
    {
        RequireCaptureOwner(previousSessionId);
        return await BeginCaptureCoreAsync(listId, options, operatorName, at, previousSessionId, ct);
    }

    private async Task<CaptureSession> BeginCaptureCoreAsync(Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, Guid? previousSessionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (string.IsNullOrWhiteSpace(operatorName)) { throw new DomainValidationException("An operator identity is required."); }
        options = options with { Operator = operatorName.Trim() };
        if (previousSessionId is null)
        {
            RequireIdleCapture();
            try { _captureLease = new(FilePath + ".capture.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
            catch (IOException ex) { throw new SeriesFileException("Another instance is capturing this event file.", ex); }
        }
        try
        {
            var capture = await TimingWriteAsync(async db =>
            {
                var listRow = await db.StartLists.SingleOrDefaultAsync(x => x.Id == listId, ct)
                    ?? throw new DomainValidationException("Select a saved start list.");
                if (await db.StartLists.AnyAsync(x => x.RunId == listRow.RunId && x.Revision > listRow.Revision, ct))
                { throw new DomainValidationException("A newer starting order exists. Select the latest start list."); }
                var list = await ReadListAsync(db, listRow, ct);
                var old = await db.Captures.Where(x => x.ListId == listId).ToArrayAsync(ct);
                if (old.Any(x => ToCapture(x).Options.Simulation != options.Simulation))
                { throw new DomainValidationException("Simulation/replay and real timing cannot be mixed in one run. Use a separate test event file."); }
                if (listRow.StartedAt is null)
                {
                    await ValidateStartPlanAsync(db, list.Plan, ct);
                    await ValidateTimingSourceAsync(db, listRow, list.Plan, ct);
                    listRow.StartedAt = at; listRow.StartedBy = operatorName.Trim();
                    var series = await db.Series.SingleAsync(ct); series.Revision++;
                }
                var row = new CaptureRow { Id = Guid.NewGuid(), ListId = listId, OptionsJson = JsonSerializer.Serialize(options), StartedAt = at };
                if (previousSessionId is { } previousId)
                {
                    var previous = await db.Captures.SingleAsync(x => x.Id == previousId, ct);
                    previous.StoppedAt = at; previous.CleanStop = true;
                }
                db.Captures.Add(row);
                return ToCapture(row);
            }, ct);
            _activeCaptureId = capture.Id;
            return capture;
        }
        catch
        {
            if (previousSessionId is null) { _captureLease!.Dispose(); _captureLease = null; }
            throw;
        }
    }

    public Task AppendRawAsync(RawTimingPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        RequireCaptureOwner(packet.SessionId);
        return TimingWriteAsync(async db =>
        {
            var session = await db.Captures.SingleOrDefaultAsync(x => x.Id == packet.SessionId, ct)
                ?? throw new SeriesFileException("Capture session is missing.");
            if (session.CleanStop) { throw new SeriesFileException("This capture session has already ended."); }
            var existing = await db.RawPackets.FindAsync([packet.SessionId, packet.Sequence], ct);
            if (existing is not null)
            {
                if (existing.ReceivedAt != packet.ReceivedAt || existing.Protocol != packet.Protocol || existing.Source != packet.Source
                    || existing.Stream != packet.Stream || !existing.Bytes.AsSpan().SequenceEqual(packet.Bytes))
                { throw new SeriesFileException("A raw timing sequence cannot be replaced."); }
                return true;
            }
            var last = await db.RawPackets.Where(x => x.SessionId == packet.SessionId).MaxAsync(x => (long?)x.Sequence, ct) ?? 0;
            if (packet.Sequence != last + 1) { throw new SeriesFileException("Raw timing packet sequence has a gap."); }
            db.RawPackets.Add(new() { SessionId = packet.SessionId, Sequence = packet.Sequence, ReceivedAt = packet.ReceivedAt,
                Protocol = packet.Protocol, Source = packet.Source, Stream = packet.Stream, Bytes = packet.Bytes.ToArray() });
            return true;
        }, ct);
    }

    public async Task EndCaptureAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default)
    {
        RequireCaptureOwner(sessionId);
        await TimingWriteAsync(async db =>
        {
            var session = await db.Captures.SingleAsync(x => x.Id == sessionId, ct);
            session.StoppedAt = at; session.CleanStop = true;
            return true;
        }, ct);
        _captureLease?.Dispose(); _captureLease = null; _activeCaptureId = null;
    }

    public async Task<TimingAudit> AppendTimingAuditAsync(Guid listId, long expectedVersion, TimingDecision before,
        TimingDecision after, string operatorName, string reason, DateTimeOffset at, long? reversesId = null, CancellationToken ct = default)
        => (await AppendTimingAuditBatchAsync(listId, expectedVersion, [new(before, after, reversesId)], operatorName, reason, at, ct))[0];

    public async Task<IReadOnlyList<TimingAudit>> AppendTimingAuditBatchAsync(Guid listId, long expectedVersion,
        IReadOnlyList<TimingAuditChange> edits, string operatorName, string reason, DateTimeOffset at, CancellationToken ct = default)
    {
        using var idleLease = _captureLease is null ? AcquireIdleWriteLease() : null;
        if (string.IsNullOrWhiteSpace(operatorName) || string.IsNullOrWhiteSpace(reason))
        { throw new DomainValidationException("Enter an operator and a reason for the timing change."); }
        if (edits.Count == 0 || edits.Select(x => TimingEngine.DecisionKey(x.After)).Distinct().Count() != edits.Count)
        { throw new DomainValidationException("A timing operation must change each target at most once."); }
        var rows = await TimingWriteAsync(async db =>
        {
            var version = await db.TimingAudit.Where(x => x.ListId == listId).MaxAsync(x => (long?)x.Id, ct) ?? 0;
            if (version != expectedVersion) { throw new SeriesConflictException(); }
            var changes = (await db.TimingAudit.Where(x => x.ListId == listId).OrderBy(x => x.Id).ToArrayAsync(ct)).Select(ToAudit).ToArray();
            var list = await db.StartLists.SingleAsync(x => x.Id == listId, ct);
            var entries = (await ReadListAsync(db, list, ct)).Plan.Entries;
            var results = new List<TimingAuditRow>();
            foreach (var edit in edits)
            {
                var (before, after, reversesId) = edit;
                if (TimingEngine.DecisionKey(before) != TimingEngine.DecisionKey(after)) { throw new DomainValidationException("The correction targets must match."); }
                if (TimingEngine.CurrentDecision(after, changes) != before) { throw new SeriesConflictException(); }
                if (reversesId is { } undoId && !changes.Any(x => x.Id == undoId && x.After == before && x.Before == after))
                { throw new DomainValidationException("The undo must reverse a recorded change in this run."); }
                TimingEngine.ValidateDecisionShape(after);
                if (after.Kind == DecisionKind.Assignment)
                {
                    var parts = after.ObservationKey!.Split(':');
                    if (!Guid.TryParseExact(parts[0], "N", out var captureId)
                        || !await db.Captures.AnyAsync(x => x.Id == captureId && x.ListId == listId, ct)
                        || (parts[1] != "interrupted" && (!long.TryParse(parts[1], out var seq)
                            || !await db.RawPackets.AnyAsync(x => x.SessionId == captureId && x.Sequence == seq, ct))))
                    { throw new DomainValidationException("The observation must reference original data in this run."); }
                    if (after.Bib is { } bib && !entries.Any(x => x.Bib == bib)) { throw new DomainValidationException("Unknown bib."); }
                }
                else if (!entries.Any(x => x.Entrant.CompetitorId == after.CompetitorId))
                { throw new DomainValidationException("Unknown starter."); }
                var result = new TimingAuditRow { ListId = listId, At = at, Operator = operatorName.Trim(), Reason = reason.Trim(),
                    BeforeJson = JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after), ReversesId = reversesId };
                db.TimingAudit.Add(result);
                results.Add(result);
            }
            return results;
        }, ct);
        return rows.Select(ToAudit).ToArray();
    }

    private void RequireCaptureOwner(Guid sessionId)
    {
        if (_captureLease is null || _activeCaptureId != sessionId)
        { throw new SeriesFileException("Only the active capture owner can append or close this device session."); }
    }

    private async Task<T> TimingWriteAsync<T>(Func<SeriesDbContext, Task<T>> change, CancellationToken ct)
    {
        CheckOpen();
        await _write.WaitAsync(ct);
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await change(db);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException sql) { throw SqliteSeriesFileStore.FileError(sql); }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException) { throw SqliteSeriesFileStore.FileError(ex); }
        finally { _write.Release(); }
    }

    private static CaptureSession ToCapture(CaptureRow row) => new(row.Id, row.ListId,
        JsonSerializer.Deserialize<CaptureOptions>(row.OptionsJson) ?? throw new SeriesFileException("Invalid capture settings."), row.StartedAt, row.StoppedAt, row.CleanStop);
    private static TimingAudit ToAudit(TimingAuditRow row) => new(row.Id, row.ListId, row.At, row.Operator, row.Reason,
        JsonSerializer.Deserialize<TimingDecision>(row.BeforeJson)!, JsonSerializer.Deserialize<TimingDecision>(row.AfterJson)!, row.ReversesId);
}
