using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSkiTime.Application;

namespace OpenSkiTime.Reporting;

public interface IReportPdfRenderer { void Render(PdfReportData data, string destination); }
public interface IPdfOpener { void Open(string path); }
public sealed class SystemPdfOpener : IPdfOpener
{
    public void Open(string path)
    {
        if (!File.Exists(path)) { throw new FileNotFoundException("The PDF is missing. Generate it first.", path); }
        if (OperatingSystem.IsLinux()) { Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { path }, UseShellExecute = false }); }
        else { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    }
}
public sealed record PdfGenerationOutcome(string ReportId, bool Success, string? Error = null);
public static class ReportStatusService
{
    public static string VersionFor(PdfReportSource source, PdfReportDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(descriptor);
        return descriptor.CompetitionId is null
            ? Convert.ToHexString(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Version = 1, source.Series.Revision, source.Settings.Profile })))
            : source.SourceVersion;
    }
    public static PdfReportStatus GetStatus(PdfReportSource source, PdfReportDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(descriptor);
        var path = Path.Combine(Path.GetDirectoryName(source.FilePath) ?? "", descriptor.FileName);
        if (string.IsNullOrWhiteSpace(source.FilePath) || !File.Exists(path)) { return PdfReportStatus.NotGenerated; }
        var metadata = source.Settings.Reports.FirstOrDefault(x => x.ReportId == descriptor.Id);
        if (metadata?.SourceVersion != VersionFor(source, descriptor) || metadata.FileName != descriptor.FileName) { return PdfReportStatus.Outdated; }
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)) == metadata.PdfHash ? PdfReportStatus.Generated : PdfReportStatus.Outdated;
    }
}
public sealed partial class ReportGenerationService(IReportPdfRenderer renderer, ILogger<ReportGenerationService>? logger = null)
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Generating PDF {ReportId} run {Run} to {File}")]
    private partial void LogStarted(string reportId, int? run, string file);
    [LoggerMessage(Level = LogLevel.Information, Message = "PDF generation completed for {ReportId}")]
    private partial void LogCompleted(string reportId);
    [LoggerMessage(Level = LogLevel.Error, Message = "PDF generation failed for {ReportId}")]
    private partial void LogFailed(Exception exception, string reportId);
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not clean PDF temporary file")]
    private partial void LogCleanup(Exception exception);
    private readonly ILogger<ReportGenerationService> _logger = logger ?? NullLogger<ReportGenerationService>.Instance;
    // Shared across service instances. The exclusive file lease also protects another process.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    public async Task<PdfGenerationOutcome> GenerateAsync(PdfReportSource source, PdfReportDescriptor descriptor,
        Func<GeneratedPdf, Task> saveMetadata, DateTimeOffset at, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(descriptor); ArgumentNullException.ThrowIfNull(saveMetadata);
        if (!descriptor.CanGenerate) { return new(descriptor.Id, false, descriptor.UnavailableReason); }
        var path = Path.Combine(Path.GetDirectoryName(source.FilePath) ?? throw new IOException("Save the .ost file first."), descriptor.FileName);
        var gate = s_locks.GetOrAdd(path, _ => new(1, 1));
        await gate.WaitAsync(ct);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            LogStarted(descriptor.Id, descriptor.RunNumber, descriptor.FileName);
            using var lease = new FileStream(path + ".generation.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            var data = new PdfReportData(descriptor, source, EntryReportBuilder.Build(source, descriptor), at);
            await Task.Run(() => renderer.Render(data, temporary), ct);
            ct.ThrowIfCancellationRequested();
            using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Span<byte> signature = stackalloc byte[5];
                if (stream.Length < 100 || stream.Read(signature) != 5 || !signature.SequenceEqual("%PDF-"u8))
                { throw new IOException("Rendering did not produce a valid PDF."); }
                stream.Flush(flushToDisk: true);
            }
            var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(temporary, ct)));
            // Same-directory replacement never exposes a partially rendered file.
            File.Move(temporary, path, overwrite: true);
            await saveMetadata(new(descriptor.Id, descriptor.FileName, at, ReportStatusService.VersionFor(source, descriptor), hash));
            LogCompleted(descriptor.Id);
            return new(descriptor.Id, true);
        }
        catch (OperationCanceledException) { throw; }
        // Plugin/native layout errors are isolated at this secondary feature boundary.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LogFailed(ex, descriptor.Id);
            return new(descriptor.Id, false, "PDF generation failed. Check the background, available disk space and file permissions. Close the PDF reader before retrying if it locks the file.");
        }
        finally
        {
            try { if (File.Exists(temporary)) { File.Delete(temporary); } }
            catch (IOException ex) { LogCleanup(ex); }
            gate.Release();
        }
    }
    public async Task<IReadOnlyList<PdfGenerationOutcome>> GenerateAllAsync(PdfReportSource source,
        Func<GeneratedPdf, Task> saveMetadata, DateTimeOffset at, CancellationToken ct = default)
    {
        var result = new List<PdfGenerationOutcome>();
        foreach (var descriptor in ReportCatalog.GetAvailableReports(source).Where(x => x.CanGenerate))
        { result.Add(await GenerateAsync(source, descriptor, saveMetadata, at, ct)); }
        return result;
    }
}
