using System.Threading.Channels;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public sealed record CaptureOptions(string Device, string Endpoint, DateOnly DeviceDate,
    int StartChannel = 0, int FinishChannel = 1, bool Simulation = false, string Firmware = "Not queried",
    string? StartDeviceId = null, string? FinishDeviceId = null, DateTimeOffset? FromUtc = null,
    string CalculationVersion = "alpine-net-hundredths/v1")
{
    public string Operator { get; init; } = string.Empty;
    public int BaudRate { get; init; } = 38400;
    public int[] IntermediateChannels { get; init; } = [];
    // Explicit role routing for one device connection of a multi-device capture: physical channel (and ALGE Results
    // device ID) to timing position (0 start, 1 finish, 2.. intermediates). Null keeps the original single-device
    // Start/Finish/IntermediateChannels mapping used by earlier sessions.
    public CaptureChannelRoute[]? Routes { get; init; }
    // Device sessions started together for one capture share this group: their clocks are set to the same time, so
    // elapsed times may span devices. Null keeps the original per-session clock context.
    public string? ClockGroup { get; init; }

    // Physical channel that this session maps to a timing position (0 start, 1 finish, 2.. intermediates), if any.
    public int? Channel(int position) => Routes is not null ? Routes.FirstOrDefault(x => x.Position == position)?.Channel
        : position == 0 ? StartChannel : position == 1 ? FinishChannel
        : position - 2 < IntermediateChannels.Length && position >= 2 ? IntermediateChannels[position - 2] : null;

    // Timing position for a received physical channel, or null when this session does not map it.
    public int? Position(int channel, string? deviceId = null)
    {
        if (Routes is not null)
        {
            return Routes.FirstOrDefault(x => x.Channel == channel
                && (x.DeviceId is null || string.Equals(x.DeviceId, deviceId, StringComparison.Ordinal)))?.Position;
        }
        if (channel == StartChannel && (StartDeviceId is null || deviceId is null || deviceId == StartDeviceId)) { return 0; }
        if (channel == FinishChannel && (FinishDeviceId is null || deviceId is null || deviceId == FinishDeviceId)) { return 1; }
        var intermediate = Array.IndexOf(IntermediateChannels, channel);
        return intermediate >= 0 ? intermediate + 2 : null;
    }

    public void Validate()
    {
        if (CalculationVersion != "alpine-net-hundredths/v1") { throw new DomainValidationException("Unsupported timing calculation version."); }
        if (BaudRate is < 1200 or > 115200) { throw new DomainValidationException("Unsupported serial baud rate."); }
        if (Routes is not null)
        {
            if (string.IsNullOrWhiteSpace(Device) || string.IsNullOrWhiteSpace(Endpoint) || Routes.Length == 0
                || Routes.Any(x => x is null || x.Channel is < 0 or > 8 || x.Position is < 0 or > 21)
                || Routes.Select(x => x.Position).Distinct().Count() != Routes.Length
                || Routes.Select(x => (x.Channel, x.DeviceId)).Distinct().Count() != Routes.Length)
            { throw new DomainValidationException("Each timing position needs its own device channel (C0–C8)."); }
            return;
        }
        if (string.IsNullOrWhiteSpace(Device) || string.IsNullOrWhiteSpace(Endpoint)
            || StartChannel is < 0 or > 8 || FinishChannel is < 0 or > 8
            || (StartChannel == FinishChannel && (StartDeviceId is null || StartDeviceId == FinishDeviceId)))
        { throw new DomainValidationException("Choose a device and two different start/finish channels (0–8)."); }
        if (IntermediateChannels is null || IntermediateChannels.Any(x => x is < 0 or > 8 || x == StartChannel || x == FinishChannel)
            || IntermediateChannels.Distinct().Count() != IntermediateChannels.Length)
        { throw new DomainValidationException("Intermediate channels must be distinct (0–8), separate from start and finish."); }
    }
}

public sealed record CaptureChannelRoute(int Channel, int Position, string? DeviceId = null);

public sealed record CaptureSession(Guid Id, Guid ListId, CaptureOptions Options, DateTimeOffset StartedAt,
    DateTimeOffset? StoppedAt, bool CleanStop);
public sealed record TransportPacket(string Protocol, string Source, string Stream, byte[] Bytes, DateTimeOffset? ReceivedAt = null);
public sealed record RawTimingPacket(Guid SessionId, long Sequence, DateTimeOffset ReceivedAt,
    string Protocol, string Source, string Stream, byte[] Bytes);
public sealed record TimingReplayData(StartListRevision List, IReadOnlyList<CaptureSession> Sessions,
    IReadOnlyList<RawTimingPacket> Packets, IReadOnlyList<TimingAudit> Audit);
public sealed record TimingAuditChange(TimingDecision Before, TimingDecision After, long? ReversesId = null);

public interface ITimingStore
{
    Task<TimingReplayData> ReadTimingAsync(Guid listId, CancellationToken ct = default);
    Task<CaptureSession> BeginCaptureAsync(Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, CancellationToken ct = default);
    Task<CaptureSession> SwitchCaptureAsync(Guid previousSessionId, Guid listId, CaptureOptions options, string operatorName, DateTimeOffset at, CancellationToken ct = default)
        => throw new NotSupportedException("This timing store cannot switch runs during capture.");
    // Several device connections of one capture begin, and switch runs, atomically: all sessions or none.
    async Task<IReadOnlyList<CaptureSession>> BeginCaptureGroupAsync(Guid listId, IReadOnlyList<CaptureOptions> options,
        string operatorName, DateTimeOffset at, CancellationToken ct = default)
        => options?.Count == 1 ? [await BeginCaptureAsync(listId, options[0], operatorName, at, ct)]
            : throw new NotSupportedException("This timing store cannot capture several devices.");
    async Task<IReadOnlyList<CaptureSession>> SwitchCaptureGroupAsync(IReadOnlyList<Guid> previousSessionIds, Guid listId,
        IReadOnlyList<CaptureOptions> options, string operatorName, DateTimeOffset at, CancellationToken ct = default)
        => options?.Count == 1 && previousSessionIds?.Count == 1
            ? [await SwitchCaptureAsync(previousSessionIds[0], listId, options[0], operatorName, at, ct)]
            : throw new NotSupportedException("This timing store cannot switch several devices.");
    Task AppendRawAsync(RawTimingPacket packet, CancellationToken ct = default);
    Task EndCaptureAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default);
    Task<TimingAudit> AppendTimingAuditAsync(Guid listId, long expectedVersion, TimingDecision before,
        TimingDecision after, string operatorName, string reason, DateTimeOffset at, long? reversesId = null,
        bool startsRun = false, CancellationToken ct = default);
    Task<IReadOnlyList<TimingAudit>> AppendTimingAuditBatchAsync(Guid listId, long expectedVersion,
        IReadOnlyList<TimingAuditChange> changes, string operatorName, string reason, DateTimeOffset at,
        bool startsRun = false, CancellationToken ct = default)
        => throw new NotSupportedException("This timing store cannot save a timestamp transfer atomically.");
}

public interface ITimingSource : IAsyncDisposable
{
    Task ReceiveAsync(Func<TransportPacket, ValueTask> receive, Action<string> status, CancellationToken ct);
}

public interface ITimingDecoder
{
    bool HasPendingInput => false;
    IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet);
    IReadOnlyList<TimingObservation> Complete();
}

public interface ITimingDecoderFactory
{
    ITimingDecoder Create(CaptureSession session, string protocol, string source, string stream);
}

public sealed record TimingSourceInput(ITimingSource Source, CaptureOptions Options);

// Latest impulse per physical device channel, for the Settings signal monitor. Diagnostics only; never used for timing.
public sealed record TimingSignal(string Device, int Channel, long DeviceTicks, int Precision, DateTimeOffset ReceivedAt);

public static class TimingSignals
{
    // ALGE Results observations name their MT1 device; other sources are identified by their connection.
    public static string DeviceKey(CaptureOptions options, string observationSource)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Device == TimingSourceTypes.AlgeResultsLabel ? "alge:" + observationSource : options.Device + "|" + options.Endpoint;
    }

    public static string DeviceKey(TimingConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return connection.Source == TimingSourceType.AlgeResults ? "alge:" + connection.AlgeDeviceId.Trim() : connection.SourceLabel + "|" + connection.Endpoint;
    }

    internal static void Record(System.Collections.Concurrent.ConcurrentDictionary<(string, int), TimingSignal> signals,
        CaptureOptions options, IEnumerable<TimingObservation> observations, DateTimeOffset receivedAt)
    {
        foreach (var o in observations)
        {
            if (o.PhysicalChannel is not { } channel || o.DeviceTicks is not { } ticks || o.Kind is not (ObservationKind.Impulse or ObservationKind.Information)) { continue; }
            var device = DeviceKey(options, o.Source);
            // History pages arrive newest first; keep the latest device time per channel, not the last one processed.
            signals.AddOrUpdate((device, channel), new TimingSignal(device, channel, ticks, o.Precision, receivedAt),
                (_, old) => ticks >= old.DeviceTicks ? new TimingSignal(device, channel, ticks, o.Precision, receivedAt) : old);
        }
    }
}

// This object belongs to exactly one series session. Device work never uses the UI synchronization context.
// One capture may read several device connections. Each has its own session and raw journal; one writer loop
// commits every packet durably, in arrival order, before decoding and automatic assignment.
public sealed class TimingWorkspace(ITimingStore store, ITimingDecoderFactory decoders) : IAsyncDisposable
{
    private sealed record CaptureInput(TransportPacket? Packet = null, int Source = 0, RunChange? Change = null);
    private sealed record RunChange(Guid ListId, TaskCompletionSource Completion);
    private readonly RunningTimingClock _runningClock = new();
    private readonly SemaphoreSlim _state = new(1, 1);
    private readonly List<TimingObservation> _observations = [];
    private readonly List<TimingAudit> _audit = [];
    private readonly Dictionary<string, ITimingDecoder> _decoders = new(StringComparer.Ordinal);
    private readonly List<CaptureSession> _sessions = [];
    private StartListRevision? _list;
    private TimingSnapshot? _snapshot;
    private ITimingSource[] _sources = [];
    private string[] _labels = [];
    private string[] _connections = [];
    private CaptureOptions[] _activeOptions = [];
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int), TimingSignal> _signals = new();
    private CancellationTokenSource? _readCancellation;
    private Channel<CaptureInput>? _queue;
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
    public string Connection
    {
        get
        {
            var connections = Volatile.Read(ref _connections);
            if (!IsActive || connections.Length == 0) { return Volatile.Read(ref _connection); }
            if (connections.Length == 1) { return Volatile.Read(ref connections[0]); }
            return string.Join(" · ", connections.Select((x, i) => $"{_labels[i]}: {Volatile.Read(ref connections[i])}"));
        }
    }
    public string? Fault => Volatile.Read(ref _fault);
    public int Pending => Volatile.Read(ref _pending);
    public long SavedPackets => Interlocked.Read(ref _saved);
    public TimingSnapshot? Snapshot => Volatile.Read(ref _snapshot);
    public bool IsSimulation => _sessions.Any(x => x.Options.Simulation);
    public bool HasCaptureHistory => _sessions.Count != 0;
    public CaptureOptions? LastCaptureOptions => _sessions.LastOrDefault()?.Options;
    // Options of every device session in the running capture (one per physical connection).
    public IReadOnlyList<CaptureOptions> ActiveCaptureOptions => IsActive ? Volatile.Read(ref _activeOptions) : [];
    // Latest impulse per physical device channel received by the running capture (Settings signal monitor).
    public IReadOnlyList<TimingSignal> RecentSignals => IsActive ? _signals.Values.ToArray() : [];
    // The device sessions of the most recent capture start in this run, for restoring settings without preferences.
    public IReadOnlyList<CaptureOptions> LastCaptureGroup
    {
        get
        {
            var last = _sessions.LastOrDefault();
            if (last is null) { return []; }
            if (last.Options.ClockGroup is not { } group) { return [last.Options]; }
            return _sessions.Where(x => x.Options.ClockGroup == group)
                .GroupBy(x => (x.Options.Device, x.Options.Endpoint)).Select(x => x.Last().Options).ToArray();
        }
    }
    public Guid? ListId => _list?.Id;
    public int? ArmedStart => ExpectedBib(0);
    public int? ArmedFinish => ExpectedBib(1);
    public int? ExpectedBib(int channel) => _expected[channel] is > 0 and var bib ? bib : null;
    public bool IsHeld(int channel) => _held[channel];
    public long? LiveDeviceTicks => _runningClock.DeviceNowTicks(TimeSpan.FromSeconds(5));
    public long? RunningHundredths(string? startKey)
    {
        var start = Snapshot?.Observations.FirstOrDefault(x => x.Observation.Key == startKey)?.Observation;
        return start is null ? null : _runningClock.ElapsedHundredths(start);
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task SelectRunAsync(Guid listId, CancellationToken ct = default)
    {
        if (IsActive)
        {
            if (ListId == listId) { return; }
            if (_producer!.IsCompleted || Fault is not null)
            { throw new SeriesFileException("The timing source needs attention in Settings before changing runs."); }
            var change = new RunChange(listId, NewSignal());
            try { await _queue!.Writer.WriteAsync(new(Change: change), ct); }
            catch (ChannelClosedException ex) { throw new SeriesFileException("The timing source stopped before the run could be changed.", ex); }
            if (await Task.WhenAny(change.Completion.Task, _writer!) != change.Completion.Task)
            { throw new SeriesFileException("The timing source stopped before the run could be changed. Check Settings."); }
            await change.Completion.Task;
            return;
        }
        var data = await store.ReadTimingAsync(listId, ct);
        await _state.WaitAsync(ct);
        try
        {
            _list = data.List;
            _observations.Clear(); _audit.Clear(); _decoders.Clear(); _sessions.Clear();
            _audit.AddRange(data.Audit); _sessions.AddRange(data.Sessions);
            foreach (var unit in TimingReplay.Units(data.Sessions, data.Packets))
            {
                foreach (var session in unit.Sessions) { session.Options.Validate(); }
                foreach (var (session, packet) in unit.Packets) { Decode(session, packet); }
                FinishDecoders();
                foreach (var session in unit.Sessions.Where(x => !x.CleanStop))
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


    public async Task RefreshIntermediateCountAsync(CancellationToken ct = default)
    {
        var listId = _list?.Id ?? throw new DomainValidationException("Choose a timing run first.");
        var current = (await store.ReadTimingAsync(listId, ct)).List.Plan.Competition.IntermediateCount;
        await _state.WaitAsync(ct);
        try
        {
            if (_list?.Id != listId || _list.Plan.Competition.IntermediateCount == current) { return; }
            var previous = _list.Plan.Competition.IntermediateCount;
            _list = _list with { Plan = _list.Plan with
            { Competition = _list.Plan.Competition with { IntermediateCount = current } } };
            for (var channel = 2 + Math.Min(previous, current); channel < 2 + Math.Max(previous, current); channel++)
            {
                _expected[channel] = 0;
                _held[channel] = true;
            }
            Rebuild();
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    public Task StartAsync(ITimingSource source, CaptureOptions options, string operatorName, CancellationToken ct = default)
        => StartAsync([new TimingSourceInput(source, options)], operatorName, ct);

    public async Task StartAsync(IReadOnlyList<TimingSourceInput> inputs, string operatorName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0) { throw new DomainValidationException("Choose at least one timing device."); }
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input?.Source);
            ArgumentNullException.ThrowIfNull(input.Options);
            input.Options.Validate();
        }
        if (IsActive || _list is null) { throw new DomainValidationException("Choose a saved start list and disconnect any previous source first."); }
        var sessions = (await store.BeginCaptureGroupAsync(_list.Id, inputs.Select(x => x.Options).ToArray(), operatorName, DateTimeOffset.UtcNow, ct)).ToArray();
        if (sessions.Length != inputs.Count) { throw new SeriesFileException("The timing store did not open a session for every device."); }
        _sessions.AddRange(sessions);
        var count = inputs.Count;
        _sources = inputs.Select(x => x.Source).ToArray();
        _labels = sessions.Select(x => $"{x.Options.Device} · {x.Options.Endpoint.Trim()}").ToArray();
        Volatile.Write(ref _connections, Enumerable.Repeat("Connecting…", count).ToArray());
        Volatile.Write(ref _activeOptions, sessions.Select(x => x.Options).ToArray());
        _queue = Channel.CreateBounded<CaptureInput>(new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        _runningClock.Clear();
        _signals.Clear();
        _readCancellation = new();
        _fault = null; _failed = NewSignal(); _connection = "Connecting…";
        var queue = _queue;
        var token = _readCancellation.Token;
        var connections = _connections;
        _writer = Task.Run(async () =>
        {
            try { await WriteLoopAsync(sessions, queue); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _fault = "Capture processing stopped unexpectedly. Keep this file open; received input may still be waiting. Preserve device memory/backup timing.";
                _failed.TrySetResult();
                _readCancellation.Cancel();
                throw;
            }
        }, CancellationToken.None);
        var remaining = count;
        // Each device has its own producer. One device failing or completing never stops the others.
        var producers = Enumerable.Range(0, count).Select(index => Task.Run(async () =>
        {
            var source = _sources[index];
            var endpoint = sessions[index].Options.Endpoint;
            try
            {
                await source.ReceiveAsync(async packet =>
                {
                    if (packet.Bytes.Length > 4_000_000) { throw new IOException("Device frame exceeds the capture size limit."); }
                    Interlocked.Increment(ref _pending);
                    // Once delivered, this packet must drain even if the operator stops capture.
                    await queue.Writer.WriteAsync(new(Packet: packet with { Bytes = packet.Bytes.ToArray(), ReceivedAt = packet.ReceivedAt ?? DateTimeOffset.UtcNow }, Source: index));
                }, text => Volatile.Write(ref connections[index], text), token);
                if (!token.IsCancellationRequested) { Volatile.Write(ref connections[index], "Source completed · disconnect to finish capture"); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var text = "Disconnected · " + ex.Message;
                Volatile.Write(ref connections[index], text);
                Interlocked.Increment(ref _pending);
                await queue.Writer.WriteAsync(new(Packet: new("transport-status", endpoint, "failure", System.Text.Encoding.UTF8.GetBytes(text)), Source: index));
            }
            finally { if (Interlocked.Decrement(ref remaining) == 0) { queue.Writer.TryComplete(); } }
        }, CancellationToken.None)).ToArray();
        _producer = Task.WhenAll(producers);
    }

    private async Task WriteLoopAsync(CaptureSession[] sessions, Channel<CaptureInput> queue)
    {
        var sequences = new long[sessions.Length];
        RunChange? change = null;
        await foreach (var input in queue.Reader.ReadAllAsync())
        {
            if (input.Change is { } requested)
            {
                if (change is not null) { requested.Completion.TrySetException(new DomainValidationException("A run change is already waiting for the current device message.")); }
                else { change = requested; }
            }
            if (input.Packet is { } packet)
            {
                var session = sessions[input.Source];
                var raw = new RawTimingPacket(session.Id, ++sequences[input.Source], packet.ReceivedAt ?? DateTimeOffset.UtcNow, packet.Protocol, packet.Source, packet.Stream, packet.Bytes);
                await RetryDurableAsync(() => store.AppendRawAsync(raw));
                Interlocked.Increment(ref _saved);
                Interlocked.Decrement(ref _pending);
                await _state.WaitAsync();
                try
                {
                    var before = _observations.Count;
                    Decode(session, raw, live: true);
                    if (_observations.Count != before)
                    {
                        Rebuild();
                        foreach (var observation in _observations.Skip(before).ToArray()) { await RetryDurableAsync(() => AutoAssignAsync(observation)); }
                    }
                }
                finally { _state.Release(); }
            }
            // Finish a fragmented device line in its original run before moving the routing boundary.
            if (change is not null && !_decoders.Values.Any(x => x.HasPendingInput))
            {
                await _state.WaitAsync();
                try
                {
                    if (RaceFlow.OnCourse(_snapshot!).Count > 0)
                    { throw new DomainValidationException("Competitors are still on course. Finish or classify them before switching the active timing run."); }
                    var data = await store.ReadTimingAsync(change.ListId);
                    var restored = TimingReplay.Restore(data, decoders);
                    var next = (await store.SwitchCaptureGroupAsync(sessions.Select(x => x.Id).ToArray(), change.ListId,
                        sessions.Select(x => x.Options).ToArray(), sessions[0].Options.Operator, DateTimeOffset.UtcNow)).ToArray();
                    if (next.Length != sessions.Length) { throw new SeriesFileException("The timing store did not switch every device session."); }
                    _list = data.List; _observations.Clear(); _audit.Clear(); _sessions.Clear(); _decoders.Clear();
                    _observations.AddRange(restored.Observations.Select(x => x.Observation)); _audit.AddRange(data.Audit);
                    _sessions.AddRange(data.Sessions); _sessions.AddRange(next);
                    Array.Clear(_expected); Array.Clear(_held);
                    sessions = next; Array.Clear(sequences);
                    Volatile.Write(ref _activeOptions, next.Select(x => x.Options).ToArray());
                    Interlocked.Exchange(ref _saved, data.Packets.Count);
                    Rebuild(); AdvanceQueues();
                    change.Completion.TrySetResult();
                }
                catch (Exception ex) when (ex is not OutOfMemoryException) { change.Completion.TrySetException(ex); }
                finally { change = null; _state.Release(); }
            }
        }
        change?.Completion.TrySetException(new SeriesFileException("The device stopped partway through a message; the active run was not changed."));
        await _state.WaitAsync();
        try { FinishDecoders(); Rebuild(); }
        finally { _state.Release(); }
        foreach (var session in sessions) { await RetryDurableAsync(() => store.EndCaptureAsync(session.Id, DateTimeOffset.UtcNow)); }
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

    private void Decode(CaptureSession session, RawTimingPacket packet, bool live = false)
    {
        var key = $"{session.Id:N}:{packet.Protocol}:{packet.Source}:{packet.Stream}";
        if (!_decoders.TryGetValue(key, out var decoder))
        { decoder = decoders.Create(session, packet.Protocol, packet.Source, packet.Stream); _decoders.Add(key, decoder); }
        var decoded = TimingReplay.Decode(decoder, packet, includeInformation: live);
        if (live)
        {
            // The running display clock follows timing impulses and device heartbeats only; informational device input
            // (for example an ALGE Results channel no role uses, or another device day) must not move it.
            foreach (var observation in decoded.Where(x => x.Kind == ObservationKind.Impulse || (x.Kind == ObservationKind.Information && x.PhysicalChannel is null)))
            { _runningClock.Observe(observation, packet.ReceivedAt); }
            TimingSignals.Record(_signals, session.Options, decoded, packet.ReceivedAt);
        }
        _observations.AddRange(decoded.Where(x => x.Kind != ObservationKind.Information));
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

    public Task CorrectStatusesAsync(IReadOnlyList<int> bibs, TimingStatus? status,
        string operatorName, string reason, CancellationToken ct = default)
        => CorrectStatusesAsync(bibs, status, operatorName, reason, null, ct);

    public async Task CorrectStatusesAsync(IReadOnlyList<int> bibs, TimingStatus? status,
        string operatorName, string reason, DisqualificationDetails? disqualification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bibs);
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot is null || bibs.Count == 0 || bibs.Distinct().Count() != bibs.Count)
            { throw new DomainValidationException("Select one or more distinct starters in this run."); }
            var changes = new List<TimingAuditChange>();
            foreach (var bib in bibs)
            {
                var row = _snapshot.Results.SingleOrDefault(x => x.Bib == bib)
                    ?? throw new DomainValidationException($"Bib {bib} is not in this run.");
                var after = new TimingDecision(DecisionKind.Status, CompetitorId: row.CompetitorId, Status: status,
                    Disqualification: status == TimingStatus.DSQ ? disqualification ?? row.Disqualification : disqualification);
                TimingEngine.ValidateDecision(after, _snapshot);
                var before = TimingEngine.CurrentDecision(after, _audit);
                if (before != after) { changes.Add(new(before, after)); }
            }
            if (changes.Count == 0) { return; }
            var saved = await store.AppendTimingAuditBatchAsync(_snapshot.ListId, _snapshot.AuditVersion,
                changes, operatorName, reason, DateTimeOffset.UtcNow, ct: ct);
            _audit.AddRange(saved);
            Rebuild();
            foreach (var change in changes) { ReconcileCorrectedQueue(change.Before, change.After); }
        }
        finally { _state.Release(); }
    }

    public async Task ReturnToStartAsync(int bib, string startKey, string operatorName, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            var result = _snapshot?.Results.SingleOrDefault(x => x.Bib == bib);
            if (!RaceFlow.CanReturnToStart(result) || result!.StartKey != startKey)
            { throw new DomainValidationException("Select a competitor on course with only a start impulse. Their timing may have changed; check the current row."); }
            await AppendDecisionAsync(new(DecisionKind.Assignment, startKey, Ignored: true), operatorName,
                "False start impulse — competitor returned to start");
            // Publish queue changes only after the correction is durable. Preserve operator holds.
            for (var channel = 1; channel < _expected.Length; channel++)
            { if (_expected[channel] == bib) { _expected[channel] = 0; } }
            _expected[0] = _held[0] ? 0 : bib;
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    public async Task MoveToStartAsync(Guid listId, int bib, string? expectedStart,
        IReadOnlyList<string> expectedAssignedKeys, string operatorName, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot?.ListId != listId) { throw new DomainValidationException("The active run changed. Drag the competitor again."); }
            var result = _snapshot.Results.SingleOrDefault(x => x.Bib == bib);
            if (result is null || result.StartKey != expectedStart)
            { throw new DomainValidationException("The competitor's start changed. Drag the competitor again."); }
            var assigned = _snapshot.Observations.Where(x => x.Bib == bib && !x.Ignored && x.DuplicateOf is null
                && x.Observation.Kind == ObservationKind.Impulse).ToArray();
            if (!assigned.Select(x => x.Observation.Key).Order().SequenceEqual(expectedAssignedKeys.Order()))
            { throw new DomainValidationException("The competitor received new timing while dragging. Check it and drag again."); }
            var changes = assigned.Select(x => new TimingAuditChange(
                TimingEngine.CurrentDecision(new(DecisionKind.Assignment, x.Observation.Key), _audit),
                new(DecisionKind.Assignment, x.Observation.Key, Ignored: true))).ToList();
            var status = TimingEngine.CurrentDecision(new(DecisionKind.Status, CompetitorId: result.CompetitorId), _audit);
            if (status.Status is not null) { changes.Add(new(status, status with { Status = null, Disqualification = null })); }
            var time = TimingEngine.CurrentDecision(new(DecisionKind.Time, CompetitorId: result.CompetitorId), _audit);
            if (time.Hundredths is not null) { changes.Add(new(time, time with { Hundredths = null })); }
            var order = _snapshot.StartOrder.ToList();
            order.Remove(bib);
            var waiting = RaceFlow.Waiting(_snapshot);
            var next = waiting.Count == 0 ? (int?)null : waiting[0].Bib;
            order.Insert(next is null ? 0 : order.IndexOf(next.Value), bib);
            if (!order.SequenceEqual(_snapshot.StartOrder))
            {
                var queue = TimingEngine.CurrentDecision(new(DecisionKind.StartOrder), _audit);
                changes.Add(new(queue, new(DecisionKind.StartOrder, StartOrder: string.Join(',', order))));
            }
            foreach (var change in changes) { TimingEngine.ValidateDecision(change.After, _snapshot); }
            if (changes.Count > 0)
            {
                var saved = await store.AppendTimingAuditBatchAsync(listId, _snapshot.AuditVersion, changes, operatorName,
                    $"Restart Bib {bib} at start; prior impulses retained", DateTimeOffset.UtcNow, ct: ct);
                _audit.AddRange(saved); Rebuild();
            }
            for (var channel = 1; channel < _expected.Length; channel++)
            { if (_expected[channel] == bib) { _expected[channel] = 0; } }
            _expected[0] = _held[0] ? 0 : bib;
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    public async Task MoveTimestampAsync(Guid listId, int bib, string key, TimingDecision expectedTarget,
        IReadOnlyList<string> expectedSourceKeys, string operatorName, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot?.ListId != listId) { throw new DomainValidationException("The active run changed. Drag the competitor again."); }
            var target = _snapshot.Observations.SingleOrDefault(x => x.Observation.Key == key)
                ?? throw new DomainValidationException("Choose a recorded timestamp.");
            var decision = new TimingDecision(DecisionKind.Assignment, key, Bib: bib);
            TimingEngine.ValidateDecision(decision, _snapshot);
            if (TimingEngine.CurrentDecision(decision, _audit) != expectedTarget)
            { throw new DomainValidationException("The timestamp assignment changed. Check it and drag again."); }
            var source = _snapshot.Observations.Where(x => x.Bib == bib && !x.Ignored && x.DuplicateOf is null
                && x.Observation.Kind == ObservationKind.Impulse && x.Observation.Channel == target.Observation.Channel).ToArray();
            var expectedKeys = _snapshot.Observations.Where(x => expectedSourceKeys.Contains(x.Observation.Key)
                && x.Observation.Channel == target.Observation.Channel).Select(x => x.Observation.Key).Order().ToArray();
            if (!source.Select(x => x.Observation.Key).Order().SequenceEqual(expectedKeys))
            { throw new DomainValidationException("The competitor received new timing while dragging. Check it and drag again."); }
            if (target.Bib == bib && !target.Ignored) { return; }
            var changes = source.Where(x => x.Observation.Key != key).Select(x =>
                new TimingAuditChange(TimingEngine.CurrentDecision(new(DecisionKind.Assignment, x.Observation.Key), _audit),
                    new(DecisionKind.Assignment, x.Observation.Key))).ToList();
            changes.Add(new(expectedTarget, decision));
            foreach (var change in changes) { TimingEngine.ValidateDecision(change.After, _snapshot); }
            var saved = await store.AppendTimingAuditBatchAsync(listId, _snapshot.AuditVersion, changes, operatorName,
                $"Drag timestamp to Bib {bib}; displaced timestamps remain unassigned", DateTimeOffset.UtcNow,
                startsRun: target.Observation.Channel == 0, ct: ct);
            _audit.AddRange(saved); Rebuild();
            foreach (var change in changes) { ReconcileCorrectedQueue(change.Before, change.After); }
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
            operatorName, reason, DateTimeOffset.UtcNow, reversesId,
            startsRun: decision.Kind == DecisionKind.Assignment && decision.Bib is not null
                && _snapshot.Observations.Any(x => x.Observation.Key == decision.ObservationKey && x.Observation.Channel == 0));
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

    // The complete order is one audited value, so a move and its undo cannot leave duplicate positions.
    public async Task MoveWaitingAsync(int bib, int direction, string operatorName, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (direction is not (-1 or 1) || _snapshot is null)
            { throw new DomainValidationException("Choose a waiting starter and a direction."); }
            var waiting = RaceFlow.Waiting(_snapshot).Select(x => x.Bib).ToArray();
            var index = Array.IndexOf(waiting, bib);
            var other = index + direction;
            if (index < 0 || other < 0 || other >= waiting.Length) { return; }
            var order = _snapshot.StartOrder.ToArray();
            var first = Array.IndexOf(order, bib);
            var second = Array.IndexOf(order, waiting[other]);
            (order[first], order[second]) = (order[second], order[first]);
            await AppendDecisionAsync(new(DecisionKind.StartOrder, StartOrder: string.Join(',', order)),
                operatorName, $"Move Bib {bib} in start order");
            _expected[0] = 0;
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    public async Task MoveWaitingToNextAsync(int bib, string operatorName, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot is null || !RaceFlow.Waiting(_snapshot).Any(x => x.Bib == bib))
            { throw new DomainValidationException("Choose a waiting starter."); }
            var order = _snapshot.StartOrder.ToList();
            order.Remove(bib);
            var next = RaceFlow.Waiting(_snapshot).FirstOrDefault(x => x.Bib != bib)?.Bib;
            order.Insert(next is null ? 0 : order.IndexOf(next.Value), bib);
            await AppendDecisionAsync(new(DecisionKind.StartOrder, StartOrder: string.Join(',', order)),
                operatorName, $"Move Bib {bib} to next start");
            _expected[0] = _held[0] ? 0 : bib;
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    public async Task MoveWaitingRelativeAsync(int bib, int targetBib, bool visuallyAbove,
        string operatorName, CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot is null || bib == targetBib) { return; }
            var waiting = RaceFlow.Waiting(_snapshot);
            if (!waiting.Any(x => x.Bib == bib) || !waiting.Any(x => x.Bib == targetBib))
            { throw new DomainValidationException("The start queue changed. Drag the competitor again."); }
            var order = _snapshot.StartOrder.ToList();
            order.Remove(bib);
            // At start is displayed in reverse: visually above means later in the starting order.
            order.Insert(order.IndexOf(targetBib) + (visuallyAbove ? 1 : 0), bib);
            await AppendDecisionAsync(new(DecisionKind.StartOrder, StartOrder: string.Join(',', order)),
                operatorName, $"Move Bib {bib} {(visuallyAbove ? "above" : "below")} Bib {targetBib} at start");
            _expected[0] = 0;
            AdvanceQueues();
        }
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

    public async Task HoldAllAsync(CancellationToken ct = default)
    {
        await _state.WaitAsync(ct);
        try
        {
            if (_snapshot is null || _list is null) { throw new DomainValidationException("Choose a timing run first."); }
            for (var channel = 0; channel < 2 + _list.Plan.Competition.IntermediateCount; channel++)
            { _expected[channel] = 0; _held[channel] = true; }
            AdvanceQueues();
        }
        finally { _state.Release(); }
    }

    private void ReconcileCorrectedQueue(TimingDecision before, TimingDecision after)
    {
        if (after.Kind == DecisionKind.StartOrder) { _expected[0] = 0; }
        if (!_followOrder && _snapshot is not null)
        {
            // Undoing a return-to-start can make the armed starter ineligible again.
            for (var channel = 0; channel < _expected.Length; channel++)
            { if (_expected[channel] != 0 && !RaceFlow.Expected(_snapshot, channel).Any(x => x.Bib == _expected[channel])) { _expected[channel] = 0; } }
        }
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
        var closeErrors = new List<string>();
        foreach (var source in _sources)
        {
            // Received data is already durable; a device that fails to close must not keep the others open.
            try { await source.DisposeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { closeErrors.Add(ex.Message); }
        }
        _sources = []; _producer = _writer = null;
        _readCancellation.Dispose(); _readCancellation = null;
        _connection = "Disconnected · received data saved"
            + (closeErrors.Count == 0 ? "" : " · a device did not close cleanly: " + string.Join("; ", closeErrors));
        _runningClock.Clear();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _state.Dispose();
    }
}
