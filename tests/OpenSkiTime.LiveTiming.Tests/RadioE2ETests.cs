using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Client;
using OpenSkiTime.LiveTiming.Harness;
using OpenSkiTime.LiveTiming.Radio.Providers;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Radio through the real server process, worker process, REST and SignalR boundaries.
[Collection("Live timing processes")]
public sealed class RadioE2ETests
{
    [Fact]
    public async Task ServerAcceptsOnlyAuthenticatedValidClipsKeepsThemInABoundedRingAndAnnouncesValidOnes()
    {
        await using var server = new ServerProcess(); await server.Start();
        using var http = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add(LiveProtocol.Header, LiveProtocol.Version.ToString(CultureInfo.InvariantCulture));
        using var creation = server.CreateSession();
        using var created = await http.SendAsync(creation);
        var session = (await created.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!;
        var state = SyntheticRace.Create(4);
        using var anonymous = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.PublisherToken);
        var radio = $"api/sessions/{session.SessionId}/radio";
        // Radio needs published state first.
        using (var early = await http.PostAsJsonAsync(radio + "/clips", Clip(), LiveJson.Options)) { Assert.Equal(HttpStatusCode.Conflict, early.StatusCode); }
        (await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state", state, LiveJson.Options)).EnsureSuccessStatusCode();
        using var socket = await Watch(http, session.SessionId);
        Assert.False((await ReceiveRadio(socket)).Available);

        using (var unauthorized = await anonymous.PostAsJsonAsync(radio + "/clips", Clip(), LiveJson.Options)) { Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode); }
        using (var html = await http.PostAsJsonAsync(radio + "/clips", Clip() with { Audio = Convert.ToBase64String("<html></html>"u8.ToArray()) }, LiveJson.Options))
        { Assert.Equal(HttpStatusCode.BadRequest, html.StatusCode); }
        using (var old = new HttpRequestMessage(HttpMethod.Put, radio + "/status") { Content = JsonContent.Create(new RadioStatusUpdate(true, false), options: LiveJson.Options) })
        {
            old.Headers.Remove(LiveProtocol.Header); old.Headers.Add(LiveProtocol.Header, "1");
            using var refused = await http.SendAsync(old);
            Assert.Equal(HttpStatusCode.UpgradeRequired, refused.StatusCode);
        }

        (await http.PutAsJsonAsync(radio + "/status", new RadioStatusUpdate(true, false), LiveJson.Options)).EnsureSuccessStatusCode();
        // The clip states that bib 1 is still waiting to start.
        var clip = Clip([new(RadioConditionKind.Status, 1, 1, LiveStatus.Ready)]);
        (await http.PostAsJsonAsync(radio + "/clips", clip, LiveJson.Options)).EnsureSuccessStatusCode();
        var announced = await ReceiveRadio(socket, x => x.Clips.Length == 1);
        Assert.True(announced.Available);
        Assert.Equal(clip.ClipId, announced.Clips[0].ClipId);
        using (var audio = await anonymous.GetAsync($"{radio}/clips/{clip.ClipId}"))
        {
            Assert.Equal("audio/wav", audio.Content.Headers.ContentType!.MediaType);
            Assert.Equal(Convert.FromBase64String(clip.Audio), await audio.Content.ReadAsByteArrayAsync());
        }
        // Bib 1 starts: the clip is no longer true and disappears from the announcement.
        state = SyntheticRace.Update(state, 1, LiveStatus.OnCourse);
        (await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state", state, LiveJson.Options)).EnsureSuccessStatusCode();
        Assert.Empty((await ReceiveRadio(socket, x => x.Clips.Length == 0)).Clips);

        var ring = Enumerable.Range(0, RadioPlayback.MaxClipsPerSession + 2).Select(_ => Clip()).ToArray();
        foreach (var each in ring) { (await http.PostAsJsonAsync(radio + "/clips", each, LiveJson.Options)).EnsureSuccessStatusCode(); }
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{radio}/clips/{ring[0].ClipId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"{radio}/clips/{ring[^1].ClipId}")).StatusCode);
        Assert.Equal(RadioPlayback.MaxClipsPerSession, (await ReceiveRadio(socket, x => x.Clips.Length == RadioPlayback.MaxClipsPerSession)).Clips.Length);

        // Budget exhausted hides the control; disabling removes every clip.
        (await http.PutAsJsonAsync(radio + "/status", new RadioStatusUpdate(true, true), LiveJson.Options)).EnsureSuccessStatusCode();
        Assert.False((await ReceiveRadio(socket, x => !x.Available)).Available);
        (await http.PutAsJsonAsync(radio + "/status", new RadioStatusUpdate(false, false), LiveJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{radio}/clips/{ring[^1].ClipId}")).StatusCode);

        (await http.PostAsJsonAsync(radio + "/clips", ring[^1], LiveJson.Options)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"{radio}/clips/{ring[^1].ClipId}")).StatusCode);
        // Delete All Data removes radio clips with the session.
        (await http.DeleteAsync($"api/sessions/{session.SessionId}/data")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"{radio}/clips/{ring[^1].ClipId}")).StatusCode);
        Assert.False((await ReceiveRadio(socket, x => !x.Available)).Available);
    }

    [Fact]
    public async Task RadioWorkerSpeaksOverTheServerAndItsDeathLeavesPublishingUntouched()
    {
        await using var server = new ServerProcess(); await server.Start();
        await using var publisher = new PublisherProcess();
        var state = SyntheticRace.Create(6); publisher.Offer(state);
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"), new PublisherOptions(PublisherKind.Cloud, server.Endpoint, PublisherKey: server.PublisherKey), ProcessFixture.Host);
        await ProcessFixture.Until(() => publisher.Health.State == PublisherState.Running);
        var session = publisher.ResumeSession!;
        using var http = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(5) };
        using var socket = await Watch(http, session.SessionId);

        await using var radio = new RadioProcess();
        radio.Offer(state);
        var configuration = new RadioConfiguration { CloudEnabled = true, LlmProvider = RadioProviders.Simulation, LlmModel = "simulation", TtsProvider = RadioProviders.Simulation };
        await radio.StartAsync(ProcessFixture.Artifact("RadioWorker"), new(configuration, 0m),
            [new(PublisherKind.Cloud, server.Endpoint, session.SessionId, session.PublisherToken)], ProcessFixture.Host);
        await ProcessFixture.Until(() => radio.Health.State == RadioState.Running);
        Assert.True((await ReceiveRadio(socket, x => x.Available)).Available);
        foreach (var (bib, status, time) in new[] { (1, LiveStatus.OnCourse, (long?)null), (1, LiveStatus.Finished, (long?)5012) })
        { state = SyntheticRace.Update(state, bib, status, time); publisher.Offer(state); radio.Offer(state); }
        var spoken = await ReceiveRadio(socket, x => x.Clips.Length > 0, seconds: 30);
        using (var audio = await http.GetAsync($"api/sessions/{session.SessionId}/radio/clips/{spoken.Clips[0].ClipId}"))
        { Assert.Equal("audio/wav", audio.Content.Headers.ContentType!.MediaType); }
        await ProcessFixture.Until(() => radio.Health.LastClipAt is not null);

        // Killing the radio worker reaches neither the publisher nor the server state it publishes.
        using (var worker = System.Diagnostics.Process.GetProcessById(radio.ProcessId!.Value)) { worker.Kill(); await worker.WaitForExitAsync(); }
        await ProcessFixture.Until(() => radio.Health.State == RadioState.Error);
        state = SyntheticRace.Update(state, 2, LiveStatus.DNF); publisher.Offer(state);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while ((await http.GetFromJsonAsync<LiveSnapshot>($"api/sessions/{session.SessionId}/state", LiveJson.Options, timeout.Token))!
            .Runs[0].Results.First(x => x.Bib == 2).Status != LiveStatus.DNF) { await Task.Delay(100, timeout.Token); }
        Assert.Equal(PublisherState.Running, publisher.Health.State);
    }

    [Fact]
    public void IpcAndServerContractsRoundTrip()
    {
        var state = SyntheticRace.Create(2);
        var input = new RadioWorkerInput("start", new(new RadioConfiguration { LocalEnabled = true, EventContext = "Taustaa" }, 0.25m, "llm-key", "tts-key"),
            state, [new(PublisherKind.Local, "http://127.0.0.1:5078", Guid.NewGuid(), "token")]);
        var json = JsonSerializer.Serialize(input, LiveJson.Options);
        var back = JsonSerializer.Deserialize<RadioWorkerInput>(json, LiveJson.Options)!;
        Assert.Equal(input.Options!.Configuration, back.Options!.Configuration);
        Assert.Equal(input.Targets, back.Targets);
        Assert.Equal(0.25m, back.Options.SpentUsd);
        var health = new RadioHealth(RadioState.BudgetExhausted, 2, RadioRace.Start, "Aino", "Radio budget reached", 1.5m, 1.5m, "Exhausted", RacePhase.BetweenRuns);
        Assert.Equal(health, JsonSerializer.Deserialize<RadioWorkerOutput>(JsonSerializer.Serialize(new RadioWorkerOutput(health), LiveJson.Options), LiveJson.Options)!.Health);
        Assert.Contains("\"state\":\"BudgetExhausted\"", JsonSerializer.Serialize(health, LiveJson.Options), StringComparison.Ordinal);
        var panel = new ControlPanelOutput(Guid.NewGuid(), [], Radio: health);
        Assert.Equal(health, JsonSerializer.Deserialize<ControlPanelOutput>(JsonSerializer.Serialize(panel, LiveJson.Options), LiveJson.Options)!.Radio);
        var clip = Clip([new(RadioConditionKind.Rank, 1, 2, Value: 3)]);
        var clipJson = JsonSerializer.Serialize(clip, LiveJson.Options);
        Assert.Contains("\"kind\":\"Rank\"", clipJson, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript", clipJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(clip.Conditions, JsonSerializer.Deserialize<RadioClipUpload>(clipJson, LiveJson.Options)!.Conditions);
        var announcement = new RadioAnnouncement(true, [new(Guid.NewGuid(), Guid.NewGuid(), 7, 0, 2, "Kalle", 2, false, RadioRace.Start, 1200)]);
        using var parsed = JsonDocument.Parse(JsonSerializer.Serialize(announcement, LiveJson.Options));
        // The viewer reads these exact camelCase names.
        foreach (var name in new[] { "clipId", "batchId", "sequence", "line", "lineCount", "host", "priority", "interruptible", "expiresAt", "durationMs" })
        { Assert.True(parsed.RootElement.GetProperty("clips")[0].TryGetProperty(name, out _), name); }
        Assert.Equal(2, LiveProtocol.Version);
    }

    private static RadioClipUpload Clip(RadioCondition[]? conditions = null) => new(Guid.NewGuid(), Guid.NewGuid(), 0, 1, "Aino", 3, true,
        120, [], conditions ?? [], "audio/wav", 400, Convert.ToBase64String(SimulationTts.Tone(400, 440)));

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
    private static async Task<RadioAnnouncement> ReceiveRadio(ClientWebSocket socket, Func<RadioAnnouncement, bool>? until = null, int seconds = 15)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)); var pending = ""; var bytes = new byte[65536];
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
                    && (until is null || until(announcement)))
                { return announcement; }
            }
        }
    }
}
