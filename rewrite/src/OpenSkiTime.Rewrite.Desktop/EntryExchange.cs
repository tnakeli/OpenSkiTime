using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace OpenSkiTime.Rewrite.Desktop;

public interface IEntryExchange
{
    Task<string?> ReadClipboardAsync();
    Task WriteClipboardAsync(string text);
    Task<bool> SaveTsvAsync(string suggestedName, string text);
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

    public async Task<bool> SaveTsvAsync(string suggestedName, string text)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export selected competitors as TSV", SuggestedFileName = suggestedName + ".tsv",
            DefaultExtension = "tsv", FileTypeChoices = [new FilePickerFileType("Tab-separated values") { Patterns = ["*.tsv"] }],
        });
        var path = file?.TryGetLocalPath();
        if (path is null) { return false; }
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
        return true;
    }
}
