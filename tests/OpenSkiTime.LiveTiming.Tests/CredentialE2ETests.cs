using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Harness;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

[Collection("Live timing processes")]
public sealed class CredentialE2ETests
{
    [Fact]
    public async Task ExpiredTamperedAndDeletedCredentialsAreRejectedAndCapacityStaysBounded()
    {
        await using var server=new ServerProcess(maxSessions:2); await server.Start();
        using var http=new HttpClient { BaseAddress=new(server.Endpoint) };
        using var creationRequest=server.CreateSession();
        using var creation=await http.SendAsync(creationRequest);
        var session=(await creation.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!;
        Assert.InRange((session.ExpiresAt-DateTimeOffset.UtcNow).TotalHours,335.9,336.1);
        var expiredPayload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { SessionId=session.SessionId,Expires=DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds() }));
        var expired=expiredPayload+"."+Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(server.SigningKey),Encoding.UTF8.GetBytes(expiredPayload)));
        foreach(var token in new[]{expired,session.PublisherToken+"tampered"})
        {
            http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",token);
            using var response=await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state",SyntheticRace.Create(),LiveJson.Options);
            Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode);
        }
        http.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",session.PublisherToken);
        using(var response=await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state",SyntheticRace.Create(),LiveJson.Options)) { response.EnsureSuccessStatusCode(); }
        using(var response=await http.DeleteAsync($"api/sessions/{session.SessionId}/data")) { response.EnsureSuccessStatusCode(); }
        using(var response=await http.PutAsJsonAsync($"api/sessions/{session.SessionId}/state",SyntheticRace.Create(),LiveJson.Options)) { Assert.Equal(HttpStatusCode.Unauthorized,response.StatusCode); }
        // Deleting frees the session's slot (the deletion marker takes none); the capacity of two still holds.
        for(var i=0;i<2;i++)
        {
            using var request=server.CreateSession();
            using var response=await http.SendAsync(request); response.EnsureSuccessStatusCode();
        }
        using(var request=server.CreateSession())
        using(var response=await http.SendAsync(request)) { Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); }
    }

    [Fact]
    public async Task OnePublisherKeyCannotTakeTheWholeServerAndARemovedKeyLosesItsSessions()
    {
        var other=LivePublisherKey.Generate();
        var keys=new Dictionary<string,string>{ ["LiveTiming__MaxSessionsPerPublisher"]="2" };
        await using var server=new ServerProcess(maxSessions:3,environment:keys);
        keys["LiveTiming__PublisherKeys"]="test-publisher:"+LivePublisherKey.Hash(server.PublisherKey)+";other:"+LivePublisherKey.Hash(other);
        await server.Start();
        using var http=new HttpClient { BaseAddress=new(server.Endpoint) };
        async Task<HttpResponseMessage> Create(string? key=null) { using var request=server.CreateSession(key); return await http.SendAsync(request); }
        async Task<HttpStatusCode> Publish(LiveSession session)
        {
            using var request=new HttpRequestMessage(HttpMethod.Put,$"api/sessions/{session.SessionId}/state") { Content=JsonContent.Create(SyntheticRace.Create(),options:LiveJson.Options) };
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",session.PublisherToken);
            using var response=await http.SendAsync(request); return response.StatusCode;
        }
        var mine=new List<LiveSession>();
        for(var i=0;i<2;i++) { using var response=await Create(); mine.Add((await response.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!); }
        // The key's own limit is reached while the server still has room for another organizer.
        using(var response=await Create()) { Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); }
        LiveSession theirs;
        using(var response=await Create(other)) { response.EnsureSuccessStatusCode(); theirs=(await response.Content.ReadFromJsonAsync<LiveSession>(LiveJson.Options))!; }
        Assert.Equal(HttpStatusCode.OK,await Publish(mine[0]));
        // A legacy token (issued before tokens named their publisher) stays valid until it expires.
        var legacyId=Guid.NewGuid();
        var legacyPayload=Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { SessionId=legacyId,Expires=DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }));
        var legacy=new LiveSession(legacyId,legacyPayload+"."+Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(server.SigningKey),Encoding.UTF8.GetBytes(legacyPayload))),DateTimeOffset.UtcNow.AddDays(1),"");
        // Removing a key from the configuration revokes its tokens; other keys' sessions keep working after the restart.
        keys["LiveTiming__PublisherKeys"]="other:"+LivePublisherKey.Hash(other);
        await server.Kill(); await server.Start();
        Assert.Equal(HttpStatusCode.Unauthorized,await Publish(mine[0]));
        Assert.Equal(HttpStatusCode.OK,await Publish(theirs));
        Assert.Equal(HttpStatusCode.OK,await Publish(legacy));
    }
}

[Collection("Live timing processes")]
public sealed class ServerHardeningE2ETests
{
    [Theory]
    [InlineData("")]
    [InlineData("missing-hash")]
    [InlineData("short:abcd")]
    [InlineData("bad/name:0000000000000000000000000000000000000000000000000000000000000000")]
    public async Task ServerRefusesToStartWithoutValidPublisherKeys(string keys)
    {
        await using var server=new ServerProcess(environment:new Dictionary<string,string> { ["LiveTiming__PublisherKeys"]=keys });
        await Assert.ThrowsAsync<IOException>(server.Start);
    }

    [Fact]
    public async Task PublisherDeclaringAnotherProtocolIsRefusedWithoutCreatingSessions()
    {
        await using var server=new ServerProcess(); await server.Start();
        using var http=new HttpClient { BaseAddress=new(server.Endpoint) };
        using(var response=await http.GetAsync("health"))
        {
            response.EnsureSuccessStatusCode();
            Assert.Equal(LiveProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture),Assert.Single(response.Headers.GetValues(LiveProtocol.Header)));
            Assert.Equal(LiveProtocol.Version,(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("protocol").GetInt32());
        }
        using(var request=server.CreateSession())
        {
            request.Headers.Add(LiveProtocol.Header,(LiveProtocol.Version+1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response=await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.UpgradeRequired,response.StatusCode);
        }
        using(var request=server.CreateSession())
        {
            request.Headers.Add(LiveProtocol.Header,LiveProtocol.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response=await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        // Only the accepted request created a session; its state is still unpublished so the public list stays empty.
        Assert.Empty((await http.GetFromJsonAsync<LiveSessionSummary[]>("api/sessions",LiveJson.Options))!);
    }

    [Fact]
    public async Task PublisherReportsIncompatibleServerProtocolAsOwnValidationError()
    {
        using var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0); listener.Start();
        var port=((IPEndPoint)listener.LocalEndpoint).Port;
        var served=Task.Run(async()=>
        {
            using var client=await listener.AcceptTcpClientAsync();
            var stream=client.GetStream(); var buffer=new byte[8192]; var request=new StringBuilder();
            while(!request.ToString().Contains("\r\n\r\n",StringComparison.Ordinal)) { var read=await stream.ReadAsync(buffer); if(read==0) { break; } request.Append(Encoding.ASCII.GetString(buffer,0,read)); }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 426 Upgrade Required\r\nContent-Length: 6\r\nConnection: close\r\n\r\nsecret"));
            return request.ToString();
        });
        using var publisher=new OpenSkiTime.LiveTiming.Publishing.StandalonePublisher($"http://127.0.0.1:{port}",publisherKey:LivePublisherKey.Generate());
        var error=await Assert.ThrowsAsync<LiveValidationException>(()=>publisher.PublishAsync(SyntheticRace.Create(),true,CancellationToken.None));
        Assert.Contains("incompatible protocol version",error.Message,StringComparison.Ordinal);
        Assert.DoesNotContain("secret",error.Message,StringComparison.Ordinal);
        Assert.Contains($"{LiveProtocol.Header}: {LiveProtocol.Version}",await served,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SecurityTxtIsServedWithContactCanonicalAndFutureExpiry()
    {
        await using var server=new ServerProcess(); await server.Start();
        using var http=new HttpClient { BaseAddress=new(server.Endpoint) };
        using var response=await http.GetAsync(".well-known/security.txt");
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/plain",response.Content.Headers.ContentType!.MediaType);
        var text=await response.Content.ReadAsStringAsync();
        Assert.Contains("Contact: https://github.com/tnakeli/OpenSkiTime/security/advisories/new\n",text,StringComparison.Ordinal);
        Assert.Contains($"Canonical: {server.Endpoint}/.well-known/security.txt\n",text,StringComparison.Ordinal);
        var expires=DateTimeOffset.Parse(text.Split('\n').Single(x=>x.StartsWith("Expires: ",StringComparison.Ordinal))[9..],System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(expires,DateTimeOffset.UtcNow.AddDays(170),DateTimeOffset.UtcNow.AddDays(366));
    }

    [Fact]
    public async Task RequestBudgetIsPerClientAndForwardedAddressesAreIgnoredByDefault()
    {
        await using var server=new ServerProcess(environment:new Dictionary<string,string> { ["LiveTiming__RequestsPerMinute"]="60" }); await server.Start();
        using var http=new HttpClient { BaseAddress=new(server.Endpoint) };
        var statuses=new List<HttpStatusCode>();
        for(var i=0;i<61;i++)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"health");
            // A spoofed header must not mint a fresh budget unless the operator trusts a forwarding proxy.
            request.Headers.Add("X-Forwarded-For",$"203.0.113.{i}");
            using var response=await http.SendAsync(request); statuses.Add(response.StatusCode);
        }
        // Startup health polls share the loopback budget, so only the overall exhaustion is asserted exactly.
        Assert.Equal(HttpStatusCode.TooManyRequests,statuses[^1]);
        Assert.All(statuses.Take(50),x=>Assert.Equal(HttpStatusCode.OK,x));
    }

    [Fact]
    public async Task TrustedProxyClientAddressesGetSeparateBudgets()
    {
        await using var server=new ServerProcess(environment:new Dictionary<string,string> { ["LiveTiming__RequestsPerMinute"]="60", ["LiveTiming__TrustForwardedFor"]="true" }); await server.Start();
        using var http=new HttpClient { BaseAddress=new(server.Endpoint) };
        async Task<HttpStatusCode> Get(string client)
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,"health");
            // The trusted ingress appends the real address last; earlier client-supplied entries are ignored.
            request.Headers.Add("X-Forwarded-For","198.51.100.7, "+client);
            using var response=await http.SendAsync(request); return response.StatusCode;
        }
        for(var i=0;i<60;i++) { Assert.Equal(HttpStatusCode.OK,await Get("203.0.113.1")); }
        Assert.Equal(HttpStatusCode.TooManyRequests,await Get("203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK,await Get("203.0.113.2"));
    }
}
