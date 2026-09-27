using System.Threading.Channels;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Application;

public sealed record CaptureOptions(string Device, string Endpoint, DateOnly DeviceDate,
    int StartChannel = 0, int FinishChannel = 1, bool Simulation = false, string Firmware = "Not queried",
    string? StartDeviceId = null, string? FinishDeviceId = null, DateTimeOffset? FromUtc = null,
    string CalculationVersion = "alpine-net-hundredths/v1")
{
    public string Operator { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 38400;
    public int[] IntermediateChannels { get; init; } = [];

    public void Validate()
    {
        if (CalculationVersion != "alpine-net-hundredths/v1") { throw new DomainValidationException("Unsupported timing calculation version."); }
        if (BaudRate is < 1200 or > 115200) { throw new DomainValidationException("Unsupported serial baud rate."); }
        if (string.IsNullOrWhiteSpace(Device) || string.IsNullOrWhiteSpace(Endpoint)
            || StartChannel is < 0 or > 8 || FinishChannel is < 0 or > 8
            || (StartChannel == FinishChannel && (StartDeviceId is null || StartDeviceId == FinishDeviceId)))
        { throw new DomainValidationException("Choose a device and two different start/finish channels (0–8)."); }
        if (IntermediateChannels is null || IntermediateChannels.Any(x => x is < 0 or > 8 || x == StartChannel || x == FinishChannel)
            || IntermediateChannels.Distinct().Count() != IntermediateChannels.Length)
        { throw new DomainValidationException("Intermediate channels must be distinct (0–8), separate from start and finish."); }
    }
}

public sealed record CaptureSession(Guid Id, Guid ListId, CaptureOptions Options, DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt, bool CleanStop);
public sealed record TransportPacket(string Protocol, string Source, string Stream, byte[] Bytes, DateTimeOffset? ReceivedAt = null);
public sealed record RawTimingPacket(Guid SessionId, long Sequence, DateTimeOffset ReceivedAt,
    string Protocol, string Source, string Stream, byte[] Bytes);
public sealed record TimingReplayData(StartListRevision List, IReadOnlyList<CaptureSession> Sessions,
    IReadOnlyList<RawTimingPacket> Packets, IReadOnlyList<TimingAudit> Audit);

public interface ITimingStore
{
    Task<TimingReplayData> ReadTimingAsync(Guid listId, CancellationToken ct = default);
    Task<CaptureSession> BeginCaptureAsync(Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, CancellationToken ct = default);
    Task AppendRawAsync(RawTimingPacket packet, CancellationToken ct = default);
    Task EndCaptureAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default);
    Task<TimingAudit> AppendTimingAuditAsync(Guid listId, long expectedVersion, TimingDecision before,
        TimingDecision after, string operatorName, string reason, DateTimeOffset at, long? reversesId = null, CancellationToken ct = default);
}

public interface ITimingSource : IAsyncDisposable
{
    Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct);
}

public interface ITimingDecoder
{
    IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet);
    IReadOnlyList<TimingObservation> Complete();
}

public interface ITimingDecoderFactory
{
    ITimingDecoder Create(CaptureSession session, string protocol, string source, string stream);
}

// This object belongs to exactly one series session. Device work never uses the UI synchronization context.
public sealed class TimingWorkspace(ITimingStore store, ITimingDecoderFactory decoders) : IAsyncDisposable
{
    private readonly SemaphoreSlim _state = new(1, 1);
    private readonly List<TimingObservation> _observations = [];
    private readonly List<TimingAudit> _audit = [];
    private readonly Dictionary<string, ITimingDecoder> _decoders = new(StringComparer.Ordinal);
    private readonly List<CaptureSession> _sessions = [];
    private StartListRevision? _list;
    private TimingSnapshot? _snapshot;
    private ITimingSource? _source;
    private CancellationTokenSource? _readCancellation;
    private Channel<TransportPacket>? _queue;
    private Task? _producer;
    private Task? _writer;
    private TaskCompletionSource _retry = NewSignal();
    private TaskCompletionSource _failed = NewSignal();
    private string? _fault;
    private string _connection = "Disconnected";
    private int _pending;
    private long _saved;
    private readonly int[] _expected = new int[22];
    private readonly bool[] _held = new bool[22];
    private bool _followOrder;
    public bool IsActive => _producer is not null;
    public string Connection => Volatile.Read(ref _connection);
    public string? Fault => Volatile.Read(ref _fault);
    public int Pending => Volatile.Read(ref _pending);
    public long SavedPackets => Interlocked.Read(ref _saved);
    public TimingSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public bool IsSimulation => _sessions.Any(x => x.Options.Simulation);
    public bool HasCaptureHistory => _sessions.Count != 0;
    public CaptureOptions? LastCaptureOptions => _sessions.LastOrDefault()?.Options;
    public Guid? ListId => _list?.Id;
    public int? ArmedStart => ExpectedBib(0);
    public int? ArmedFinish => ExpectedBib(1);
    public int? ExpectedBib(int channel) => _expected[channel] is > 0 and var bib ? bib : null;
    public bool IsHeld(int channel) => _held[channel];
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task SelectRunAsync(Guid listId, CancellationToken ct = default)
    {
        if (IsActive) { throw new DomainValidationException("Disconnect the timing device before changing the active run."); }
        var data = await store.ReadTimingAsync(listId, ct);
        await _state.WaitAsync(ct);
        try
        {
            _list = data.List;
            _observations.Clear(); _audit.Clear(); _decoders.Clear(); _sessions.Clear();
            _audit.AddRange(data.Audit); _sessions.AddRange(data.Sessions);
            foreach (var session in data.Sessions)
            {
                session.Options.Validate();
                foreach (var packet in data.Packets.Where(x => x.SessionId == session.Id).OrderBy(x => x.Sequence)) { Decode(session, packet); }
                FinishDecoders();
                if (!session.CleanStop)
                {
                    _observations.Add(new($"{session.Id:N}:interrupted", session.Id, 0, session.Options.Endpoint,
                        $"interrupted:{session.Id:N}", ObservationKind.Invalid, null, null, 0, null, false,
                        string.Empty, "Capture ended unexpectedly. Recover missing impulses from device memory/backup and review before continuing."));
                }
            }
            Array.Clear(_expected); Array.Clear(_held); _followOrder = false;
            Interlocked.Exchange(ref _saved, data.Packets.Count);
            Rebuild();
        }
        finally { _state.Release(); }
    }

    public async Task StartAsync(ITimingSource source, CaptureOptions options, string operatorName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (IsActive || _list is null) { throw new DomainValidationException("Choose a saved start list and disconnect any previous source first."); }
        var session = await store.BeginCaptureAsync(_list.Id, options, operatorName, DateTimeOffset.UtcNow, ct);
        _sessions.Add(session);
        _source = source;
        _queue = Channel.CreateBounded<TransportPacket>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _readCancellation = new();
        _fault = null; _failed = NewSignal(); _connection = "Connecting…";
        var queue = _queue;
        var token = _readCancellation.Token;
        _writer = Task.Run(async () =>
        {
            try { await WriteLoopAsync(session, queue); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _fault = "Capture processing stopped unexpectedly. Keep this file open; received input may still be waiting. Preserve device memory/backup timing.";
                _failed.TrySetResult();
                _readCancellation.Cancel();
                throw;
            }
        }, CancellationToken.None);
        _producer = Task.Run(async () =>
        {
            try
            {
                await source.ReceiveAsync(async packet =>
                {
                    if (packet.Bytes.Length > 4_000_000) { throw new IOException("Device frame exceeds the capture size limit."); }
                    Interlocked.Increment(ref _pending);
                    // Once delivered, this packet must drain even if the operator stops capture.
                    await queue.Writer.WriteAsync(packet with { Bytes = packet.Bytes.ToArray(), ReceivedAt = packet.ReceivedAt ?? DateTimeOffset.UtcNow });
                }, text => Volatile.Write(ref _connection, text), token);
                if (!token.IsCancellationRequested) { _connection = "Source completed · disconnect to finish capture"; }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _connection = "Disconnected · " + ex.Message;
                Interlocked.Increment(ref _pending);
                await queue.Writer.WriteAsync(new("transport-status", options.Endpoint, "failure", System.Text.Encoding.UTF8.GetBytes(_connection)));
            }
            finally { queue.Writer.TryComplete(); }
        }, CancellationToken.None);
    }

    private async Task WriteLoopAsync(CaptureSession session, Channel<TransportPacket> queue)
    {
        long sequence = 0;
        await foreach (var input in queue.Reader.ReadAllAsync())
        {
            var raw = new RawTimingPacket(session.Id, ++sequence, input.ReceivedAt ?? DateTimeOffset.UtcNow, input.Protocol, input.Source, input.Stream, input.Bytes);
            await RetryDurableAsync(() => store.AppendRawAsync(raw));
            Interlocked.Increment(ref _saved);
            Interlocked.Decrement(ref _pending);
            await _state.WaitAsync();
            try
            {
                var before = _observations.Count;
                Decode(session, raw);
                if (_observations.Count != before)
                {
                    Rebuild();
                    foreach (var observation in _observations.Skip(before).ToArray()) { await RetryDurableAsync(() => AutoAssignAsync(observation)); }
                }
            }
            finally { _state.Release(); }
        }
        await _state.WaitAsync();
        try { FinishDecoders(); Rebuild(); }
        finally { _state.Release(); }
        await RetryDurableAsync(() => store.EndCaptureAsync(session.Id, DateTimeOffset.UtcNow));
    }

    private async Task RetryDurableAsync(Func<Task> write)
    {
        while (true)
        {
            try { await write(); return; }
            catch (Exception ex) when (ex is SeriesFileException or IOException or UnauthorizedAccessException)
            {
                _retry = NewSignal();
                _fault = "CAPTURE NOT SAVED. " + ex.Message + " Free disk space/check storage, then Retry storage. Keep this file open.";
                _failed.TrySetResult();
                await _retry.Task;
            }
        }
    }

    public void RetryStorage()
    {
        if (_writer?.IsFaulted == true) { return; }
        _failed = NewSignal(); _fault = null;
        _retry.TrySetResult();
    }

    private void Decode(CaptureSession session, RawTimingPacket packet)
    {
        var key = $"{session.Id:N}:{packet.Protocol}:{packet.Source}:{packet.Stream}";
        if (!_decoders.TryGetValue(key, out var decoder))
        { decoder = decoders.Create(session, packet.Protocol, packet.Source, packet.Stream); _decoders.Add(key, decoder); }
        _observations.AddRange(TimingReplay.Decode(decoder, packet));
    }

    private void FinishDecoders()
    {
        foreach (var decoder in _decoders.Values) { _observations.AddRange(decoder.Complete()); }
        _decoders.Clear();
    }

    private void Rebuild()
    {
        if (_list is not null) { Volatile.Write(ref _snapshot, TimingEngine.Replay(_list, _observations, _audit, 0, 1)); }
    }

    private async Task AutoAssignAsync(TimingObservation observation)
    {
        var review = _snapshot!.Observations.FirstOrDefault(x => x.Observation.Key == observation.Key);
        if (review?.State != "Unassigned" || observation.Kind != ObservationKind.Impulse) { return; }
        if (observation.Channel is { } paused && IsHeld(paused)) { return; }
        var bib = observation.SuggestedBib;
        var reason = "Explicit device bib";
        if (bib is null)
        {
            bib = observation.Channel is { } channel && channel < _expected.Length ? ExpectedBib(channel) : null;
            reason = _followOrder ? "Race queue: expected bib" : "Operator armed bib";
        }
        if (bib is null || !_snapshot.Results.Any(x => x.Bib == bib)) { return; }
        var operatorName = _sessions.Single(x => x.Id == observation.SessionId).Options.Operator;
        await AppendDecisionAsync(new(DecisionKind.Assignment, observation.Key, Bib: bib), operatorName, reason);
        if (observation.Channel is { } consumed && ExpectedBib(consumed) == bib) { _expected[consumed] = 0; }
        AdvanceQueues();
    }

    public async Task ArmAsync(int? startBib, int? finishBib, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            foreach (var bib in new[] { startBib, finishBib }.OfType<int>())
            { if (_snapshot is null || !_snapshot.Results.Any(x => x.Bib == bib)) { throw new DomainValidationException("Choose a bib on this start list."); } }
            _expected[0] = startBib ?? 0; _expected[1] = finishBib ?? 0;
        }
        finally { _state.Release(); }
    }

    public async Task CorrectAsync(TimingDecision decision, string operatorName, string reason, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);
        await _state.WaitAsync(ct);
        try
        {
            var before = TimingEngine.CurrentDecision(decision, _audit);
            await AppendDecisionAsync(decision, operatorName, reason);
            ReconcileCorrectedQueue(before, decision);
        }
        finally { _state.Release(); }
    }

    private async Task AppendDecisionAsync(TimingDecision decision, string operatorName, string reason, long? reversesId = null)
    {
        if (_snapshot is null) { throw new DomainValidationException("Choose a run first."); }
        TimingEngine.ValidateDecision(decision, _snapshot);
        var before = TimingEngine.CurrentDecision(decision, _audit);
        if (before == decision) { return; }
        var change = await store.AppendTimingAuditAsync(_snapshot.ListId, _snapshot.AuditVersion, before, decision,
            operatorName, reason, DateTimeOffset.UtcNow, reversesId);
        _audit.Add(change); Rebuild();
    }

    public async Task UndoAsync(long auditId, string operatorName, string reason, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            var action = _audit.SingleOrDefault(x => x.Id == auditId) ?? throw new DomainValidationException("Select a change to undo.");
            if (TimingEngine.CurrentDecision(action.After, _audit) != action.After)
            { throw new DomainValidationException("A later change affects the same value. Undo the later change first."); }
            await AppendDecisionAsync(action.Before, operatorName, reason, action.Id);
            ReconcileCorrectedQueue(action.After, action.Before);
        }
        finally { _state.Release(); }
    }

    public async Task FollowStartOrderAsync(bool enabled, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try { _followOrder = enabled; if (enabled) { AdvanceQueues(); } }
        finally { _state.Release(); }
    }

    public async Task ExpectAsync(int channel, int? bib, bool held = false, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot is null || channel < 0 || channel >= 2 + _list!.Plan.Competition.IntermediateCount)
            { throw new DomainValidationException("Choose a timing position in this run."); }
            if (bib is not null && !RaceFlow.Expected(_snapshot, channel).Any(x => x.Bib == bib))
            { throw new DomainValidationException("Choose a competitor waiting at this timing position."); }
            _expected[channel] = held ? 0 : bib ?? 0; _held[channel] = held;
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    private void ReconcileCorrectedQueue(TimingDecision before, TimingDecision after)
    {
        if (_followOrder && _snapshot is not null && before.Kind == DecisionKind.Assignment && before.Bib is { } bib && after.Bib != bib)
        {
            var observation = _snapshot.Observations.FirstOrDefault(x => x.Observation.Key == before.ObservationKey)?.Observation;
            if (observation?.Channel is { } channel && channel < _expected.Length && !_held[channel]
                && RaceFlow.Expected(_snapshot, channel).Any(x => x.Bib == bib)) { _expected[channel] = bib; }
        }
        AdvanceQueues();
    }

    private void AdvanceQueues()
    {
        if (!_followOrder || _snapshot is null) { return; }
        for (var channel = 0; channel < 2 + _list!.Plan.Competition.IntermediateCount; channel++)
        {
            if (_held[channel]) { _expected[channel] = 0; continue; }
            var queue = RaceFlow.Expected(_snapshot, channel);
            if (!queue.Any(x => x.Bib == _expected[channel])) { _expected[channel] = queue.Count == 0 ? 0 : queue[0].Bib; }
        }
    }

    public async Task StopAsync()
    {
        if (_producer is null) { return; }
        _readCancellation!.Cancel();
        if (Fault is not null) { throw new SeriesFileException(Fault); }
        var drain = Task.WhenAll(_producer, _writer!);
        var completed = await Task.WhenAny(drain, _failed.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        if (completed != drain) { throw new SeriesFileException(Fault ?? "Capture is still draining. Keep the file open and retry disconnect."); }
        await drain;
        await _source!.DisposeAsync();
        _source = null; _producer = _writer = null;
        _readCancellation.Dispose(); _readCancellation = null;
        _connection = "Disconnected · received data saved";
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _state.Dispose();
    }
}
