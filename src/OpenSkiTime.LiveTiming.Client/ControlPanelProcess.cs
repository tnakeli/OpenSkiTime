using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenSkiTime.LiveTiming.Client;

public sealed class ControlPanelProcess : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ControlPanelInput> _commands = Channel.CreateBounded<ControlPanelInput>(16);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ControlPanelOutput>> _requests = new();
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private OwnedProcessJob? _job;
    private Task? _pump;
    private Task? _receive;
    private ControlPanelInput? _latest;
    private int _disposed;
    private ControlPanelOutput _state = new(Guid.Empty, []);
    public ControlPanelOutput State => Volatile.Read(ref _state);
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    public bool IsConnected => _pipe?.IsConnected == true && _receive?.IsCompleted == false;
    public Func<LiveConnectionSettings, Task<ControlPanelInput>>? PrepareSnapshot { get; set; }
    public void Offer(Guid competitionId, LiveSnapshot snapshot) => Volatile.Write(ref _latest, new(Guid.Empty, ControlPanelAction.State, competitionId, snapshot));
    public void ClearSnapshot() => Volatile.Write(ref _latest, null);

    public async Task StartAsync(string executable, LiveConnectionSettings settings, CancellationToken ct = default)
    {
        if (_process is not null) { throw new InvalidOperationException("Panel process already started."); }
        var pipeName = "OpenSkiTime-panel-" + Guid.NewGuid().ToString("N");
        _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
        try
        {
            _process = Process.Start(start) ?? throw new IOException("Could not start Live timing control panel.");
            _job = new OwnedProcessJob(_process);
            await _pipe.WaitForConnectionAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
            _pump = PumpAsync(_pipe);
            _receive = ReceiveAsync(_pipe);
            await RequestAsync(new(Guid.NewGuid(), ControlPanelAction.State, Settings: settings), ct);
        }
        catch { await DisposeAsync(); throw; }
    }
    public async Task<ControlPanelOutput> RequestAsync(ControlPanelInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!IsConnected) { throw new IOException("Live timing control panel is closed. Reopen it."); }
        var id = input.RequestId == Guid.Empty ? Guid.NewGuid() : input.RequestId;
        var completion = new TaskCompletionSource<ControlPanelOutput>(TaskCreationOptions.RunContinuationsAsynchronously);
        _requests[id] = completion;
        try
        {
            await _commands.Writer.WriteAsync(input with { RequestId = id }, ct);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        finally { _requests.TryRemove(id, out _); }
    }
    private async Task PumpAsync(Stream pipe)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        ControlPanelInput? sent = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                while (_commands.Reader.TryRead(out var command)) { await writer.WriteLineAsync(JsonSerializer.Serialize(command, LiveJson.Options).AsMemory(), _stop.Token); }
                var latest = Volatile.Read(ref _latest);
                if (latest is not null && !ReferenceEquals(latest, sent))
                { await writer.WriteLineAsync(JsonSerializer.Serialize(latest, LiveJson.Options).AsMemory(), _stop.Token); sent = latest; }
                await Task.Delay(100, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ReceiveAsync(Stream pipe)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length > 65536) { throw new IOException("Invalid control panel status."); }
                var status = JsonSerializer.Deserialize<ControlPanelOutput>(line, LiveJson.Options) ?? throw new IOException("Missing panel status.");
                Volatile.Write(ref _state, status);
                if (status.SnapshotRequestId is { } snapshotId && status.RequestedSettings is { } settings)
                { _ = PrepareAsync(snapshotId, settings); }
                if (_requests.TryGetValue(status.RequestId, out var request)) { request.TrySetResult(status); }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { }
        finally
        {
            _stop.Cancel();
            _job?.Dispose();
            Volatile.Write(ref _state, new(Guid.Empty, State.Channels.Select(x => x with { Health = x.Health with { State = PublisherState.Stopped }, ProcessId = null }).ToArray()));
            foreach (var request in _requests.Values) { request.TrySetException(new IOException("Live timing control panel closed.")); }
        }
    }
    private async Task PrepareAsync(Guid id, LiveConnectionSettings settings)
    {
        ControlPanelInput reply;
        try
        {
            reply = PrepareSnapshot is { } prepare ? (await prepare(settings)) with { RequestId = id }
                : new(id, ControlPanelAction.State, PreparationError: "Timing state unavailable.");
        }
        catch (Exception ex) when (ex is IOException or LiveValidationException or InvalidOperationException or ArgumentException or TimeZoneNotFoundException)
        { reply = new(id, ControlPanelAction.State, PreparationError: "Check the selected timing run and race time zone."); }
        try { await _commands.Writer.WriteAsync(reply, _stop.Token); }
        catch (Exception ex) when (ex is OperationCanceledException or ChannelClosedException) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
        _stop.Cancel(); _pipe?.Dispose(); _job?.Dispose();
        _commands.Writer.TryComplete();
        if (_process is { HasExited: false }) { _process.Kill(true); }
        try { if (_pump is not null) { await _pump; } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        try { if (_receive is not null) { await _receive; } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
        _process?.Dispose(); _process = null; _pipe = null; _job = null;
    }
}
