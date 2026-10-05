using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Persistence;

internal sealed partial class SqliteSeriesFileSession : ITimingStore
{
    private FileStream? _captureLease;
    // Replaced atomically under _captureOwnership; device writer threads read it without locking.
    private volatile ImmutableHashSet<Guid> _activeCaptureIds = [];
    private readonly SemaphoreSlim _captureOwnership = new(1, 1);
    // A run change keeps its pre-allocated next sessions until the caller has them. A retry after an uncertain commit
    // (saved, then a failure reported) returns that exact boundary instead of stranding the capture.
    private sealed record CaptureSwitch(Guid ListId, Guid[] Next, DateTimeOffset At);
    private readonly Dictionary<string, CaptureSwitch> _captureSwitches = new(StringComparer.Ordinal);

    // One process owns the file lease, while A and independent auxiliary sessions own their own lifetimes.
    // FileShare.None: on Unix .NET maps only None to an exclusive flock (anything else is a shared lock that
    // never conflicts with the shared idle-write check below). On Windows it matches the former FileShare.Read,
    // because every opener of the lock file requests write access.
    private void EnsureCaptureLease()
    {
        if (_captureLease is not null) { return; }
        try { _captureLease = new(FilePath + ".capture.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new SeriesFileException("Another instance is capturing this event file.", ex); }
    }

    private void ReleaseCaptureLeaseIfIdle()
    {
        if (_activeCaptureIds.Count != 0 || !_auxiliaryCaptureIds.IsEmpty) { return; }
        _captureLease?.Dispose(); _captureLease = null;
    }

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
            // The draw preserves its original competition snapshot. Timing positions are
            // operational configuration and may be edited after the draw, so use the
            // current count when replaying or continuing this run.
            var currentIntermediateCount = await db.Competitions.AsNoTracking()
                .Where(x => x.Id == list.Plan.CompetitionId).Select(x => x.IntermediateCount).SingleAsync(ct);
            list = list with { Plan = list.Plan with
            { Competition = list.Plan.Competition with { IntermediateCount = currentIntermediateCount } } };
            var sessions = (await db.Captures.AsNoTracking().Where(x => x.ListId == listId).ToArrayAsync(ct))
                .OrderBy(x => x.StartedAt).ThenBy(x => x.Id).Select(ToCapture).ToArray();
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
        => (await BeginCaptureCoreAsync(listId, [options], operatorName, at, [], ct))[0];

    public async Task<CaptureSession> SwitchCaptureAsync(Guid previousSessionId, Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, CancellationToken ct = default)
    {
        RequireCaptureOwner(previousSessionId);
        return (await BeginCaptureCoreAsync(listId, [options], operatorName, at, [previousSessionId], ct))[0];
    }

    // Several device connections of one A capture begin, and later switch runs, in one transaction: either every
    // session exists in the run or none does.
    public Task<IReadOnlyList<CaptureSession>> BeginCaptureGroupAsync(Guid listId, IReadOnlyList<CaptureOptions> options,
        string operatorName, DateTimeOffset at, CancellationToken ct = default)
        => BeginCaptureCoreAsync(listId, options, operatorName, at, [], ct);

    public Task<IReadOnlyList<CaptureSession>> SwitchCaptureGroupAsync(IReadOnlyList<Guid> previousSessionIds, Guid listId,
        IReadOnlyList<CaptureOptions> options, string operatorName, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(previousSessionIds);
        return BeginCaptureCoreAsync(listId, options, operatorName, at, previousSessionIds, ct);
    }

    private async Task<IReadOnlyList<CaptureSession>> BeginCaptureCoreAsync(Guid listId, IReadOnlyList<CaptureOptions> requested, string operatorName,
        DateTimeOffset at, IReadOnlyList<Guid> previousSessionIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (requested.Count == 0) { throw new DomainValidationException("Choose at least one timing device."); }
        foreach (var item in requested) { ArgumentNullException.ThrowIfNull(item); item.Validate(); }
        if (requested.Select(x => x.Simulation).Distinct().Count() > 1)
        { throw new DomainValidationException("Simulation/replay and real timing cannot be mixed in one run. Use a separate test event file."); }
        if (requested.Count > 1 && (requested.Any(x => x.ClockGroup is null) || requested.Select(x => x.ClockGroup).Distinct().Count() != 1))
        { throw new DomainValidationException("Several timing devices must share one synchronized clock group."); }
        if (string.IsNullOrWhiteSpace(operatorName)) { throw new DomainValidationException("An operator identity is required."); }
        var options = requested.Select(x => x with { Operator = operatorName.Trim() }).ToArray();
        await _captureOwnership.WaitAsync(ct);
        var switchKey = string.Join(",", previousSessionIds.Order());
        var newAttempt = false;
        try
        {
            if (previousSessionIds.Count == 0 && _activeCaptureIds.Count != 0)
            { throw new SeriesFileException("Disconnect the active A source before connecting another."); }
            CaptureSwitch? attempt = null;
            if (previousSessionIds.Count > 0 && !_captureSwitches.TryGetValue(switchKey, out attempt))
            {
                foreach (var previousOwner in previousSessionIds) { RequireCaptureOwner(previousOwner); }
                attempt = new(listId, options.Select(_ => Guid.NewGuid()).ToArray(), at);
                _captureSwitches.Add(switchKey, attempt); newAttempt = true;
            }
            else if (attempt is not null)
            {
                if (attempt.ListId != listId || attempt.Next.Length != options.Length)
                { throw new DomainValidationException("Resolve the pending run change before choosing another run."); }
                if (!previousSessionIds.All(_activeCaptureIds.Contains) && !attempt.Next.All(_activeCaptureIds.Contains))
                { throw new SeriesFileException("Only the active capture owner can change the run of these device sessions."); }
            }
            EnsureCaptureLease();
            var captures = await TimingWriteAsync(async db =>
            {
                var listRow = await db.StartLists.SingleOrDefaultAsync(x => x.Id == listId, ct)
                    ?? throw new DomainValidationException("Select a saved start list.");
                if (await db.StartLists.AnyAsync(x => x.RunId == listRow.RunId && x.Revision > listRow.Revision, ct))
                { throw new DomainValidationException("A newer starting order exists. Select the latest start list."); }
                var list = await ReadListAsync(db, listRow, ct);
                if (attempt is not null && previousSessionIds.Count > 0)
                {
                    var previousRows = await db.Captures.Where(x => previousSessionIds.Contains(x.Id)).ToArrayAsync(ct);
                    if (previousRows.Length == previousSessionIds.Count && previousRows.All(x => x.CleanStop))
                    {
                        // The run change committed before its result reached the caller: return that exact boundary.
                        var committed = await db.Captures.Where(x => attempt.Next.Contains(x.Id)).ToArrayAsync(ct);
                        if (committed.Length != attempt.Next.Length || committed.Any(x => x.ListId != listId || x.CleanStop))
                        { throw new SeriesFileException("The previous run ended without the expected next sessions. Preserve the series for review."); }
                        return attempt.Next.Select(id => ToCapture(committed.Single(x => x.Id == id))).ToArray();
                    }
                }
                var old = await db.Captures.Where(x => x.ListId == listId).ToArrayAsync(ct);
                if (old.Any(x => ToCapture(x).Options.Simulation != options[0].Simulation))
                { throw new DomainValidationException("Simulation/replay and real timing cannot be mixed in one run. Use a separate test event file."); }
                // Validate a list before its first capture only. Once capture has begun the list can no longer be redrawn,
                // so refusing a reconnect (for example after a later Run 1 correction) would strand the run; the start
                // list view still reports a changed source.
                if (listRow.StartedAt is null && old.Length == 0)
                {
                    await ValidateStartPlanAsync(db, list.Plan, ct);
                    await ValidateTimingSourceAsync(db, listRow, list.Plan, ct);
                }
                var startedAt = attempt?.At ?? at;
                foreach (var previousId in previousSessionIds)
                {
                    var previous = await db.Captures.SingleAsync(x => x.Id == previousId, ct);
                    previous.StoppedAt = startedAt; previous.CleanStop = true;
                }
                var rows = options.Select((x, i) => new CaptureRow { Id = attempt?.Next[i] ?? Guid.NewGuid(), ListId = listId,
                    OptionsJson = JsonSerializer.Serialize(x), StartedAt = startedAt }).ToArray();
                db.Captures.AddRange(rows);
                return rows.Select(ToCapture).ToArray();
            }, ct);
            _activeCaptureIds = _activeCaptureIds.Except(previousSessionIds).Union(captures.Select(x => x.Id));
            return captures;
        }
        catch (DomainValidationException)
        {
            // Nothing was committed: a corrected request may choose another run.
            if (newAttempt) { _captureSwitches.Remove(switchKey); }
            ReleaseCaptureLeaseIfIdle();
            throw;
        }
        catch
        {
            ReleaseCaptureLeaseIfIdle();
            throw;
        }
        finally { _captureOwnership.Release(); }
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
        await _captureOwnership.WaitAsync(ct);
        try
        {
            RequireCaptureOwner(sessionId);
            await TimingWriteAsync(async db =>
            {
                var session = await db.Captures.SingleAsync(x => x.Id == sessionId, ct);
                session.StoppedAt = at; session.CleanStop = true;
                return true;
            }, ct);
            _activeCaptureIds = _activeCaptureIds.Remove(sessionId);
            ReleaseCaptureLeaseIfIdle();
        }
        finally { _captureOwnership.Release(); }
    }

    public async Task<TimingAudit> AppendTimingAuditAsync(Guid listId, long expectedVersion, TimingDecision before,
        TimingDecision after, string operatorName, string reason, DateTimeOffset at, long? reversesId = null,
        bool startsRun = false, CancellationToken ct = default)
        => (await AppendTimingAuditBatchAsync(listId, expectedVersion, [new(before, after, reversesId)], operatorName,
            reason, at, startsRun, ct))[0];

    public async Task<IReadOnlyList<TimingAudit>> AppendTimingAuditBatchAsync(Guid listId, long expectedVersion,
        IReadOnlyList<TimingAuditChange> edits, string operatorName, string reason, DateTimeOffset at,
        bool startsRun = false, CancellationToken ct = default)
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
                else if (after.Kind == DecisionKind.StartOrder)
                {
                    if (after.StartOrder is { } order && !TimingEngine.ParseStartOrder(order).Order()
                        .SequenceEqual(entries.Select(x => x.Bib).Order()))
                    { throw new DomainValidationException("The start order must include every starter exactly once."); }
                }
                else if (!entries.Any(x => x.Entrant.CompetitorId == after.CompetitorId))
                { throw new DomainValidationException("Unknown starter."); }
                var result = new TimingAuditRow { ListId = listId, At = at, Operator = operatorName.Trim(), Reason = reason.Trim(),
                    BeforeJson = JsonSerializer.Serialize(before), AfterJson = JsonSerializer.Serialize(after), ReversesId = reversesId };
                db.TimingAudit.Add(result);
                results.Add(result);
            }
            if (startsRun && list.StartedAt is null)
            {
                list.StartedAt = at;
                list.StartedBy = operatorName.Trim();
                var series = await db.Series.SingleAsync(ct);
                series.Revision++;
            }
            return results;
        }, ct);
        return rows.Select(ToAudit).ToArray();
    }

    private void RequireCaptureOwner(Guid sessionId)
    {
        if (_captureLease is null || !_activeCaptureIds.Contains(sessionId))
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
