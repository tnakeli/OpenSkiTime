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
        using(var request=server.CreateSession())
        using(var response=await http.SendAsync(request)) { response.EnsureSuccessStatusCode(); }
        using(var request=server.CreateSession())
        using(var response=await http.SendAsync(request)) { Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); }
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
