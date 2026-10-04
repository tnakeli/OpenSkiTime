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
        return new AuxiliaryTimingData(Guid.Empty, [session], Packets()).Decode(decoders);
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

public sealed record DeviceEvidenceSet(IReadOnlyList<EvidenceTimestamp> Evidence, IReadOnlyList<string> Warnings, int ShiftedCount);

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

    // The explicit UTC offset is applied through AuxiliaryClockComparison to derived comparison values only.
    // Full device precision is retained; the raw line text remains the evidence text for review.
    public static DeviceEvidenceSet ToEvidence(IReadOnlyList<AuxiliaryTimingObservation> observations, bool targetUsesUtc, string provenance)
    {
        ArgumentNullException.ThrowIfNull(observations); ArgumentNullException.ThrowIfNull(provenance);
        var normalized = AuxiliaryClockComparison.Normalize(targetUsesUtc, observations);
        var original = observations.Select(x => x.Observation).Where(x => x.DeviceTicks is not null)
            .GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First().DeviceTicks!.Value, StringComparer.Ordinal);
        var shifted = 0;
        var evidence = normalized.Observations.Select(x =>
        {
            var moved = original.TryGetValue(x.Key, out var ticks) && ticks != x.DeviceTicks;
            if (moved) { shifted++; }
            var offset = moved ? (x.DeviceTicks!.Value - ticks) / TimeSpan.TicksPerMinute : 0;
            var text = moved ? $"{x.Message} · compared with explicit UTC offset {(offset >= 0 ? "+" : "")}{offset} min" : x.Message;
            return new EvidenceTimestamp($"{provenance}:{x.Key}", x.DeviceTicks!.Value, x.Precision, x.Channel, text);
        }).ToArray();
        return new(evidence, normalized.Warnings.Count == 0 ? [] :
            ["Device and A timestamps use different clock bases (UTC and local clock). Enter the local clock UTC offset and read again, or enter verified values manually."],
            shifted);
    }
}
