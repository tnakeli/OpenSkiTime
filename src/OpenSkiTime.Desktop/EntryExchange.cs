using Avalonia.Controls;

namespace OpenSkiTime.Desktop;

public interface IEntryExchange
{
    Task<string?> ReadClipboardAsync();
    Task WriteClipboardAsync(string text);
}

public sealed class AvaloniaEntryExchange(Window owner) : IEntryExchange
{
    public Task<string?> ReadClipboardAsync()
        => owner.Clipboard?.GetTextAsync() ?? Task.FromResult<string?>(null);

    public async Task WriteClipboardAsync(string text)
    {
        if (owner.Clipboard is null) { throw new InvalidOperationException("Clipboard is unavailable."); }
        await owner.Clipboard.SetTextAsync(text);
    }
}
