using System.IO.Pipes;
using System.Text.Json;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Radio;
using OpenSkiTime.LiveTiming.Radio.Providers;

// Optional race radio. Owned by the live timing control panel through a private current-user pipe and its kill-on-close
// job. It reads only the read-only live projection, has no series database or device access, and its failure, slowness
// or a missing network never reaches timing capture or live publishing.
if (args.Length != 2 || args[0] != "--pipe") { Console.Error.WriteLine("Radio worker requires a private --pipe connection."); return 2; }
using var pipe = new NamedPipeClientStream(".", args[1], PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
using var lifetime = new CancellationTokenSource();
await pipe.ConnectAsync(15000, lifetime.Token);
using var reader = new StreamReader(pipe, leaveOpen: true);
await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
using var log = new RadioLog();
var gate = new SemaphoreSlim(1, 1);
RadioEngine? engine = null;
CancellationTokenSource? running = null;
Task? loop = null;
var sinks = new List<RadioServerSink>();
var active = new Dictionary<RadioTarget, RadioServerSink>();
IDisposable?[] providers = [];
var health = new RadioHealth(RadioState.Stopped);
LiveSnapshot? latest = null;
log.Write("Radio worker started");

var status = Task.Run(async () =>
{
    try
    {
        while (!lifetime.IsCancellationRequested)
        {
            var current = engine?.Health ?? health;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new RadioWorkerOutput(current), LiveJson.Options).AsMemory(), lifetime.Token);
            await Task.Delay(1000, lifetime.Token);
        }
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { lifetime.Cancel(); }
});
try
{
    while (await reader.ReadLineAsync(lifetime.Token) is { } line)
    {
        if (line.Length > 4 * 1024 * 1024) { throw new IOException("Oversized radio worker input."); }
        var input = JsonSerializer.Deserialize<RadioWorkerInput>(line, LiveJson.Options) ?? throw new IOException("Missing radio worker input.");
        await gate.WaitAsync(lifetime.Token);
        try
        {
            if (input.Snapshot is { } snapshot)
            {
                snapshot.Validate();
                latest = snapshot; engine?.Offer(snapshot);
            }
            if (input.Targets is { } next) { await ReplaceTargets(next); }
            switch (input.Command)
            {
                case "start":
                    await Stop();
                    var options = input.Options ?? throw new LiveValidationException("Radio options missing.");
                    options.Configuration.Validate();
                    try
                    {
                        var llm = RadioProviderFactory.Llm(options.Configuration, options.LlmKey);
                        var tts = RadioProviderFactory.Tts(options.Configuration, options.TtsKey);
                        providers = [llm as IDisposable, tts as IDisposable];
                        engine = new RadioEngine(options.Configuration, llm, tts, new RadioBudget(options.Configuration.BudgetUsd, options.SpentUsd), TimeProvider.System, log.Write);
                        engine.SetSinks(sinks);
                        if (latest is not null) { engine.Offer(latest); }
                        running = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                        var engineToRun = engine; var token = running.Token;
                        loop = Task.Run(() => engineToRun.RunAsync(token));
                        log.Write($"Radio started: {options.Configuration.LlmProvider}/{options.Configuration.TtsProvider}, {sinks.Count} targets");
                    }
                    catch (Exception ex) when (ex is RadioProviderException or LiveValidationException)
                    {
                        foreach (var provider in providers) { provider?.Dispose(); }
                        providers = []; engine = null;
                        health = new(RadioState.Error, Error: ex.Message, BudgetUsd: options.Configuration.BudgetUsd, SpentUsd: options.SpentUsd);
                        log.Write("Radio could not start: configuration");
                    }
                    break;
                case "stop":
                    await Stop();
                    health = health with { State = RadioState.Stopped };
                    break;
                case "state" or "targets": break;
                case "shutdown":
                    await Stop();
                    lifetime.Cancel();
                    break;
                default: throw new LiveValidationException("Unknown radio command.");
            }
        }
        finally { gate.Release(); }
    }
}
catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or LiveValidationException or ObjectDisposedException)
{ log.Write("Radio worker input closed"); }
finally
{
    lifetime.Cancel();
    await Stop();
    foreach (var sink in active.Values) { sink.Dispose(); }
    try { await status; } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
    log.Write("Radio worker stopped");
}
return 0;

async Task Stop()
{
    if (running is null) { return; }
    var spent = engine!.Budget.Spent;
    running.Cancel();
    try { if (loop is not null) { await loop.WaitAsync(TimeSpan.FromSeconds(5)); } }
    catch (Exception ex) when (ex is OperationCanceledException or TimeoutException) { }
    health = engine.Health with { State = RadioState.Stopped, SpentUsd = spent };
    running.Dispose(); running = null; loop = null; engine = null;
    foreach (var provider in providers) { provider?.Dispose(); }
    providers = [];
    await Disable(sinks);
}
// The viewer control disappears at once instead of after the heartbeat timeout.
async Task Disable(IEnumerable<RadioServerSink> removed)
{
    foreach (var sink in removed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await sink.HeartbeatAsync(new(false, false), timeout.Token); }
        catch (Exception ex) when (ex is RadioProviderException or OperationCanceledException or HttpRequestException or IOException) { }
    }
}
async Task ReplaceTargets(RadioTarget[] next)
{
    if (next.Length > 2 || next.Any(x => x is null)) { throw new LiveValidationException("Invalid radio targets."); }
    var removed = active.Where(x => !next.Contains(x.Key)).ToList();
    if (removed.Count == 0 && next.All(active.ContainsKey)) { return; }
    // A target that is no longer enabled, or whose session was replaced, hides its viewer control before it is dropped.
    await Disable(removed.Select(x => x.Value));
    foreach (var (target, sink) in removed) { sink.Dispose(); active.Remove(target); }
    foreach (var target in next.Where(x => !active.ContainsKey(x))) { active[target] = new RadioServerSink(target); }
    sinks = next.Select(x => active[x]).ToList();
    engine?.SetSinks(sinks);
    log.Write($"Radio targets: {sinks.Count}");
}

internal sealed class RadioLog : IDisposable
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    public RadioLog()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime", "LiveTimingLogs");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, $"radio-{Environment.ProcessId}.log");
    }
    // Lifecycle, counts and OpenSkiTime's own messages only: never prompts, scripts, transcripts, names, keys or tokens.
    public void Write(string text)
    {
        lock (_gate)
        {
            try
            {
                if (_writer is not null && _writer.BaseStream.Length > 4 * 1024 * 1024)
                { _writer.Dispose(); _writer = null; File.Move(_path, _path + ".previous", true); }
                _writer ??= new StreamWriter(_path, append: true) { AutoFlush = true };
                _writer.WriteLine($"{DateTimeOffset.UtcNow:O} {text}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _writer?.Dispose(); _writer = null; }
        }
    }
    public void Dispose() { lock (_gate) { _writer?.Dispose(); } }
}
