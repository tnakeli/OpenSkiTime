using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OpenSkiTime.LiveTiming.LoadTest;

// Usage: --origin <https://server> (--session <id> | --control <blob container SAS URL>) [--viewers 900] [--ramp-seconds 120]
//        [--minutes 40] [--no-page] [--legacy] [--label <name>] [--output <folder>] [--whoami <URL answering with the caller's address>]
// --legacy subscribes like a page opened before viewer synchronization (Watch, a full State after every change).
// With --control, several generators started ahead of the race wait for the blob session.txt (the session ID), stop when
// the blob stop appears and upload their results under results/<label>/. Every run stops after --minutes or on
// Ctrl+C/SIGTERM, compares every viewer's final state with the server's state and then writes report.md, states.csv.gz and
// attempts.csv.gz. It exits with 1 when a viewer's final state differs; stop the run only after the last publish.
var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 0; i < args.Length; i++)
{
    if (!args[i].StartsWith("--", StringComparison.Ordinal)) { Console.Error.WriteLine("Unexpected argument " + args[i]); return 2; }
    options[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
}
Uri? control = options.TryGetValue("control", out var controlText) ? new Uri(controlText) : null;
var session = Guid.Empty;
if (!options.TryGetValue("origin", out var originText) || !Uri.TryCreate(originText, UriKind.Absolute, out var origin)
    || (control is null && !(options.TryGetValue("session", out var sessionText) && Guid.TryParse(sessionText, out session))))
{
    Console.Error.WriteLine("Usage: --origin <https://server> (--session <id> | --control <container SAS URL>) [--viewers 900] [--ramp-seconds 120] [--minutes 40] [--no-page] [--legacy] [--label <name>] [--output <folder>] [--whoami <URL>]");
    return 2;
}
int Number(string name, int fallback) => options.TryGetValue(name, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;
var viewers = Number("viewers", 900);
var ramp = TimeSpan.FromSeconds(Number("ramp-seconds", 120));
var duration = TimeSpan.FromMinutes(Number("minutes", 40));
var label = options.GetValueOrDefault("label", Environment.MachineName);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = int.MaxValue,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    AutomaticDecompression = DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(30),
}) { Timeout = TimeSpan.FromSeconds(60) };

// Server limits apply per client address; with several generators each prints its public address to show the split.
async Task WhoAmI() { if (options.TryGetValue("whoami", out var whoami)) { Console.WriteLine($"{label} public address " + (await http.GetStringAsync(new Uri(whoami))).Trim()); } }
Uri Blob(string name) => new UriBuilder(control!) { Path = control!.AbsolutePath.TrimEnd('/') + "/" + name }.Uri;
async Task<string?> ReadBlob(string name)
{
    using var response = await http.GetAsync(Blob(name));
    return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null;
}
await WhoAmI();
if (control is not null)
{
    Console.WriteLine($"{label} waiting for session.txt");
    while (!stop.IsCancellationRequested && !Guid.TryParse((await ReadBlob("session.txt"))?.Trim(), out session))
    {
        try { await Task.Delay(TimeSpan.FromSeconds(3), stop.Token); }
        catch (OperationCanceledException) { return 1; }
    }
    await WhoAmI();
    _ = Task.Run(async () =>
    {
        while (!stop.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stop.Token); if (await ReadBlob("stop") is not null) { await stop.CancelAsync(); } }
            catch (OperationCanceledException) { return; }
            catch (HttpRequestException) { /* Checked again in five seconds. */ }
        }
    });
}
stop.CancelAfter(duration);

var recorder = new LoadRecorder();
var started = DateTime.UtcNow;
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{started:HH:mm:ss} {label}: {viewers} viewers on {origin} session {session}, ramp {ramp.TotalSeconds:0} s, at most {duration.TotalMinutes:0} min"));
var legacy = options.ContainsKey("legacy");
var tasks = new List<Task>();
var simulated = new List<Viewer>();
var status = Task.Run(async () =>
{
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
    try { while (await timer.WaitForNextTickAsync(stop.Token)) { Console.WriteLine(recorder.Status()); } }
    catch (OperationCanceledException) { }
});
for (var i = 0; i < viewers && !stop.IsCancellationRequested; i++)
{
    var viewer = new Viewer(i, origin, session, http, recorder, !options.ContainsKey("no-page"), legacy);
    simulated.Add(viewer);
    tasks.Add(Task.Run(() => viewer.RunAsync(stop.Token)));
    try { await Task.Delay(ramp / viewers, stop.Token); }
    catch (OperationCanceledException) { }
}
await Task.WhenAll(tasks);
await status;
var ended = DateTime.UtcNow;
Console.WriteLine(recorder.Status());

// Every viewer must end at the authoritative state, field for field (source ticks included).
// An unreachable server (for example an outage at the end) leaves nothing to compare; the results are still saved.
JsonNode? authoritative = null;
try
{
    using var response = await http.GetAsync(new Uri(origin, $"/api/sessions/{session}/state"));
    if (response.IsSuccessStatusCode) { authoritative = JsonNode.Parse(await response.Content.ReadAsStringAsync()); }
}
catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Console.WriteLine("Final state request failed: " + ex.GetType().Name); }
var final = authoritative is null ? new FinalCheck(false, 0, 0, 0, null) : Compare(simulated, authoritative);
Console.WriteLine(final.Available ? $"Final state: {final.Equal} equal, {final.Mismatched} different, {final.WithoutState} without state" : "Final state: not available");
var run = recorder.Snapshot();
var report = LoadReport.Markdown(run, origin, session, viewers, legacy, started, ended, final);
var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
{
    ["report.md"] = Encoding.UTF8.GetBytes(report),
    ["states.csv.gz"] = Gzip(run.StatesCsv()),
    ["attempts.csv.gz"] = Gzip(run.AttemptsCsv()),
};
Console.WriteLine("----- report.md -----");
Console.WriteLine(report);
if (options.TryGetValue("output", out var output))
{
    Directory.CreateDirectory(output);
    foreach (var (name, bytes) in files) { await File.WriteAllBytesAsync(Path.Combine(output, name), bytes); }
}
if (control is not null)
{
    foreach (var (name, bytes) in files)
    {
        using var content = new ByteArrayContent(bytes);
        using var request = new HttpRequestMessage(HttpMethod.Put, Blob($"results/{label}/{name}")) { Content = content };
        request.Headers.Add("x-ms-blob-type", "BlockBlob");
        using var response = await http.SendAsync(request);
        Console.WriteLine($"Uploaded {name}: {(int)response.StatusCode}");
    }
}
return final.Mismatched + final.WithoutState > 0 ? 1 : 0;

static FinalCheck Compare(IEnumerable<Viewer> viewers, JsonNode authoritative)
{
    int equal = 0, different = 0, missing = 0; string? example = null;
    foreach (var viewer in viewers)
    {
        if (viewer.Final is not { } shown) { missing++; continue; }
        if (JsonNode.DeepEquals(shown, authoritative)) { equal++; continue; }
        different++;
        example ??= Difference(shown, authoritative, "$");
    }
    return new(true, equal, different, missing, example);
}

// The path and both values of the first difference between two JSON trees.
static string Difference(JsonNode? shown, JsonNode? expected, string path)
{
    if (shown is JsonObject a && expected is JsonObject b)
    {
        foreach (var name in a.Select(x => x.Key).Union(b.Select(x => x.Key)))
        { if (!JsonNode.DeepEquals(a[name], b[name])) { return Difference(a[name], b[name], path + "." + name); } }
    }
    if (shown is JsonArray x && expected is JsonArray y && x.Count == y.Count)
    {
        for (var i = 0; i < x.Count; i++) { if (!JsonNode.DeepEquals(x[i], y[i])) { return Difference(x[i], y[i], string.Create(CultureInfo.InvariantCulture, $"{path}[{i}]")); } }
    }
    static string Text(JsonNode? node) { var text = node?.ToJsonString() ?? "null"; return text.Length > 120 ? text[..120] + "…" : text; }
    return $"{path}: viewer {Text(shown)}, server {Text(expected)}";
}

static byte[] Gzip(string text)
{
    using var memory = new MemoryStream();
    using (var zip = new GZipStream(memory, CompressionLevel.Optimal)) { zip.Write(Encoding.UTF8.GetBytes(text)); }
    return memory.ToArray();
}
