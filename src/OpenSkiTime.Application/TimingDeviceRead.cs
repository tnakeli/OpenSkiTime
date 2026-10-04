using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// Operator-triggered read of a timing device for timing report evidence (B / hand clocks).
// Everything stays in memory: no capture session, raw packet or observation is written to the series file,
// and the read has no path to live A capture, B Clock comparison or race results.
public sealed class TimingDeviceRead : IAsyncDisposable
{
    public const int DefaultMaxPackets = 20_000;
    public const long DefaultMaxBytes = 16_000_000;
    private readonly ITimingSource _source;
    private readonly TimeProvider _clock;
    private readonly int _maxPackets;
    private readonly long _maxBytes;
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _gate = new();
    private readonly List<RawTimingPacket> _packets = [];
    private readonly AuxiliaryCaptureSession _session;
    private long _bytes;
    private Task? _completion;
    private string _status = "Connecting…";
    private string? _fault;
    private bool _truncated;
    private bool _disposed;

    public TimingDeviceRead(ITimingSource source, AuxiliaryTimingRole role, CaptureOptions options,
        TimeProvider? clock = null, int maxPackets = DefaultMaxPackets, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(options);
        AuxiliaryTimingValidation.Validate(role, options);
        if (maxPackets <= 0 || maxBytes <= 0) { throw new ArgumentOutOfRangeException(nameof(maxPackets)); }
        _source = source; _clock = clock ?? TimeProvider.System; _maxPackets = maxPackets; _maxBytes = maxBytes;
        var now = _clock.GetUtcNow();
        // A transient session only gives the existing decoders their capture context. It is never stored.
        _session = new(new CaptureSession(Guid.NewGuid(), Guid.Empty, options, now, null, CleanStop: true), role, Live: false);
    }

    public AuxiliaryTimingRole Role => _session.Role;
    public CaptureOptions Options => _session.Capture.Options;
    public Task Completion => _completion ?? Task.CompletedTask;
    public bool IsRunning => _completion is { IsCompleted: false };
    public string Status => Volatile.Read(ref _status);
    public string? Fault => Volatile.Read(ref _fault);
    public bool Truncated { get { lock (_gate) { return _truncated; } } }
    public int PacketCount { get { lock (_gate) { return _packets.Count; } } }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completion is not null) { throw new InvalidOperationException("A device read can be started only once."); }
        _completion = Task.Run(async () =>
        {
            try
            {
                await _source.ReceiveAsync(async packet =>
                {
                    ArgumentNullException.ThrowIfNull(packet);
                    var limit = false;
                    lock (_gate)
                    {
                        if (_truncated) { return; }
                        if (_packets.Count >= _maxPackets || _bytes + packet.Bytes.Length > _maxBytes)
                        {
                            _truncated = limit = true;
                            Volatile.Write(ref _status, "Read limit reached · stopped reading");
                        }
                        else
                        {
                            _bytes += packet.Bytes.Length;
                            _packets.Add(new(_session.Capture.Id, _packets.Count + 1, packet.ReceivedAt ?? _clock.GetUtcNow(),
                                packet.Protocol, packet.Source, packet.Stream, packet.Bytes.ToArray()));
                        }
                    }
                    // Bounded memory: stop the source instead of silently discarding later input.
                    if (limit) { await _cancel.CancelAsync(); }
                }, status => { if (!Truncated) { Volatile.Write(ref _status, status); } }, _cancel.Token);
                if (!_cancel.IsCancellationRequested) { Volatile.Write(ref _status, "Source completed"); }
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Volatile.Write(ref _fault, ex.Message);
                Volatile.Write(ref _status, "Device read failed · " + ex.Message);
            }
        });
    }

    public async Task StopAsync()
    {
        if (!_cancel.IsCancellationRequested) { await _cancel.CancelAsync(); }
        try { await Completion; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Volatile.Write(ref _fault, ex.Message); }
    }

    public IReadOnlyList<RawTimingPacket> Packets() { lock (_gate) { return _packets.ToArray(); } }

    // Uses the same decoders and role/channel mapping as stored auxiliary capture, on the in-memory packets.
    public IReadOnlyList<AuxiliaryTimingObservation> Decode(ITimingDecoderFactory decoders)
    {
        ArgumentNullException.ThrowIfNull(decoders);
        var session = Fault is null ? _session : _session with { Capture = _session.Capture with { CleanStop = false } };
        // Reading a device's memory for the timing report takes the chosen race day only, so the same time of day from
        // another day is never matched. Sources without a calendar date (Timy, MT1 serial) are unaffected.
        return new AuxiliaryTimingData(Guid.Empty, [session], Packets()).Decode(decoders)
            .Where(x => x.Observation.CalendarDate is not { } day || day == Options.DeviceDate).ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) { return; }
        _disposed = true;
        await StopAsync();
        try { await _source.DisposeAsync(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Volatile.Write(ref _fault, ex.Message); }
        _cancel.Dispose();
    }
}

public sealed record DeviceEvidenceSet(IReadOnlyList<EvidenceTimestamp> Evidence);

// Converts decoded device impulses to the evidence representation shared with receipt OCR.
public static class DeviceEvidence
{
    public const string KeyPrefix = "device";

    // Provenance identifies the source type and endpoint; the observation key identifies the decoded line.
    public static string Provenance(string sourceLabel, string endpoint)
    {
        ArgumentNullException.ThrowIfNull(sourceLabel); ArgumentNullException.ThrowIfNull(endpoint);
        return $"{KeyPrefix}:{sourceLabel}:{endpoint}";
    }

    // Full device precision is retained; the raw line text remains the evidence text for review.
    public static DeviceEvidenceSet ToEvidence(IReadOnlyList<AuxiliaryTimingObservation> observations, string provenance)
    {
        ArgumentNullException.ThrowIfNull(observations); ArgumentNullException.ThrowIfNull(provenance);
        var evidence = observations.Select(x => x.Observation)
            .Where(x => x.Kind == ObservationKind.Impulse && x.DeviceTicks is not null)
            .Select(x => new EvidenceTimestamp($"{provenance}:{x.Key}", x.DeviceTicks!.Value, x.Precision, x.Channel, x.Message)).ToArray();
        return new(evidence);
    }
}
