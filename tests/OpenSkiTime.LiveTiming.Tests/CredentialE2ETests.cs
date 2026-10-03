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
        using var creation=await http.PostAsync("api/sessions",null);
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
        using(var response=await http.PostAsync("api/sessions",null)) { response.EnsureSuccessStatusCode(); }
        using(var response=await http.PostAsync("api/sessions",null)) { Assert.Equal(HttpStatusCode.ServiceUnavailable,response.StatusCode); }
    }
}
