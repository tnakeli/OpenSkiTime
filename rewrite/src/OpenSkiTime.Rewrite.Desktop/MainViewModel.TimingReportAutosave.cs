using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _reportSaveGate = new(1, 1);
    private CancellationTokenSource? _reportSaveDelay;
    private Task _reportSaveTask = Task.CompletedTask;
    private long _reportEditGeneration;
    private bool _reportDisposed;

    private void QueueReportSave()
    {
        _reportEditGeneration++;
        _reportSaveDelay?.Cancel(); _reportSaveDelay?.Dispose();
        _reportSaveDelay = new();
        ReportStatus = "Saving changes…";
        _reportSaveTask = SaveReportAfterPauseAsync(_reportSaveDelay.Token);
    }

    private async Task SaveReportAfterPauseAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct);
            // A long image/FIS operation owns its accepted snapshot. Its finally block
            // releases the UI; the pending edit is then saved before any race/file switch.
            while (IsReportBusy) { await Task.Delay(100, ct); }
            if (!_reportDisposed) { await DrainReportSavesAsync(); }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> DrainReportSavesAsync()
    {
        await _reportSaveGate.WaitAsync();
        _reportSaveGate.Release();
        while (HasTimingReportEdits)
        {
            try { await SaveReportCoreAsync(); }
            catch (Exception ex) when (ex is DomainValidationException or SeriesFileException or SeriesConflictException
                or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ReportStatus = "Changes not saved: " + ex.Message;
                SetStatus(ReportStatus, true);
                return false;
            }
        }
        return true;
    }

    private void DisposeReportAutosave()
    {
        _reportDisposed = true;
        _reportSaveDelay?.Cancel(); _reportSaveDelay?.Dispose();
        // Do not dispose the semaphore while an asynchronous write is still using it.
    }
}
