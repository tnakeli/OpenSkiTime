using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public enum AuxiliaryTimingRole { B, HandStart, HandFinish }

public sealed record AuxiliaryCaptureSession(CaptureSession Capture, AuxiliaryTimingRole Role, bool Live);
public sealed record AuxiliaryTimingObservation(AuxiliaryTimingRole Role, bool Live, TimingObservation Observation)
{
    public DateTimeOffset? ReceivedAt { get; init; }
    public int? ComparisonUtcOffsetMinutes { get; init; }
}
public sealed record AuxiliaryTimingData(Guid ListId, IReadOnlyList<AuxiliaryCaptureSession> Sessions,
    IReadOnlyList<RawTimingPacket> Packets)
{
    public IReadOnlyList<AuxiliaryTimingObservation> Decode(ITimingDecoderFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var result = new List<AuxiliaryTimingObservation>();
        foreach (var session in Sessions)
        {
            AuxiliaryTimingValidation.Validate(session.Role, session.Capture.Options);
            var decoders = new Dictionary<string, ITimingDecoder>(StringComparer.Ordinal);
            foreach (var packet in Packets.Where(x => x.SessionId == session.Capture.Id).OrderBy(x => x.Sequence))
            {
                var key = $"{packet.Protocol}:{packet.Source}:{packet.Stream}";
                if (!decoders.TryGetValue(key, out var decoder))
                { decoder = factory.Create(session.Capture, packet.Protocol, packet.Source, packet.Stream); decoders.Add(key, decoder); }
                result.AddRange(TimingReplay.Decode(decoder, packet).Select(x => Wrap(session, x) with { ReceivedAt = packet.ReceivedAt }));
            }
            foreach (var decoder in decoders.Values) { result.AddRange(decoder.Complete().Select(x => Wrap(session, x))); }
            if (!session.Capture.CleanStop)
            {
                result.Add(Wrap(session, new($"{session.Capture.Id:N}:interrupted", session.Capture.Id, 0,
                    session.Capture.Options.Endpoint, $"interrupted:{session.Capture.Id:N}", ObservationKind.Invalid,
                    null, null, 0, null, false, "", "Auxiliary capture did not close cleanly. Review device memory for missing evidence.")));
            }
        }
        return result;
    }

    internal static AuxiliaryTimingObservation Wrap(AuxiliaryCaptureSession session, TimingObservation observation)
    {
        // A hand source may map one physical channel to both options. Its role fixes the timing position.
        if (session.Role != AuxiliaryTimingRole.B && observation.Channel is 0 or 1)
        { observation = observation with { Channel = session.Role == AuxiliaryTimingRole.HandStart ? 0 : 1 }; }
        return new(session.Role, session.Live, observation) { ComparisonUtcOffsetMinutes = session.Capture.Options.ComparisonUtcOffsetMinutes };
    }
}

public static class AuxiliaryTimingValidation
{
    public static void Validate(AuxiliaryTimingRole role, CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(role)) { throw new DomainValidationException("Choose B, hand start or hand finish."); }
        if (options.ComparisonUtcOffsetMinutes is < -840 or > 840)
        { throw new DomainValidationException("The local clock UTC offset must be between -840 and 840 minutes."); }
        // Separate hand clocks need only a single mapped channel; the decoder still uses the original options.
        var validation = role != AuxiliaryTimingRole.B && options.StartChannel == options.FinishChannel
            ? options with { FinishChannel = (options.StartChannel + 1) % 9, IntermediateChannels = [] } : options;
        validation.Validate();
        if (options.IntermediateChannels is null || options.IntermediateChannels.Length != 0)
        { throw new DomainValidationException("Auxiliary report capture uses start and finish channels only."); }
    }
}

public interface IAuxiliaryTimingStore
{
    Task<AuxiliaryTimingData> ReadAuxiliaryTimingAsync(Guid listId, CancellationToken ct = default);
    Task<AuxiliaryCaptureSession> BeginAuxiliaryCaptureAsync(Guid listId, AuxiliaryTimingRole role,
        CaptureOptions options, string operatorName, DateTimeOffset at, bool live, CancellationToken ct = default);
    Task AppendAuxiliaryRawAsync(RawTimingPacket packet, CancellationToken ct = default);
    Task EndAuxiliaryCaptureAsync(Guid sessionId, DateTimeOffset at, CancellationToken ct = default);
    Task<AuxiliaryCaptureSession> SwitchAuxiliaryCaptureAsync(Guid sessionId, Guid listId, DateTimeOffset at,
        CancellationToken ct = default) => throw new NotSupportedException("This auxiliary store cannot switch runs during capture.");
}

public sealed record AuxiliaryCaptureState(AuxiliaryTimingRole Role, Guid? ListId, bool IsActive, bool Live,
    string Connection, string? Fault, int Pending, long SavedPackets, IReadOnlyList<AuxiliaryTimingObservation> Observations)
{
    public Guid? SessionId { get; init; }
    public CaptureOptions? Options { get; init; }
}

// Auxiliary input has no path to TimingEngine, race assignments or result publication.
public sealed class AuxiliaryTimingWorkspace(IAuxiliaryTimingStore store, ITimingDecoderFactory decoders,
    TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<AuxiliaryTimingRole, Capture> _captures = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    public bool IsActive => _captures.Values.Any(x => x.IsActive);
    public AuxiliaryCaptureState State(AuxiliaryTimingRole role) => _captures.TryGetValue(role, out var capture)
        ? capture.State : new(role, null, false, false, "Disconnected", null, 0, 0, []);
    public Task<AuxiliaryTimingData> ReadAsync(Guid listId, CancellationToken ct = default)
        => store.ReadAuxiliaryTimingAsync(listId, ct);

    public async Task StartAsync(Guid listId, AuxiliaryTimingRole role, ITimingSource source, CaptureOptions options,
        string operatorName, bool live = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        AuxiliaryTimingValidation.Validate(role, options);
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (_captures.TryGetValue(role, out var old) && old.IsActive)
            { throw new DomainValidationException("Disconnect and drain this auxiliary source before reconnecting it."); }
            var session = await store.BeginAuxiliaryCaptureAsync(listId, role, options, operatorName, _clock.GetUtcNow(), live, ct);
            var capture = new Capture(store, decoders, _clock, session, source);
            _captures[role] = capture;
            capture.Start();
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync(AuxiliaryTimingRole role)
    {
        await _lifecycle.WaitAsync();
        try { if (_captures.TryGetValue(role, out var capture)) { await capture.StopAsync(); } }
        finally { _lifecycle.Release(); }
    }

    public async Task SwitchRunAsync(AuxiliaryTimingRole role, Guid listId, CancellationToken ct = default)
    {
        Capture? capture;
        await _lifecycle.WaitAsync(ct);
        try
        {
            _captures.TryGetValue(role, out capture);
        }
        finally { _lifecycle.Release(); }
        // A fragmented device message may need later input. Never retain the lifecycle lock while waiting:
        // stopping the source must still drain it and resolve a pending switch.
        if (capture?.IsActive == true) { await capture.SwitchRunAsync(listId, ct); }
    }

    public async Task StopAllAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            // Signal every source before waiting, including when one source requires storage recovery.
            foreach (var capture in _captures.Values) { capture.RequestStop(); }
            await Task.WhenAll(_captures.Values.Select(x => x.StopAsync()));
        }
        finally { _lifecycle.Release(); }
    }

    public void RetryStorage(AuxiliaryTimingRole role)
    { if (_captures.TryGetValue(role, out var capture)) { capture.RetryStorage(); } }
    public async ValueTask DisposeAsync() { await StopAllAsync(); _lifecycle.Dispose(); }

    private sealed class Capture(IAuxiliaryTimingStore store, ITimingDecoderFactory factory,
        TimeProvider clock, AuxiliaryCaptureSession session, ITimingSource source) : IAsyncDisposable
    {
        private sealed record RunChange(Guid ListId, TaskCompletionSource Completion);
        private sealed record CaptureInput(TransportPacket? Packet = null, RunChange? Change = null);
        private readonly Channel<CaptureInput> _queue = Channel.CreateBounded<CaptureInput>(
            new BoundedChannelOptions(2048) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        private readonly CancellationTokenSource _cancel = new();
        private readonly Dictionary<string, ITimingDecoder> _decoders = new(StringComparer.Ordinal);
        private readonly List<AuxiliaryTimingObservation> _observations = [];
        private readonly object _viewGate = new();
        private AuxiliaryTimingObservation[] _shown = [];
        private Task? _producer;
        private Task? _writer;
        private TaskCompletionSource _retry = NewSignal();
        private TaskCompletionSource _failed = NewSignal();
        private string _connection = "Connecting…";
        private string? _fault;
        private int _pending;
        private long _saved;
        public bool IsActive => _producer is not null;
        public AuxiliaryCaptureState State
        {
            get
            {
                lock (_viewGate)
                {
                    return new(session.Role, session.Capture.ListId, IsActive, session.Live,
                        Volatile.Read(ref _connection), Volatile.Read(ref _fault), Volatile.Read(ref _pending),
                        Interlocked.Read(ref _saved), Volatile.Read(ref _shown))
                        { SessionId = session.Capture.Id, Options = session.Capture.Options };
                }
            }
        }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Start()
        {
            _writer = Task.Run(async () =>
            {
                try { await WriteAsync(); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _fault = "Auxiliary processing stopped. Keep this file open and preserve device memory for recovery.";
                    _failed.TrySetResult(); _cancel.Cancel(); throw;
                }
            });
            _producer = Task.Run(async () =>
            {
                try
                {
                    await source.ReceiveAsync(async packet =>
                    {
                        if (packet.Bytes.Length > 4_000_000) { throw new IOException("Device frame exceeds the capture size limit."); }
                        Interlocked.Increment(ref _pending);
                        await _queue.Writer.WriteAsync(new(Packet: packet with
                        { Bytes = packet.Bytes.ToArray(), ReceivedAt = packet.ReceivedAt ?? clock.GetUtcNow() }));
                    }, status => Volatile.Write(ref _connection, status), _cancel.Token);
                    if (!_cancel.IsCancellationRequested) { _connection = "Source completed · disconnect to finish capture"; }
                }
                catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _connection = "Auxiliary source disconnected · check its connection";
                    Interlocked.Increment(ref _pending);
                    await _queue.Writer.WriteAsync(new(Packet: new("transport-status", session.Capture.Options.Endpoint, "failure",
                        Encoding.UTF8.GetBytes("Auxiliary transport interrupted. Check device history for missing evidence."), clock.GetUtcNow())));
                }
                finally { _queue.Writer.TryComplete(); }
            });
        }

        private async Task WriteAsync()
        {
            long sequence = 0;
            RunChange? change = null;
            await foreach (var input in _queue.Reader.ReadAllAsync())
            {
                if (input.Change is { } requested)
                {
                    if (change is null) { change = requested; }
                    else { requested.Completion.TrySetException(new SeriesFileException("An auxiliary run change is already waiting for the current device message.")); }
                }
                if (input.Packet is { } packet)
                {
                    var raw = new RawTimingPacket(session.Capture.Id, ++sequence, packet.ReceivedAt ?? clock.GetUtcNow(),
                        packet.Protocol, packet.Source, packet.Stream, packet.Bytes);
                    await DurableAsync(() => store.AppendAuxiliaryRawAsync(raw));
                    Interlocked.Increment(ref _saved); Interlocked.Decrement(ref _pending);
                    var key = $"{packet.Protocol}:{packet.Source}:{packet.Stream}";
                    if (!_decoders.TryGetValue(key, out var decoder))
                    { decoder = factory.Create(session.Capture, packet.Protocol, packet.Source, packet.Stream); _decoders.Add(key, decoder); }
                    _observations.AddRange(TimingReplay.Decode(decoder, raw).Select(x => AuxiliaryTimingData.Wrap(session, x) with { ReceivedAt = raw.ReceivedAt }));
                    Volatile.Write(ref _shown, _observations.ToArray());
                }
                if (change is not null && !_decoders.Values.Any(x => x.HasPendingInput))
                {
                    try
                    {
                        AuxiliaryCaptureSession? next = null;
                        var previousId = session.Capture.Id; var nextListId = change.ListId; var changedAt = clock.GetUtcNow();
                        await DurableAsync(async () => { next = await store.SwitchAuxiliaryCaptureAsync(previousId, nextListId, changedAt); });
                        _decoders.Clear(); _observations.Clear(); sequence = 0;
                        lock (_viewGate)
                        {
                            session = next!;
                            Interlocked.Exchange(ref _saved, 0); Volatile.Write(ref _shown, []);
                        }
                        change.Completion.TrySetResult();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { change.Completion.TrySetException(ex); }
                    finally { change = null; }
                }
            }
            change?.Completion.TrySetException(new SeriesFileException("The auxiliary source stopped partway through a message; its run was not changed."));
            foreach (var decoder in _decoders.Values)
            { _observations.AddRange(decoder.Complete().Select(x => AuxiliaryTimingData.Wrap(session, x))); }
            Volatile.Write(ref _shown, _observations.ToArray());
            await DurableAsync(() => store.EndAuxiliaryCaptureAsync(session.Capture.Id, clock.GetUtcNow()));
        }

        public async Task SwitchRunAsync(Guid listId, CancellationToken ct)
        {
            if (session.Capture.ListId == listId) { return; }
            if (!session.Live) { throw new DomainValidationException("Temporary evidence imports remain in their selected run."); }
            var producer = _producer; var writer = _writer;
            if (producer is null || writer is null || producer.IsCompleted || _fault is not null)
            { throw new SeriesFileException("Check the auxiliary source before changing its timing run."); }
            var change = new RunChange(listId, NewSignal());
            try { await _queue.Writer.WriteAsync(new(Change: change), ct); }
            catch (ChannelClosedException ex) { throw new SeriesFileException("The auxiliary source stopped before its run could change.", ex); }
            if (await Task.WhenAny(change.Completion.Task, writer) != change.Completion.Task)
            { throw new SeriesFileException("The auxiliary source stopped before its run could change."); }
            await change.Completion.Task;
        }

        private async Task DurableAsync(Func<Task> write)
        {
            while (true)
            {
                try { await write(); return; }
                catch (Exception ex) when (ex is SeriesFileException or IOException or UnauthorizedAccessException)
                {
                    _retry = NewSignal();
                    _fault = "AUXILIARY INPUT NOT SAVED. Check storage, then retry. Keep this file open.";
                    _failed.TrySetResult();
                    await _retry.Task;
                }
            }
        }

        public void RetryStorage()
        {
            if (_writer?.IsFaulted == true) { return; }
            _failed = NewSignal(); _fault = null; _retry.TrySetResult();
        }
        public void RequestStop() { if (IsActive) { _cancel.Cancel(); } }
        public async Task StopAsync()
        {
            if (_producer is null) { return; }
            RequestStop();
            if (_fault is not null) { throw new SeriesFileException(_fault); }
            var drain = Task.WhenAll(_producer, _writer!);
            if (await Task.WhenAny(drain, _failed.Task, Task.Delay(TimeSpan.FromSeconds(15))) != drain)
            { throw new SeriesFileException(_fault ?? "Auxiliary capture is still draining. Keep the file open and retry disconnect."); }
            await drain;
            await source.DisposeAsync();
            _producer = _writer = null;
            _cancel.Dispose();
            _connection = "Disconnected · received data saved";
        }
        public ValueTask DisposeAsync() => new(StopAsync());
    }
}
