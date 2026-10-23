using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Radio;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Background music through the real server process, REST, SignalR and the worker's server sink.
[Collection("Live timing processes")]
public sealed class RadioMusicTests
{
    [Fact]
    public async Task ServerKeepsOnlyVerifiedTracksOfThePlaylistAndServesThemToViewers()
    {
        await using var server = new ServerProcess(); await server.Start();
        var (http, session) = await PublishAsync(server);
        using var _ = http;
        using var anonymous = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(10) };
        var radio = $"api/sessions/{session.SessionId}/radio";
        using var socket = await Watch(http, session.SessionId);
        var enabled = await Status(http, radio, true);
        Assert.Equal(("", 0), (enabled.Playlist, enabled.Missing.Length));
        // Larger than the server's default request body limit, which the authorized track upload raises.
        var large = Mp3(3 * 1024 * 1024, 1); var small = Mp3(20_000, 2);
        RadioMusicTrack[] tracks = [new(RadioMusic.Id(large), large.Length), new(RadioMusic.Id(small), small.Length)];
        var reply = await Playlist(http, radio, tracks);
        Assert.Equal(tracks.Select(x => x.Id), reply.Missing);
        using (var unauthorized = await anonymous.PutAsync($"{radio}/music/{tracks[1].Id}", Content(small))) { Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode); }
        // The bytes must be the declared MP3; a track outside the playlist is refused.
        using (var wrong = await http.PutAsync($"{radio}/music/{tracks[1].Id}", Content(Mp3(20_000, 3)))) { Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode); }
        using (var html = await http.PutAsync($"{radio}/music/{tracks[1].Id}", Content("<html>"u8.ToArray()))) { Assert.Equal(HttpStatusCode.BadRequest, html.StatusCode); }
        using (var unknown = await http.PutAsync($"{radio}/music/{RadioMusic.Id(Mp3(100, 4))}", Content(Mp3(100, 4)))) { Assert.Equal(HttpStatusCode.Conflict, unknown.StatusCode); }
        using (var invalid = await http.PutAsJsonAsync($"{radio}/music", new RadioMusicPlaylist([new("../track", 10)]), LiveJson.Options))
        { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); }
        Assert.Empty((await ReceiveRadio(socket, x => x.Available)).Music ?? []);
        await Track(http, radio, large);
        reply = await Track(http, radio, small);
        Assert.Equal((RadioMusic.PlaylistId(tracks), 0), (reply.Playlist, reply.Missing.Length));
        var announced = await ReceiveRadio(socket, x => x.Music?.Length == 2);
        Assert.Equal(tracks.Select(x => x.Id), announced.Music);
        using (var served = await anonymous.GetAsync($"{radio}/music/{tracks[0].Id}"))
        {
            Assert.Equal("audio/mpeg", served.Content.Headers.ContentType!.MediaType);
            Assert.Contains("immutable", served.Headers.CacheControl!.ToString(), StringComparison.Ordinal);
            Assert.Equal(large, await served.Content.ReadAsByteArrayAsync());
        }
        // Saving radio settings restarts the radio (disable, enable): the music stays and is not uploaded again.
        await Status(http, radio, false);
        Assert.Equal((RadioMusic.PlaylistId(tracks), 0), ((await Status(http, radio, true)).Playlist, 0));
        // A kept track must be declared with its real size, so the memory limits cannot be bypassed.
        using (var understated = await http.PutAsJsonAsync($"{radio}/music", new RadioMusicPlaylist([tracks[0] with { Bytes = 1 }]), LiveJson.Options))
        { Assert.Equal(HttpStatusCode.BadRequest, understated.StatusCode); }
        // A new playlist drops the tracks it no longer lists.
        reply = await Playlist(http, radio, [tracks[1]]);
        Assert.Empty(reply.Missing);
        using (var removed = await anonymous.GetAsync($"{radio}/music/{tracks[0].Id}")) { Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode); }
    }

    [Fact]
    public async Task WorkerSinkUploadsTheFolderOnceWhenTheServerLacksIt()
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-radio-music-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "b-second.mp3"), Mp3(30_000, 5));
            await File.WriteAllBytesAsync(Path.Combine(folder, "a-first.mp3"), Mp3(40_000, 6));
            await File.WriteAllBytesAsync(Path.Combine(folder, "not-audio.mp3"), "<html>"u8.ToArray());
            await File.WriteAllTextAsync(Path.Combine(folder, "notes.txt"), "Synthetic");
            var log = new List<string>();
            void Log(string x) { lock (log) { log.Add(x); } }
            var library = RadioMusicLibrary.Load(folder, Log);
            // Top-level MP3 files by name; anything that is not an MP3 is skipped. Logs carry counts, not file names.
            Assert.Equal([RadioMusic.Id(Mp3(40_000, 6)), RadioMusic.Id(Mp3(30_000, 5))], library.Tracks.Select(x => x.Id));
            Assert.Equal("Radio music: 2 tracks, 0.1 MB, 1 skipped", Assert.Single(log));
            Assert.Same(RadioMusicLibrary.Empty, RadioMusicLibrary.Load(Path.Combine(folder, "missing"), Log));
            Assert.Throws<LiveValidationException>(() => new RadioConfiguration { MusicFolder = "music" }.Validate());

            await using var server = new ServerProcess(); await server.Start();
            var (http, session) = await PublishAsync(server);
            using var _ = http;
            using var sink = new RadioServerSink(new(PublisherKind.Local, server.Endpoint, session.SessionId, session.PublisherToken), log: Log);
            sink.SetMusic(library);
            await sink.HeartbeatAsync(new(true, false), CancellationToken.None);
            await sink.MusicSync.WaitAsync(TimeSpan.FromSeconds(15));
            var radio = $"api/sessions/{session.SessionId}/radio";
            var reply = await Status(http, radio, true);
            Assert.Equal((library.Id, 0), (reply.Playlist, reply.Missing.Length));
            // The next heartbeat finds the music in place and uploads nothing.
            await sink.HeartbeatAsync(new(true, false), CancellationToken.None);
            await sink.MusicSync.WaitAsync(TimeSpan.FromSeconds(15));
            lock (log) { Assert.Equal(["Radio music on Local: 2 tracks, 2 uploaded"], log.Where(x => x.StartsWith("Radio music on", StringComparison.Ordinal))); }
        }
        finally { Directory.Delete(folder, true); }
    }

    // Synthetic MP3-looking bytes (ID3 header and a seeded body); the server checks the type and the hash, not playability.
    private static byte[] Mp3(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        "ID3"u8.CopyTo(bytes);
        return bytes;
    }
    private static ByteArrayContent Content(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(RadioMusic.ContentType);
        return content;
    }
    private static async Task<(HttpClient Http, LiveSession Session)> PublishAsync(ServerProcess server)
    {
        var http = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add(LiveProtocol.Header, LiveProtocol.Version.ToString(CultureInfo.InvariantCulture));
        using var creation = server.CreateSession();
        using var created = await http.SendAsync(creation);
        var session = (await created.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.PublisherToken);
        (await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state", SyntheticRace.Create(4), LiveJson.Options)).EnsureSuccessStatusCode();
        return (http, session);
    }
    private static async Task<RadioStatusReply> Status(HttpClient http, string radio, bool enabled)
    {
        using var response = await http.PutAsJsonAsync(radio + "/status", new RadioStatusUpdate(enabled, false), LiveJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RadioStatusReply>(LiveJson.Options))!;
    }
    private static async Task<RadioStatusReply> Playlist(HttpClient http, string radio, RadioMusicTrack[] tracks)
    {
        using var response = await http.PutAsJsonAsync(radio + "/music", new RadioMusicPlaylist(tracks), LiveJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RadioStatusReply>(LiveJson.Options))!;
    }
    private static async Task<RadioStatusReply> Track(HttpClient http, string radio, byte[] data)
    {
        using var response = await http.PutAsync($"{radio}/music/{RadioMusic.Id(data)}", Content(data));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<RadioStatusReply>(LiveJson.Options))!;
    }
    private static async Task<ClientWebSocket> Watch(HttpClient http, Guid id)
    {
        using var response = await http.PostAsync("live/negotiate?negotiateVersion=1", null); response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var uri = new UriBuilder(new Uri(http.BaseAddress!, "live")) { Scheme = "ws", Query = "id=" + json.RootElement.GetProperty("connectionToken").GetString() };
        var socket = new ClientWebSocket(); await socket.ConnectAsync(uri.Uri, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096]; await socket.ReceiveAsync(buffer, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":1,\"target\":\"Watch\",\"arguments\":[\"{id}\"],\"invocationId\":\"1\"}}\u001e"), WebSocketMessageType.Text, true, CancellationToken.None);
        return socket;
    }
    private static async Task<RadioAnnouncement> ReceiveRadio(ClientWebSocket socket, Func<RadioAnnouncement, bool> until)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var pending = ""; var bytes = new byte[65536];
        while (true)
        {
            var read = await socket.ReceiveAsync(bytes, timeout.Token); pending += Encoding.UTF8.GetString(bytes, 0, read.Count);
            if (!read.EndOfMessage) { continue; }
            var parts = pending.Split('\u001e'); pending = parts[^1];
            foreach (var part in parts[..^1])
            {
                using var json = JsonDocument.Parse(part);
                if (json.RootElement.TryGetProperty("target", out var target) && target.GetString() == "Radio"
                    && json.RootElement.GetProperty("arguments")[0].Deserialize<RadioAnnouncement>(LiveJson.Options) is { } announcement
                    && until(announcement))
                { return announcement; }
            }
        }
    }
}
