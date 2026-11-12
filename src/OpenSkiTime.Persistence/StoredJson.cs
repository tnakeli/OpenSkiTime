using System.Text.Json;
using OpenSkiTime.Application;

namespace OpenSkiTime.Persistence;

/// <summary>
/// Reads JSON values stored in an event series file. A series file may come from another machine, so a malformed or
/// missing value is a recoverable file error (SeriesFileException), never an unhandled exception in the desktop.
/// </summary>
internal static class StoredJson
{
    public static T Read<T>(string json, string what) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json) ?? throw Unreadable(what, null); }
        catch (Exception ex) when (ex is JsonException or NotSupportedException) { throw Unreadable(what, ex); }
    }

    public static T? ReadOptional<T>(string? json, string what) where T : class
        => json is null ? null : Read<T>(json, what);

    private static SeriesFileException Unreadable(string what, Exception? inner)
        => new($"The {what} saved in this event series file is unreadable. Restore a verified backup.", inner);
}
