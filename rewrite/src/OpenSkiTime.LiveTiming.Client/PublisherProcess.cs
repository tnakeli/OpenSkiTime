using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace OpenSkiTime.LiveTiming.Client;

// UI calls Offer: one atomic reference write, no device callback, no network or pipe I/O.
public sealed class PublisherProcess : IAsyncDisposable
{
    private readonly Channel<WorkerInput> _commands = Channel.CreateBounded<WorkerInput>(16);
    private readonly CancellationTokenSource _stop = new();
    private LiveSnapshot? _latest;
    private PublisherHealth _health = new(PublisherState.Stopped);
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private Task? _pump;
    private Task? _receive;
    private CancellationTokenSource? _connectionStop;
    private PublisherOptions? _options;
    private readonly string _localSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private int _disposed;
    public LiveSession? ResumeSession { get; set; }
    public PublisherHealth Health => Volatile.Read(ref _health);
    public int? ProcessId => _process is { HasExited: false } ? _process.Id : null;
    public void Offer(LiveSnapshot state) => Volatile.Write(ref _latest, state);
    public async Task StartAsync(string workerAssembly, PublisherOptions options, string? dotnetHost = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        workerAssembly = Path.GetFullPath(workerAssembly);
        if (options.LocalServerAssembly is { } serverAssembly)
        { options = options with { LocalServerAssembly = Path.GetFullPath(serverAssembly) }; }
        if (_options is not null && (_options.Kind != options.Kind || _options.Endpoint != options.Endpoint
            || _options.FisPassword != options.FisPassword || _options.TcpPort != options.TcpPort))
        { throw new LiveValidationException("Stop and reset the publisher before changing its transport settings."); }
        options = options with { ResumeSession = ResumeSession, LocalSigningKey = _localSigningKey };
        _options = options;
        if (_pump is null || _pump.IsCompleted || _process?.HasExited == true || _receive?.IsCompleted == true)
        {
            _connectionStop?.Cancel();
            if (_process is not null) { if (!_process.HasExited) { _process.Kill(true); } _process.Dispose(); }
            _pipe?.Dispose();
            try { if (_pump is not null) { await _pump.WaitAsync(TimeSpan.FromSeconds(2), ct); } }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException) { }
            _connectionStop?.Dispose(); _connectionStop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            while (_commands.Reader.TryRead(out _)) { } // Commands for the failed generation cannot mutate the replacement.
            var pipeName = "OpenSkiTime-live-" + Guid.NewGuid().ToString("N");
            _pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var executable = Path.ChangeExtension(workerAssembly, OperatingSystem.IsWindows() ? ".exe" : null);
            var useAppHost = dotnetHost is null && File.Exists(executable);
            var host = dotnetHost ?? ResolveDotnetHost();
            var start = new ProcessStartInfo(useAppHost ? executable : host)
            { UseShellExecute = false, CreateNoWindow = true };
            if (!useAppHost) { start.ArgumentList.Add(workerAssembly); start.Environment["DOTNET_HOST_PATH"] = host; }
            start.ArgumentList.Add("--pipe"); start.ArgumentList.Add(pipeName);
            _health = new(PublisherState.Starting, options.Endpoint);
            try
            {
                _process = Process.Start(start) ?? throw new IOException("Live timing worker failed to start.");
                await _pipe.WaitForConnectionAsync(ct).WaitAsync(TimeSpan.FromSeconds(15), ct);
                _pump = PumpAsync(_pipe, _connectionStop.Token);
                _receive = ReceiveAsync(_pipe, _connectionStop.Token);
            }
            catch
            {
                _health = new(PublisherState.Error, options.Endpoint, Error: "Live timing worker could not start. Check installed artifacts.");
                _pipe.Dispose(); if (_process is { HasExited: false }) { _process.Kill(true); }
                throw;
            }
        }
        await CommandAsync(new WorkerInput("start", options, _latest), ct);
    }
    public ValueTask CommandAsync(string command, CancellationToken ct = default) => CommandAsync(new WorkerInput(command, Snapshot: _latest), ct);
    private ValueTask CommandAsync(WorkerInput input, CancellationToken ct)
    {
        if (_pump is null || _pump.IsCompleted) { throw new IOException("Live timing worker is stopped. Press Start."); }
        return _commands.Writer.WriteAsync(input, ct);
    }
    private async Task PumpAsync(Stream pipe, CancellationToken ct)
    {
        await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
        LiveSnapshot? sent = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (_commands.Reader.TryRead(out var command)) { await writer.WriteLineAsync(JsonSerializer.Serialize(command, LiveJson.Options).AsMemory(), ct); }
                var latest = Volatile.Read(ref _latest);
                if (latest is not null && !ReferenceEquals(latest, sent))
                { await writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerInput("state", Snapshot: latest), LiveJson.Options).AsMemory(), ct); sent = latest; }
                await Task.Delay(100, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        { if (!ct.IsCancellationRequested) { _health = Health with { State = PublisherState.Error, Error = "Live timing process disconnected. Press Start to restart." }; } }
    }
    private async Task ReceiveAsync(Stream pipe, CancellationToken ct)
    {
        using var reader = new StreamReader(pipe, leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 8192) { throw new IOException("Invalid worker status."); }
                var status = JsonSerializer.Deserialize<WorkerOutput>(line, LiveJson.Options);
                if (status is not null) { ResumeSession = status.Session; Volatile.Write(ref _health, status.Health); }
            }
            _health = Health with { State = PublisherState.Error, Error = "Live timing process exited. Press Start to restart." };
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or JsonException)
        { if (!ct.IsCancellationRequested) { _health = Health with { State = PublisherState.Error, Error = "Live timing health channel disconnected." }; } }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed,1) != 0) { return; }
        _stop.Cancel(); _pipe?.Dispose(); _commands.Writer.TryComplete();
        if (_process is not null) { if (!_process.HasExited) { _process.Kill(true); } _process.Dispose(); }
        try { if (_pump is not null) { await _pump; } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        try { if (_receive is not null) { await _receive; } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
        _connectionStop?.Dispose(); _stop.Dispose();
    }
    private static string ResolveDotnetHost()
    {
        var explicitHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(explicitHost)) { return explicitHost; }
        var runtime = new DirectoryInfo(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
        var root = runtime.Parent?.Parent?.Parent;
        var path = root is null ? "" : Path.Combine(root.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(path) ? path : "dotnet";
    }
}
