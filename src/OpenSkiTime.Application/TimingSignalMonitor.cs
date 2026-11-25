using System.Collections.Concurrent;

namespace OpenSkiTime.Application;

// Shows what the configured devices send while Settings → Timing devices is open, so wiring and channels can be checked
// without a run. Nothing is stored: no capture session, raw packet or observation reaches the series file, and the
// monitor has no path to race timing. Race impulses are received only by TimingWorkspace.
public sealed class TimingSignalMonitor : IAsyncDisposable
{
    private readonly ITimingDecoderFactory _decoders;
    private readonly CancellationTokenSource _cancel = new();
    private readonly ConcurrentDictionary<(string, int), TimingSignal> _signals = new();
    private readonly TimingSourceInput[] _inputs;
    private readonly string[] _statuses;
    private readonly Task _completion;

    public TimingSignalMonitor(IReadOnlyList<TimingSourceInput> inputs, ITimingDecoderFactory decoders)
    {
        ArgumentNullException.ThrowIfNull(inputs); ArgumentNullException.ThrowIfNull(decoders);
        _decoders = decoders;
        _inputs = inputs.ToArray();
        _statuses = Enumerable.Repeat("Connecting…", _inputs.Length).ToArray();
        _completion = Task.WhenAll(Enumerable.Range(0, _inputs.Length).Select(index => Task.Run(() => ReceiveAsync(index))));
    }

    public IReadOnlyList<CaptureOptions> Options => _inputs.Select(x => x.Options).ToArray();
    public IReadOnlyList<TimingSignal> Signals => _signals.Values.ToArray();
    public string Status => string.Join(" · ", _inputs.Select((x, i) => _inputs.Length == 1 ? Volatile.Read(ref _statuses[i])
        : $"{x.Options.Device}: {Volatile.Read(ref _statuses[i])}"));

    private async Task ReceiveAsync(int index)
    {
        var (source, options) = _inputs[index];
        // A transient session only gives the existing decoders their capture context. It is never stored.
        var session = new CaptureSession(Guid.NewGuid(), Guid.Empty, options, DateTimeOffset.UtcNow, null, CleanStop: true);
        var decoders = new StreamDecoders();
        var sequence = 0L;
        try
        {
            await source.ReceiveAsync(packet =>
            {
                var at = packet.ReceivedAt ?? DateTimeOffset.UtcNow;
                var raw = new RawTimingPacket(session.Id, ++sequence, at, packet.Protocol, packet.Source, packet.Stream, packet.Bytes);
                var decoder = decoders.For(_decoders, session, raw, []);
                TimingSignals.Record(_signals, options, TimingReplay.Decode(decoder, raw, includeInformation: true), at);
                return ValueTask.CompletedTask;
            }, status => Volatile.Write(ref _statuses[index], status), _cancel.Token);
            if (!_cancel.IsCancellationRequested) { Volatile.Write(ref _statuses[index], "Source completed"); }
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Volatile.Write(ref _statuses[index], "Not receiving · " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cancel.CancelAsync();
        await _completion;
        foreach (var input in _inputs)
        {
            try { await input.Source.DisposeAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* a device that fails to close must not keep the others open */ }
        }
        _cancel.Dispose();
    }
}
