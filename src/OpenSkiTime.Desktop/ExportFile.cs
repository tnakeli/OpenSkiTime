using System.Text;

namespace OpenSkiTime.Desktop;

// Exports are written beside the destination and then moved over it: a full disk or a removed USB stick leaves the
// previous file or none, never a truncated file that looks complete (an approved XML sent to FIS, a printed start list).
internal static class ExportFile
{
    private static readonly UTF8Encoding s_utf8 = new(false);

    public static async Task WriteAsync(string path, byte[] bytes)
    {
        var full = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes);
            File.Move(temporary, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // Same bytes as File.WriteAllTextAsync: UTF-8 without a byte order mark.
    public static Task WriteTextAsync(string path, string text) => WriteAsync(path, s_utf8.GetBytes(text));
}
