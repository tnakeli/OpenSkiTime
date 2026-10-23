using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenSkiTime.LiveTiming.Radio.Providers;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

public sealed class GoogleServiceAccountTests
{
    // A key generated for the test only; the account and project are fictional.
    private static (string Json, RSA Key) Account(string? tokenUri = "https://oauth2.googleapis.com/token")
    {
        var rsa = RSA.Create(2048);
        var json = JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["type"] = "service_account", ["project_id"] = "synthetic-project", ["client_email"] = "radio@synthetic-project.iam.gserviceaccount.com",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(), ["token_uri"] = tokenUri
        });
        return (json, rsa);
    }

    [Fact]
    public async Task SignsAJwtForGoogleOnlyAndCachesTheToken()
    {
        var (json, rsa) = Account();
        using var _ = rsa;
        var requests = new List<(Uri Uri, string Body)>();
        using var http = new HttpClient(new Handler(async r =>
        {
            requests.Add((r.RequestUri!, await r.Content!.ReadAsStringAsync()));
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"access_token\":\"synthetic-token\",\"expires_in\":3600}", Encoding.UTF8, "application/json") };
        }));
        using var account = GoogleServiceAccount.Parse(json, http);
        Assert.True(GoogleServiceAccount.IsJson(json));
        Assert.False(GoogleServiceAccount.IsJson("AIzaSynthetic-api-key-value"));
        Assert.Equal("synthetic-project", account.ProjectId);
        Assert.Equal("synthetic-token", await account.TokenAsync(CancellationToken.None));
        Assert.Equal("synthetic-token", await account.TokenAsync(CancellationToken.None));
        var (uri, body) = Assert.Single(requests); // Cached until shortly before it expires.
        Assert.Equal("https://oauth2.googleapis.com/token", uri.AbsoluteUri);
        var form = body.Split('&').Select(x => x.Split('=')).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
        var parts = form["assertion"].Split('.');
        static byte[] Decode(string x) => Convert.FromBase64String(x.Replace('-', '+').Replace('_', '/').PadRight(x.Length + (4 - x.Length % 4) % 4, '='));
        Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), Decode(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var claims = JsonDocument.Parse(Decode(parts[1]));
        Assert.Equal("radio@synthetic-project.iam.gserviceaccount.com", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("https://www.googleapis.com/auth/cloud-platform", claims.RootElement.GetProperty("scope").GetString());
    }

    [Theory]
    [InlineData("https://attacker.invalid/token")]
    [InlineData("not-json")]
    public void RefusesKeysThatWouldSendTheAssertionElsewhereOrAreUnreadable(string variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var (json, rsa) = Account(variant.StartsWith("https", StringComparison.Ordinal) ? variant : null);
        using var _ = rsa;
        Assert.Throws<RadioProviderException>(() => GoogleServiceAccount.Parse(variant == "not-json" ? "{not json" : json));
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request); }
}
