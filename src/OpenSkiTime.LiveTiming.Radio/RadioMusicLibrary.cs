using System.Globalization;

namespace OpenSkiTime.LiveTiming.Radio;

/// <summary>
/// The operator's background music: MP3 files read once from the chosen folder (top level, by file name) within the
/// per-session limits. Files that are not MP3, too large or beyond the limits are skipped. Logs carry counts only.
/// </summary>
public sealed class RadioMusicLibrary
{
    public sealed record Track(string Id, byte[] Data);
    public static readonly RadioMusicLibrary Empty = new([]);
    private RadioMusicLibrary(IReadOnlyList<Track> tracks)
    {
        Tracks = tracks;
        Playlist = new([.. tracks.Select(x => new RadioMusicTrack(x.Id, x.Data.Length))]);
        Id = RadioMusic.PlaylistId(Playlist.Tracks);
    }
    public IReadOnlyList<Track> Tracks { get; }
    public RadioMusicPlaylist Playlist { get; }
    /// <summary>The playlist ID the server reports once it holds this music.</summary>
    public string Id { get; }

    public static RadioMusicLibrary Load(string folder, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (string.IsNullOrWhiteSpace(folder)) { return Empty; }
        try
        {
            var tracks = new List<Track>();
            long total = 0; var skipped = 0;
            foreach (var file in Directory.EnumerateFiles(folder, "*.mp3", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
            {
                var length = new FileInfo(file).Length;
                if (tracks.Count >= RadioMusic.MaxTracks || length is 0 or > RadioMusic.MaxTrackBytes || total + length > RadioMusic.MaxSessionBytes)
                { skipped++; continue; }
                var data = File.ReadAllBytes(file);
                var id = RadioMusic.Id(data);
                if (!RadioMusic.IsMp3(data) || tracks.Any(x => x.Id == id)) { skipped++; continue; }
                tracks.Add(new(id, data)); total += data.Length;
            }
            log(string.Create(CultureInfo.InvariantCulture, $"Radio music: {tracks.Count} tracks, {total / 1048576.0:0.0} MB, {skipped} skipped"));
            return tracks.Count == 0 ? Empty : new(tracks);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            log("Radio music folder could not be read");
            return Empty;
        }
    }
}
