using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

public sealed record SupportLogEvent(DateTimeOffset At, string Component, string Event);
public sealed record SupportLogResult(IReadOnlyList<SupportLogEvent> Events, int FilesRead, long OmittedLines,
    IReadOnlyList<string> Notices);
public sealed record SupportLogFile(string Name, long Bytes)
{
    public override string ToString() => Name;
}

/// <summary>Reads existing worker logs. Never forwards arbitrary text, wire XML or external error messages.</summary>
public static partial class SupportDiagnostics
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    public static string WorkerLogDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSkiTime", "LiveTimingLogs");
    [GeneratedRegex(@"^(publisher|radio)-[0-9]+\.log(\.previous)?$", RegexOptions.CultureInvariant)]
    private static partial Regex LogFileName();
    public static IReadOnlyList<SupportLogFile> ListLocalFiles(string directory)
    {
        if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { return []; }
        return Directory.EnumerateFiles(directory).Where(p => LogFileName().IsMatch(Path.GetFileName(p)))
            .Take(100).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
            .Select(p => new SupportLogFile(Path.GetFileName(p), new FileInfo(p).Length))
            .OrderBy(f => f.Name, StringComparer.Ordinal).ToArray();
    }

    public static async Task<string> ReadLocalFileAsync(string directory, SupportLogFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (!LogFileName().IsMatch(file.Name) || Path.GetFileName(file.Name) != file.Name
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        { throw new InvalidOperationException("Choose a registered worker log."); }
        var path = Path.Combine(directory, file.Name);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { throw new InvalidOperationException("Linked log files are excluded."); }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        if (stream.Length > 8 * 1024 * 1024) { throw new InvalidOperationException("This log exceeds the 8 MiB local preview limit."); }
        var bytes = new byte[checked((int)stream.Length)];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(count), ct);
            if (read == 0) { break; }
            count += read;
        }
        return "Original local worker log; may contain personal data. This text is excluded from debug bundles.\n\n" +
            System.Text.Encoding.UTF8.GetString(bytes, 0, count);
    }
    private static readonly Dictionary<string, string> s_events = new(StringComparer.Ordinal)
    {
        ["Publisher process started"] = "Process started",
        ["Publisher start"] = "Publishing started",
        ["Publisher stopped"] = "Publishing stopped",
        ["Authoritative state published"] = "Authoritative state published",
        ["Server is missing session state; republishing full snapshot"] = "Server state missing; full snapshot requested",
        ["Session deleted and all session runtime data cleared"] = "Session deleted",
        ["Radio process started"] = "Process started",
        ["Radio stopped"] = "Radio stopped"
    };

    public static async Task<SupportLogResult> ReadAsync(string directory, DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default)
    {
        var events = new List<SupportLogEvent>();
        var notices = new List<string> { "Desktop trace is not persisted. Unsupported log text and wire payloads are omitted." };
        long omitted = 0;
        var filesRead = 0;
        if (!Directory.Exists(directory)) { notices.Add("Worker log directory unavailable."); return new(events, 0, 0, notices); }
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        { notices.Add("Linked log directories are excluded."); return new(events, 0, 0, notices); }
        // Bound discovery as well as content; no profile scan or recursive traversal.
        var files = Directory.EnumerateFiles(directory).Where(p => LogFileName().IsMatch(Path.GetFileName(p)))
            .Take(101).ToArray();
        if (files.Length > 100) { notices.Add("Log discovery limited to 100 files."); }
        long total = 0;
        foreach (var file in files.Take(100))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) { notices.Add("Linked log file excluded."); continue; }
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                if (stream.Length > 8 * 1024 * 1024 || total + stream.Length > 32 * 1024 * 1024)
                { notices.Add("Log file omitted by the 8 MiB per-file / 32 MiB collection limit."); continue; }
                // Read a fixed high-water mark so a growing file cannot make collection unbounded.
                var bytes = new byte[checked((int)stream.Length)];
                var read = 0;
                while (read < bytes.Length)
                {
                    var count = await stream.ReadAsync(bytes.AsMemory(read), ct);
                    if (count == 0) { break; }
                    read += count;
                }
                total += read; filesRead++;
                using var input = new StringReader(System.Text.Encoding.UTF8.GetString(bytes, 0, read));
                while (input.ReadLine() is { } line)
                {
                    ct.ThrowIfCancellationRequested();
                    var split = line.IndexOf(' ');
                    if (split < 0 || !DateTimeOffset.TryParse(line.AsSpan(0, split), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) { omitted++; continue; }
                    if (at < from || at > until) { continue; }
                    var text = line[(split + 1)..];
                    var safe = s_events.GetValueOrDefault(text);
                    if (text.StartsWith("FIS ", StringComparison.Ordinal)) { safe = "FIS protocol event; content omitted"; }
                    if (text == "FIS wire trace enabled: log contains racer identities") { safe = "Sensitive wire trace was enabled; content omitted"; }
                    if (safe is null || events.Count >= 10000) { omitted++; continue; }
                    events.Add(new(at, Path.GetFileName(file).StartsWith("publisher-", StringComparison.Ordinal) ? "Publisher" : "Radio", safe));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { notices.Add("A worker log was unreadable or rotated during collection."); }
        }
        if (omitted > 0) { notices.Add("Unsupported, sensitive or excess lines were omitted."); }
        return new(events.OrderByDescending(e => e.At).ThenBy(e => e.Component, StringComparer.Ordinal).ToArray(), filesRead, omitted, notices.Distinct().ToArray());
    }

    public static string EnvironmentJson() => JsonSerializer.Serialize(new
    {
        Product = ProductInfo.Name, ProductInfo.Version, ProductInfo.FisRulesLabel,
        Build = System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(SupportDiagnostics).Assembly)?.InformationalVersion,
        Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        Culture = CultureInfo.CurrentCulture.Name, UiCulture = CultureInfo.CurrentUICulture.Name, TimeZone = TimeZoneInfo.Local.Id,
        DesktopTrace = "Not persisted", WireTraceEnabled = Environment.GetEnvironmentVariable("OST_FIS_WIRE_TRACE") == "1"
    }, s_json);
}
