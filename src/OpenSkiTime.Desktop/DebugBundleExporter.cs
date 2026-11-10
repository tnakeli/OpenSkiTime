using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

public sealed record DebugBundleOptions(string? SeriesPath, bool IncludeSeries, DateTimeOffset From,
    DateTimeOffset Until, string? IssueDescription = null, Guid? ExpectedSeriesId = null,
    IReadOnlyList<SupportTimingRole>? TimingRoles = null);
public sealed record SupportTimingRole(TimingRoleKind Role, int Index, TimingSourceType? Source, int Channel, bool SharedConnection);
public sealed record DebugBundleEntry(string Name, long Bytes, string Sha256);
public sealed record DebugBundleResult(string Path, long Bytes, IReadOnlyList<string> Notices);
public sealed class DebugBundleCleanupException(IReadOnlyList<string> paths, Exception inner)
    : IOException("Temporary support data could not be removed. Check these local paths before sharing files: " + string.Join(", ", paths), inner)
{
    public IReadOnlyList<string> Paths { get; } = paths;
}

/// <summary>Local support export only. No new logging, credential-store access or network operations.</summary>
public sealed class DebugBundleExporter(IAuditTrailReader reader, string? logDirectory = null)
{
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    public async Task<DebugBundleResult> ExportAsync(string destination, DebugBundleOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.From > options.Until || options.Until - options.From > TimeSpan.FromDays(31))
        { throw new ArgumentException("Choose a log window of at most 31 days.", nameof(options)); }
        if (options.IssueDescription?.Length > 10000) { throw new ArgumentException("Issue description is too long.", nameof(options)); }
        if (options.IncludeSeries && options.SeriesPath is null) { throw new InvalidOperationException("Open an event series before including it."); }
        var full = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(full), ".zip", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Choose a .zip destination."); }
        var source = options.SeriesPath is null ? null : Path.GetFullPath(options.SeriesPath);
        if (source is not null && string.Equals(full, source, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        { throw new InvalidOperationException("Choose a destination different from the event series."); }
        var folder = Path.GetDirectoryName(full)!;
        var token = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(folder, ".debug-bundle-" + token + ".tmp");
        var snapshot = Path.Combine(folder, ".debug-series-" + token + ".ost");
        var entries = new List<DebugBundleEntry>();
        var notices = new List<string>();
        try
        {
            AuditSeriesSummary? summary = null;
            if (options.IncludeSeries)
            {
                await reader.CreateSupportSnapshotAsync(source!, snapshot, ct);
                summary = await reader.ReadSummaryAsync(snapshot, ct);
            }
            else if (source is not null)
            {
                try { summary = await reader.ReadSummaryAsync(source, ct); }
                catch (Exception ex) when (ex is SeriesFileException or IOException or UnauthorizedAccessException or System.Data.Common.DbException)
                { notices.Add("Series summary unavailable; no event file was attached."); }
            }
            if (options.ExpectedSeriesId is { } expectedSeries && summary is not null && summary.SeriesId != expectedSeries)
            { throw new InvalidOperationException("The event series identity changed. Reopen the bundle preview."); }
            SupportLogResult logs;
            try { logs = await SupportDiagnostics.ReadAsync(logDirectory ?? SupportDiagnostics.WorkerLogDirectory, options.From, options.Until, ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { logs = new([], 0, 0, ["Worker logs unavailable; no log content included."]); }
            notices.AddRange(logs.Notices);
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    async Task AddAsync(string name, Stream input)
                    {
                        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                        await using var target = entry.Open();
                        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81920];
                        long length = 0;
                        int count;
                        while ((count = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            await target.WriteAsync(buffer.AsMemory(0, count), ct);
                            hash.AppendData(buffer, 0, count); length += count;
                        }
                        entries.Add(new(name, length, Convert.ToHexString(hash.GetHashAndReset())));
                    }
                    async Task TextAsync(string name, string text)
                    {
                        using var input = new MemoryStream(Encoding.UTF8.GetBytes(text));
                        await AddAsync(name, input);
                    }
                    await TextAsync("README.txt", "OpenSkiTime support bundle. Generated locally; nothing was uploaded.\nOnly allowlisted worker-log summaries are included. Desktop trace is not persisted.\n" +
                        (options.IncludeSeries ? "Contains the complete saved event series with personal race data and original evidence. Share privately. ZIP is not encrypted.\n" : "Minimized diagnostics; no race file or personal record payloads included.\n"));
                    await TextAsync("environment.json", SupportDiagnostics.EnvironmentJson());
                    if (options.TimingRoles is not null) { await TextAsync("configuration-summary.json", JsonSerializer.Serialize(options.TimingRoles, s_json)); }
                    if (summary is not null) { await TextAsync("series-summary.json", JsonSerializer.Serialize(summary, s_json)); }
                    await TextAsync("diagnostics/worker-events.json", JsonSerializer.Serialize(logs, s_json));
                    if (!string.IsNullOrWhiteSpace(options.IssueDescription)) { await TextAsync("attachments/issue-description.txt", options.IssueDescription); }
                    if (options.IncludeSeries)
                    {
                        await using var input = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
                        await AddAsync("series/event-series.ost", input);
                    }
                    // Manifest does not hash itself; entry hashes are verified before publication.
                    var manifest = zip.CreateEntry("manifest.json");
                    await using var manifestOutput = manifest.Open();
                    await JsonSerializer.SerializeAsync(manifestOutput, new
                    {
                        FormatVersion = 1, BundleId = token, CreatedAt = DateTimeOffset.UtcNow, ProductInfo.Version, options.From, options.Until,
                        options.IncludeSeries, Snapshot = options.IncludeSeries ? "Verified SQLite backup; capture excluded by lease" : "No series attached",
                        PendingChanges = "Only saved data included; no forms or pending capture input were flushed",
                        RedactionPolicy = "allowlisted-worker-events/v1", Entries = entries, Notices = notices.Distinct().ToArray()
                    }, s_json, ct);
                }
                await output.FlushAsync(ct); output.Flush(flushToDisk: true);
            }
            using (var verify = ZipFile.OpenRead(temporary))
            {
                foreach (var expected in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    await using var input = verify.GetEntry(expected.Name)!.Open();
                    if (Convert.ToHexString(await SHA256.HashDataAsync(input, ct)) != expected.Sha256)
                    { throw new IOException("Support archive verification failed."); }
                }
            }
            ct.ThrowIfCancellationRequested();
            // Remove the sensitive temporary database before reporting or publishing success.
            if (File.Exists(snapshot)) { File.Delete(snapshot); }
            var length = new FileInfo(temporary).Length;
            File.Move(temporary, full, overwrite: true);
            return new(full, length, notices.Distinct().ToArray());
        }
        catch (Exception failure) when (failure is not OutOfMemoryException)
        {
            var retained = new List<string>();
            foreach (var file in new[] { temporary, snapshot })
            {
                try { File.Delete(file); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { retained.Add(file); }
            }
            if (retained.Count > 0) { throw new DebugBundleCleanupException(retained, failure); }
            throw;
        }
    }
}
