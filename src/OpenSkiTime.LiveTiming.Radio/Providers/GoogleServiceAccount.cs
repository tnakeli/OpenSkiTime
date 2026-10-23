using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Radio.Providers;

/// <summary>
/// OAuth 2.0 access tokens for a Google Cloud service account (JSON key), for APIs that do not accept a plain API key,
/// such as Gemini speech models through Cloud Text-to-Speech. The JWT is signed locally with the account's private key
/// and exchanged at Google's token endpoint only; tokens are cached until shortly before they expire and never logged.
/// </summary>
public sealed class GoogleServiceAccount : IDisposable
{
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private readonly HttpClient _http;
    private readonly RSA _key;
    private readonly string _email;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    private GoogleServiceAccount(string email, string projectId, RSA key, HttpClient http, TimeProvider time)
    { _email = email; ProjectId = projectId; _key = key; _http = http; _time = time; }

    /// <summary>The project billed for requests (x-goog-user-project).</summary>
    public string ProjectId { get; }

    /// <summary>Whether a saved Google key is a service account JSON key rather than an API key.</summary>
    public static bool IsJson(string? key) => key?.TrimStart().StartsWith('{') == true;

    public static GoogleServiceAccount Parse(string json, HttpClient? http = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string Text(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            // A key file naming another token endpoint would send the signed assertion elsewhere: only Google's is used.
            if (Text("type") != "service_account" || Text("client_email").Length == 0 || Text("private_key").Length == 0
                || (Text("token_uri").Length > 0 && Text("token_uri") != TokenEndpoint))
            { throw new RadioProviderException("The Google key is not a usable service account JSON key."); }
            var rsa = RSA.Create();
            try { rsa.ImportFromPem(Text("private_key")); }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            { rsa.Dispose(); throw new RadioProviderException("The Google service account key has an unreadable private key."); }
            return new(Text("client_email"), Text("project_id"), rsa, http ?? RadioHttp.Create(), time ?? TimeProvider.System);
        }
        catch (JsonException) { throw new RadioProviderException("The Google key is not a usable service account JSON key."); }
    }

    public async Task<string> TokenAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = _time.GetUtcNow();
            if (_token is not null && now < _expires - TimeSpan.FromMinutes(5)) { return _token; }
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer", ["assertion"] = Assertion(now)
                })
            };
            using var response = await RadioHttp.SendAsync(_http, request, "Google sign-in", ct,
                "Google refused the service account sign-in. Check that the JSON key is current and the account is enabled.");
            var json = await RadioHttp.JsonAsync(response, "Google sign-in", ct);
            if (!json.TryGetProperty("access_token", out var token) || token.GetString() is not { Length: > 0 } value)
            { throw new RadioProviderException("Google sign-in returned no access token."); }
            var seconds = json.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var s) ? s : 3600;
            _token = value; _expires = now + TimeSpan.FromSeconds(Math.Clamp(seconds, 60, 3600));
            return value;
        }
        finally { _gate.Release(); }
    }

    private string Assertion(DateTimeOffset now)
    {
        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var issued = now.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = _email, scope = Scope, aud = TokenEndpoint, iat = issued, exp = issued + 3600 }));
        var signed = header + "." + claims;
        var signature = _key.SignData(Encoding.ASCII.GetBytes(signed), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return string.Create(CultureInfo.InvariantCulture, $"{signed}.{Base64Url(signature)}");
    }

    public void Dispose() { _key.Dispose(); _gate.Dispose(); _http.Dispose(); }
}
