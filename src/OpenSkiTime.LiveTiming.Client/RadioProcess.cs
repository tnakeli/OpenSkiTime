using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenSkiTime.LiveTiming.Client;

/// <summary>
/// The optional race radio worker, owned by the control panel like the publisher workers: private current-user pipe,
/// bounded command queue, latest snapshot by reference, and part of the panel's kill-on-close job on Windows.
/// </summary>
public sealed class RadioProcess : IAsyncDisposable
{
    private readonly Channel<RadioWorkerInput> _commands = Channel.CreateBounded<RadioWorkerInput>(16);
    private readonly CancellationTokenSource _stop = new();
    private LiveSnapshot? _latest;
    private RadioHealth _health = new(RadioState.Stopped);
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private Task? _pump;
    private Task? _receive;
    private CancellationTokenSource? _connectionStop;
    private int _disposed;
    public RadioHealth Health => Volatile.Read(ref _health);
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    public bool IsConnected => _pump is { IsCompleted: false } && _receive is { IsCompleted: false };
    public void Offer(LiveSnapshot state) => Volatile.Write(ref _latest, state);

    public async Task StartAsync(string workerAssembly, RadioWorkerOptions options, RadioTarget[] targets, string? dotnetHost = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(targets);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsConnected)
        {
            // The previous generation's pump and receiver end before a replacement can take commands from the queue.
            _connectionStop?.Cancel();
            if (_process is not null) { if (!_process.HasExited) { _process.Kill(true); } _process.Dispose(); _process = null; }
            _pipe?.Dispose();
            try { if (_pump is not null) { await _pump.WaitAsync(TimeSpan.FromSeconds(2), ct); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException) { }
            _connectionStop?.Dispose();
            _connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var pipeName = "OpenSkiTime-radio-" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            Volatile.Write(ref _health, new RadioHealth(RadioState.Starting, BudgetUsd: options.Configuration.BudgetUsd, SpentUsd: options.SpentUsd));
            try
            {
                _process = Process.Start(WorkerLaunch.Create(Path.GetFullPath(workerAssembly), pipeName, dotnetHost)) ?? throw new IOException("Radio worker failed to start.");
                await _pipe.WaitForConnectionAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
                // Disposed while starting (competition changed or panel closing): never leave an orphan worker behind.
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                while (_commands.Reader.TryRead(out _)) { } // Commands for a failed generation cannot reach the replacement.
                _pump = PumpAsync(_pipe, _connectionStop.Token);
                _receive = ReceiveAsync(_pipe, _connectionStop);
            }
            catch
            {
                Volatile.Write(ref _health, new RadioHealth(RadioState.Error, Error: "Radio worker could not start. Check installed components."));
                _pipe.Dispose(); if (_process is { HasExited: false }) { _process.Kill(true); }
                throw;
            }
        }
        await _commands.Writer.WriteAsync(new("start", options, Volatile.Read(ref _latest), targets), ct);
    }
    public ValueTask TargetsAsync(RadioTarget[] targets, CancellationToken ct = default)
        => IsConnected ? _commands.Writer.WriteAsync(new("targets", Targets: targets), ct) : ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken ct = default)
        => IsConnected ? _commands.Writer.WriteAsync(new("stop", Targets: []), ct) : ValueTask.CompletedTask;

    private async Task PumpAsync(Stream pipe, CancellationToken ct)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        LiveSnapshot? sent = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (_commands.Reader.TryRead(out var command))
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(command, LiveJson.Options).AsMemory(), ct);
                    if (command.Snapshot is not null) { sent = command.Snapshot; }
                }
                var latest = Volatile.Read(ref _latest);
                if (latest is not null && !ReferenceEquals(latest, sent))
                { await writer.WriteLineAsync(JsonSerializer.Serialize(new RadioWorkerInput("state", Snapshot: latest), LiveJson.Options).AsMemory(), ct); sent = latest; }
                await Task.Delay(200, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ReceiveAsync(Stream pipe, CancellationTokenSource connection)
    {
        var ct = connection.Token;
        using var reader = new StreamReader(pipe, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 16384) { throw new IOException("Invalid radio status."); }
                if (JsonSerializer.Deserialize<RadioWorkerOutput>(line, LiveJson.Options) is { } status) { Volatile.Write(ref _health, status.Health); }
            }
            Volatile.Write(ref _health, Health with { State = RadioState.Error, Error = "Radio worker exited. Change a radio setting or restart publishing to retry." });
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        { if (!ct.IsCancellationRequested) { Volatile.Write(ref _health, Health with { State = RadioState.Error, Error = "Radio worker disconnected." }); } }
        // The status channel is gone: the command pump of this generation ends too.
        finally { try { connection.Cancel(); } catch (ObjectDisposedException) { } }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        // A graceful shutdown lets the worker hide the viewer controls before it exits; a forced exit relies on the
        // server's heartbeat timeout instead.
        try
        {
            if (IsConnected && _process is { } process)
            {
                await _commands.Writer.WriteAsync(new("shutdown"));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                // The last status line carries the final spend; let the receiver read it before anything is cancelled.
                if (_receive is not null) { await _receive.WaitAsync(TimeSpan.FromSeconds(2)); }
            }
        }
        catch (Exception ex) when (ex is ChannelClosedException or OperationCanceledException or TimeoutException or InvalidOperationException) { }
        _stop.Cancel(); _commands.Writer.TryComplete();
        try { if (_pump is not null) { await _pump.WaitAsync(TimeSpan.FromSeconds(2)); } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or TimeoutException) { }
        _pipe?.Dispose();
        if (_process is not null) { if (!_process.HasExited) { _process.Kill(true); } _process.Dispose(); }
        try { if (_receive is not null) { await _receive; } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        _connectionStop?.Dispose(); _stop.Dispose();
    }
}
