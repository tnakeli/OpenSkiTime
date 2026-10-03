using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Persistence;

internal sealed partial class SqliteSeriesFileSession : IAuxiliaryTimingStore
{
    private readonly ConcurrentDictionary<Guid, AuxiliaryTimingRole> _auxiliaryCaptureIds = new();
    private readonly HashSet<Guid> _closedAuxiliaryIds = [];
    private sealed record AuxiliarySwitch(Guid NextSessionId, Guid ListId, DateTimeOffset At);
    private readonly Dictionary<Guid, AuxiliarySwitch> _auxiliarySwitches = [];

    public async Task<AuxiliaryTimingData> ReadAuxiliaryTimingAsync(Guid listId, CancellationToken ct = default)
    {
        CheckOpen();
        try
        {
            await using var db = await SqliteSeriesFileStore.NewContextAsync(FilePath, SqliteOpenMode.ReadWrite, ct);
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await db.Database.UseTransactionAsync(transaction, ct);
            if (!await db.StartLists.AnyAsync(x => x.Id == listId, ct))
            { throw new DomainValidationException("Select a saved start list for auxiliary evidence."); }
            var sessions = (await db.AuxiliaryCaptures.AsNoTracking().Where(x => x.ListId == listId).ToArrayAsync(ct))
                .OrderBy(x => x.StartedAt).ThenBy(x => x.Id).Select(ToAuxiliaryCapture).ToArray();
            var ids = sessions.Select(x => x.Capture.Id).ToArray();
            var packets = await db.AuxiliaryRawPackets.AsNoTracking().Where(x => ids.Contains(x.SessionId)).ToArrayAsync(ct);
            return new(listId, sessions, packets.Select(x => new RawTimingPacket(x.SessionId, x.Sequence,
                x.ReceivedAt, x.Protocol, x.Source, x.Stream, x.Bytes)).ToArray());
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        { throw new SeriesFileException("Auxiliary evidence could not be read. Preserve this file and check storage.", ex); }
    }

    public async Task<AuxiliaryCaptureSession> BeginAuxiliaryCaptureAsync(Guid listId, AuxiliaryTimingRole role,
        CaptureOptions options, string operatorName, DateTimeOffset at, bool live, CancellationToken ct = default)
    {
        AuxiliaryTimingValidation.Validate(role, options);
        if (string.IsNullOrWhiteSpace(operatorName)) { throw new DomainValidationException("An operator identity is required."); }
        options = options with { Operator = operatorName.Trim() };
        await _captureOwnership.WaitAsync(ct);
        try
        {
            if (_auxiliaryCaptureIds.Values.Contains(role))
            { throw new DomainValidationException("An auxiliary source is already connected for this role."); }
            EnsureCaptureLease();
            var result = await TimingWriteAsync(async db =>
            {
                var list = await db.StartLists.SingleOrDefaultAsync(x => x.Id == listId, ct)
                    ?? throw new DomainValidationException("Select a saved start list for auxiliary evidence.");
                if (await db.StartLists.AnyAsync(x => x.RunId == list.RunId && x.Revision > list.Revision, ct))
                { throw new DomainValidationException("Choose the latest start-list revision for auxiliary capture."); }
                var row = new AuxiliaryCaptureRow { Id = Guid.NewGuid(), ListId = listId, Role = role, Live = live,
                    OptionsJson = JsonSerializer.Serialize(options), StartedAt = at };
                db.AuxiliaryCaptures.Add(row);
                return ToAuxiliaryCapture(row);
            }, ct);
            _auxiliaryCaptureIds.TryAdd(result.Capture.Id, role);
            return result;
        }
        catch { ReleaseCaptureLeaseIfIdle(); throw; }
        finally { _captureOwnership.Release(); }
    }

    public Task AppendAuxiliaryRawAsync(RawTimingPacket packet, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        RequireAuxiliaryOwner(packet.SessionId);
        return TimingWriteAsync(async db =>
        {
            var session = await db.AuxiliaryCaptures.SingleOrDefaultAsync(x => x.Id == packet.SessionId, ct)
                ?? throw new SeriesFileException("Auxiliary capture session is missing.");
            if (session.CleanStop) { throw new SeriesFileException("This auxiliary capture session has already ended."); }
            var existing = await db.AuxiliaryRawPackets.FindAsync([packet.SessionId, packet.Sequence], ct);
            if (existing is not null)
            {
                if (existing.ReceivedAt != packet.ReceivedAt || existing.Protocol != packet.Protocol
                    || existing.Source != packet.Source || existing.Stream != packet.Stream
                    || !existing.Bytes.AsSpan().SequenceEqual(packet.Bytes))
                { throw new SeriesFileException("An original auxiliary packet cannot be replaced."); }
                return true;
            }
            var last = await db.AuxiliaryRawPackets.Where(x => x.SessionId == packet.SessionId)
                .MaxAsync(x => (long?)x.Sequence, ct) ?? 0;
            if (packet.Sequence != last + 1) { throw new SeriesFileException("Auxiliary packet sequence has a gap."); }
            db.AuxiliaryRawPackets.Add(new() { SessionId = packet.SessionId, Sequence = packet.Sequence,
                ReceivedAt = packet.ReceivedAt, Protocol = packet.Protocol, Source = packet.Source,
                Stream = packet.Stream, Bytes = packet.Bytes.ToArray() });
            return true;
        }, ct);
    }

    public async Task<AuxiliaryCaptureSession> SwitchAuxiliaryCaptureAsync(Guid sessionId, Guid listId,
        DateTimeOffset at, CancellationToken ct = default)
    {
        await _captureOwnership.WaitAsync(ct);
        var newAttempt = false;
        try
        {
            if (!_auxiliarySwitches.TryGetValue(sessionId, out var attempt))
            {
                RequireAuxiliaryOwner(sessionId);
                attempt = new(Guid.NewGuid(), listId, at);
                _auxiliarySwitches.Add(sessionId, attempt);
                newAttempt = true;
            }
            else
            {
                if (attempt.ListId != listId) { throw new DomainValidationException("Resolve the pending auxiliary run change before choosing another run."); }
                if (!_auxiliaryCaptureIds.ContainsKey(sessionId)) { RequireAuxiliaryOwner(attempt.NextSessionId); }
            }
            var next = await TimingWriteAsync(async db =>
            {
                var previous = await db.AuxiliaryCaptures.SingleAsync(x => x.Id == sessionId, ct);
                if (previous.CleanStop)
                {
                    // Commit may have succeeded before a storage/caller failure was observed. Recover the exact boundary.
                    var committed = await db.AuxiliaryCaptures.SingleOrDefaultAsync(x => x.Id == attempt.NextSessionId, ct);
                    if (committed is null || committed.ListId != listId || committed.Role != previous.Role)
                    { throw new SeriesFileException("The previous auxiliary run ended without the expected next session. Preserve the series for review."); }
                    return ToAuxiliaryCapture(committed);
                }
                if (!previous.Live) { throw new DomainValidationException("Temporary imports remain in their selected run."); }
                var list = await db.StartLists.SingleOrDefaultAsync(x => x.Id == listId, ct)
                    ?? throw new DomainValidationException("Select a saved start list for auxiliary capture.");
                if (await db.StartLists.AnyAsync(x => x.RunId == list.RunId && x.Revision > list.Revision, ct))
                { throw new DomainValidationException("Choose the latest start-list revision for auxiliary capture."); }
                previous.StoppedAt = attempt.At; previous.CleanStop = true;
                var row = new AuxiliaryCaptureRow { Id = attempt.NextSessionId, ListId = listId,
                    Role = previous.Role, Live = true, OptionsJson = previous.OptionsJson, StartedAt = attempt.At };
                db.AuxiliaryCaptures.Add(row);
                return ToAuxiliaryCapture(row);
            }, ct);
            _auxiliaryCaptureIds.TryRemove(sessionId, out _);
            _auxiliaryCaptureIds.TryAdd(next.Capture.Id, next.Role);
            _closedAuxiliaryIds.Add(sessionId);
            return next;
        }
        catch (DomainValidationException) { if (newAttempt) { _auxiliarySwitches.Remove(sessionId); } throw; }
        finally { _captureOwnership.Release(); }
    }

    public async Task EndAuxiliaryCaptureAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default)
    {
        await _captureOwnership.WaitAsync(ct);
        try
        {
            // A retry after a caller observed an uncertain commit must not reacquire or release another owner's lease.
            if (_closedAuxiliaryIds.Contains(sessionId)) { return; }
            RequireAuxiliaryOwner(sessionId);
            await TimingWriteAsync(async db =>
            {
                var row = await db.AuxiliaryCaptures.SingleAsync(x => x.Id == sessionId, ct);
                row.StoppedAt ??= at; row.CleanStop = true;
                return true;
            }, ct);
            _auxiliaryCaptureIds.TryRemove(sessionId, out _);
            _closedAuxiliaryIds.Add(sessionId);
            ReleaseCaptureLeaseIfIdle();
        }
        finally { _captureOwnership.Release(); }
    }

    private void RequireAuxiliaryOwner(Guid sessionId)
    {
        if (_captureLease is null || !_auxiliaryCaptureIds.ContainsKey(sessionId))
        { throw new SeriesFileException("Only this auxiliary capture's owner can append or close its session."); }
    }
    private static AuxiliaryCaptureSession ToAuxiliaryCapture(AuxiliaryCaptureRow row) => new(
        new(row.Id, row.ListId, JsonSerializer.Deserialize<CaptureOptions>(row.OptionsJson)
            ?? throw new SeriesFileException("Invalid auxiliary capture settings."), row.StartedAt, row.StoppedAt, row.CleanStop),
        row.Role, row.Live);
}
