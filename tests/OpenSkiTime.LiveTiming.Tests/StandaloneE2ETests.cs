using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Client;
using OpenSkiTime.LiveTiming.Harness;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

[CollectionDefinition("Live timing processes", DisableParallelization = true)]
public sealed class LiveProcessTests;

[Collection("Live timing processes")]
public sealed class StandaloneE2ETests
{
    [Fact]
    public async Task CloudProcessPublishesEventsRecoversServerAndWorkerAndDeletesAllData()
    {
        await using var server = new ServerProcess(); await server.Start();
        await using var publisher = new PublisherProcess();
        var state = SyntheticRace.Create(6); publisher.Offer(state);
        var options = new PublisherOptions(PublisherKind.Cloud, server.Endpoint);
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"),options,ProcessFixture.Host);
        await ProcessFixture.Until(() => publisher.Health.State == PublisherState.Running);
        var session = publisher.ResumeSession!;
        Assert.InRange((session.ExpiresAt - DateTimeOffset.UtcNow).TotalDays,13.99,14.01);
        using var http = new HttpClient { BaseAddress = new(server.Endpoint), Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",session.PublisherToken);
        using var socket = await Watch(http,session.SessionId);
        Assert.NotNull(await ReceiveState(socket));
        foreach (var update in new[] {
            (1,LiveStatus.OnCourse,(long?)null,0),(1,LiveStatus.OnCourse,(long?)null,1),(1,LiveStatus.Finished,(long?)6012,0),
            (2,LiveStatus.DNS,(long?)null,0),(3,LiveStatus.DNF,(long?)null,0),(4,LiveStatus.DSQ,(long?)null,0) })
        {
            state = SyntheticRace.Update(state,update.Item1,update.Item2,update.Item3,update.Item4); publisher.Offer(state);
            var received = await ReceiveState(socket);
            Assert.Equal(update.Item2,received!.Runs[0].Results.First(r=>r.Bib==update.Item1).Status);
        }
        Assert.Equal(6012,(await Read(http,session)).Runs[0].Results[0].Hundredths);
        await publisher.CommandAsync("stop"); await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Stopped);
        await Task.Delay(250); Assert.True((await Read(http,session)).Paused);
        state = SyntheticRace.Update(state,5,LiveStatus.OnCourse); publisher.Offer(state); await Task.Delay(350);
        Assert.Equal(LiveStatus.Ready,(await Read(http,session)).Runs[0].Results.First(r=>r.Bib==5).Status);
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"),options,ProcessFixture.Host);
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        Assert.Equal(session.SessionId,publisher.ResumeSession!.SessionId);
        Assert.False((await Read(http,session)).Paused);
        // A deliberate server-side divergence is replaced by Refresh, even if client state hasn't changed.
        using (var response = await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state",SyntheticRace.Create(6),LiveJson.Options)) { response.EnsureSuccessStatusCode(); }
        await publisher.CommandAsync("refresh");
        await UntilState(http,session,s=>s.Runs[0].Results.First(r=>r.Bib==5).Status==LiveStatus.OnCourse);
        await server.Kill(); await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Reconnecting);
        state = SyntheticRace.Update(state,5,LiveStatus.Finished,5899); publisher.Offer(state);
        await server.Start(); await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        Assert.Equal(5899,(await Read(http,session)).Runs[0].Results.First(r=>r.Bib==5).Hundredths);
        Assert.Equal(session.SessionId,publisher.ResumeSession!.SessionId);
        // Kill only the downstream worker. Authoritative snapshots remain in the producer.
        using (var worker = System.Diagnostics.Process.GetProcessById(publisher.ProcessId!.Value)) { worker.Kill(true); await worker.WaitForExitAsync(); }
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Error);
        await Task.Delay(200);
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"),options,ProcessFixture.Host);
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        Assert.Equal(session.SessionId,publisher.ResumeSession!.SessionId);
        await publisher.CommandAsync("delete"); await ProcessFixture.Until(()=>publisher.Health.PublicUrl is null);
        Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync($"api/sessions/{session.SessionId}/state")).StatusCode);
        using (var response = await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state",state,LiveJson.Options)) { Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode); }
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"),options,ProcessFixture.Host);
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        var second = publisher.ResumeSession!; Assert.NotEqual(session.SessionId,second.SessionId);
        await publisher.CommandAsync("delete-all"); await ProcessFixture.Until(()=>publisher.Health.PublicUrl is null);
        Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync($"api/sessions/{second.SessionId}/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync($"api/sessions/{session.SessionId}/state")).StatusCode);
    }
    [Fact]
    public async Task ManagedLocalProcessWorksOfflineAndCoalescesBoundedSnapshots()
    {
        await using var publisher = new PublisherProcess();
        var endpoint = $"http://localhost:{ProcessFixture.Port()}";
        var state = SyntheticRace.Create(); publisher.Offer(state);
        await publisher.StartAsync(ProcessFixture.Artifact("Worker"),new(PublisherKind.Local,endpoint,LocalServerAssembly:ProcessFixture.Artifact("Server")),ProcessFixture.Host);
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        var session = publisher.ResumeSession!;
        using var http = new HttpClient { BaseAddress=new(endpoint) };
        foreach (var next in SyntheticRace.Simulate(state)) { publisher.Offer(state=next); }
        // Every event in a coalesced batch carries the same snapshot timestamp.
        // Wait for worker acknowledgement of the whole batch, not its first event.
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running && publisher.Health.LastEvent==state.UpdatedAt);
        var actual = await Read(http,session);
        Assert.Equal(50,actual.Competitors.Length);
        Assert.Contains(actual.Runs[0].Results,r=>r.Status==LiveStatus.DNS);
        Assert.Contains(actual.Runs[0].Results,r=>r.Status==LiveStatus.DNF);
        Assert.Contains(actual.Runs[0].Results,r=>r.Status==LiveStatus.DSQ);
        using var socket = await Watch(http,session.SessionId); Assert.NotNull(await ReceiveState(socket));
        Assert.Contains("OpenSkiTime",await http.GetStringAsync($"r/{session.SessionId}"));
        Assert.Contains("WebSocket",await http.GetStringAsync("live.js"));
        using (var health=JsonDocument.Parse(await http.GetStringAsync("health")))
        {
            using var server=System.Diagnostics.Process.GetProcessById(health.RootElement.GetProperty("processId").GetInt32());
            server.Kill(true); await server.WaitForExitAsync();
        }
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Reconnecting);
        await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        Assert.Equal(session.SessionId,publisher.ResumeSession!.SessionId);
        Assert.Equal(50,(await Read(http,session)).Competitors.Length);
        await publisher.CommandAsync("stop"); await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Stopped);
        await publisher.CommandAsync("refresh"); await ProcessFixture.Until(()=>publisher.Health.State==PublisherState.Running);
        await publisher.CommandAsync("delete-all"); await ProcessFixture.Until(()=>publisher.Health.PublicUrl is null);
        Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync($"api/sessions/{session.SessionId}/state")).StatusCode);
    }
    [Fact]
    public async Task ApiRejectsCrossSessionTokensMalformedPayloadGapsAndAbusiveCreationWithoutMutation()
    {
        await using var server = new ServerProcess(); await server.Start();
        using var http = new HttpClient { BaseAddress = new(server.Endpoint) };
        async Task<LiveSession> Create()
        { using var response=await http.PostAsync("api/sessions",null); response.EnsureSuccessStatusCode(); return (await response.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!; }
        var first = await Create(); var second = await Create();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",second.PublisherToken);
        using (var response=await http.PutAsJsonAsync($"api/sessions/{first.SessionId}/state",SyntheticRace.Create(),LiveJson.Options)) { Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode); }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",first.PublisherToken);
        var state = SyntheticRace.Create();
        using (var response=await http.PutAsJsonAsync($"api/sessions/{first.SessionId}/state",state,LiveJson.Options)) { response.EnsureSuccessStatusCode(); }
        var invalid = state with { Competitors=[state.Competitors[0],state.Competitors[0]] };
        using (var response=await http.PutAsJsonAsync($"api/sessions/{first.SessionId}/state",invalid,LiveJson.Options)) { Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode); }
        using (var response=await http.PostAsJsonAsync($"api/sessions/{first.SessionId}/events",new LiveEvent(99,1,LiveEventKind.ResultUpdated,state.Runs[0].Results[0],state.UpdatedAt),LiveJson.Options)) { Assert.Equal(HttpStatusCode.Conflict,response.StatusCode); }
        Assert.Equal(state.Version,(await Read(http,first)).Version); Assert.Equal(50,(await Read(http,first)).Competitors.Length);
        for(var i=0;i<3;i++) { await Create(); }
        using var limited=await http.PostAsync("api/sessions",null); Assert.Equal(HttpStatusCode.TooManyRequests,limited.StatusCode);
    }
    private static async Task<LiveSnapshot> Read(HttpClient http,LiveSession session) => (await http.GetFromJsonAsync<LiveSnapshot>($"api/sessions/{session.SessionId}/state",LiveJson.Options))!;
    private static async Task UntilState(HttpClient http,LiveSession session,Func<LiveSnapshot,bool> condition)
    { using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20)); while(!condition(await Read(http,session))) { await Task.Delay(100,timeout.Token); } }
    private static async Task<ClientWebSocket> Watch(HttpClient http,Guid id)
    {
        using var response=await http.PostAsync("live/negotiate?negotiateVersion=1",null); response.EnsureSuccessStatusCode();
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var uri = new UriBuilder(new Uri(http.BaseAddress!,"live")) { Scheme="ws",Query="id="+json.RootElement.GetProperty("connectionToken").GetString() };
        var socket=new ClientWebSocket(); await socket.ConnectAsync(uri.Uri,CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e"),WebSocketMessageType.Text,true,CancellationToken.None);
        var buffer=new byte[4096]; await socket.ReceiveAsync(buffer,CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes($"{{\"type\":1,\"target\":\"Watch\",\"arguments\":[\"{id}\"],\"invocationId\":\"1\"}}\u001e"),WebSocketMessageType.Text,true,CancellationToken.None);
        return socket;
    }
    private static async Task<LiveSnapshot?> ReceiveState(ClientWebSocket socket)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15)); var pending=""; var bytes=new byte[65536];
        while(true)
        {
            var read=await socket.ReceiveAsync(bytes,timeout.Token); pending+=Encoding.UTF8.GetString(bytes,0,read.Count);
            var parts=pending.Split('\u001e'); pending=parts[^1];
            foreach(var part in parts[..^1])
            {
                using var json=JsonDocument.Parse(part);
                if(json.RootElement.TryGetProperty("target",out var target)&&target.GetString()=="State")
                { return json.RootElement.GetProperty("arguments")[0].Deserialize<LiveSnapshot>(LiveJson.Options); }
            }
        }
    }
}
