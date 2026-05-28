using Avalonia.Controls.ApplicationLifetimes;

namespace OpenSkiTime.Desktop.Services;

/// <summary>
/// Abstraction over the OS clipboard. Tests inject fakes that return
/// canned TSV strings so the importer flow can be exercised without a
/// real clipboard.
/// </summary>
public interface IClipboardService
{
    Task<string?> GetTextAsync();
}

public sealed class ClipboardService : IClipboardService
{
    public async Task<string?> GetTextAsync()
    {
        // Avalonia exposes the clipboard via the focused TopLevel. In the
        // classic desktop lifetime this is the main window. If we're called
        // before the main window exists (unlikely), bail with null.
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow is null)
        {
            return null;
        }

        var clipboard = desktop.MainWindow.Clipboard;
        return clipboard is null ? null : await clipboard.GetTextAsync();
    }
}
