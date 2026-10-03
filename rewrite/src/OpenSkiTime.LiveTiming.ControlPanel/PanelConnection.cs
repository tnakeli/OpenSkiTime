using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.ControlPanel;

public sealed class PanelConnection(string pipeName, LiveControlSession session) : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _writes = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ControlPanelInput>> _preparations = new();
    private StreamWriter? _writer;
    public event Action<ControlPanelInput>? Received;
    public event Action? Disconnected;

    public async Task RunAsync()
    {
        try
        {
            await _pipe.ConnectAsync(15000, _stop.Token);
            _writer = new StreamWriter(_pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(_pipe, leaveOpen: true);
            var status = StatusAsync();
            try
            {
                while (await reader.ReadLineAsync(_stop.Token) is { } line)
                {
                    if (line.Length > 2 * 1024 * 1024) { throw new IOException("Oversized control input."); }
                    var input = JsonSerializer.Deserialize<ControlPanelInput>(line, LiveJson.Options) ?? throw new IOException("Missing control input.");
                    if (_preparations.TryRemove(input.RequestId, out var pending)) { pending.TrySetResult(input); continue; }
                    var state = await session.ApplyAsync(input);
                    Received?.Invoke(input);
                    await SendAsync(state);
                }
            }
            finally { _stop.Cancel(); await status; }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or JsonException or ObjectDisposedException) { }
        finally
        {
            foreach (var pending in _preparations.Values) { pending.TrySetException(new IOException("OpenSkiTime disconnected.")); }
            await session.DisposeAsync();
            Disconnected?.Invoke();
        }
    }
    private async Task StatusAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                session.RetainCredentials();
                await SendAsync(session.ReadState());
                await Task.Delay(250, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    public async Task<ControlPanelInput> PrepareAsync(LiveConnectionSettings settings)
    {
        var id = Guid.NewGuid();
        var completion = new TaskCompletionSource<ControlPanelInput>(TaskCreationOptions.RunContinuationsAsynchronously);
        _preparations[id] = completion;
        try
        {
            await SendAsync(session.ReadState() with { SnapshotRequestId = id, RequestedSettings = settings });
            var input = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15), _stop.Token);
            if (input.PreparationError is { } error) { throw new LiveValidationException(error); }
            return input;
        }
        finally { _preparations.TryRemove(id, out _); }
    }
    private async Task SendAsync(ControlPanelOutput output)
    {
        await _writes.WaitAsync(_stop.Token);
        try { if (_writer is { } writer) { await writer.WriteLineAsync(JsonSerializer.Serialize(output, LiveJson.Options).AsMemory(), _stop.Token); } }
        finally { _writes.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel(); _pipe.Dispose();
        await session.DisposeAsync();
    }
}
