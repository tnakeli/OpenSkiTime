using System.Globalization;
using System.Net.Http.Json;
using System.Security;
using System.Text;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Radio.Providers;

/// <summary>
/// Direct REST calls without vendor SDKs. Redirects are disabled, keys travel only in request headers, and errors never
/// include response bodies. The caller supplies cancellation with its timeout.
/// </summary>
internal static class RadioHttp
{
    public static HttpClient Create() => new(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
    { Timeout = Timeout.InfiniteTimeSpan };
    // rejected: the message for HTTP 401/403 when the key is not the only possible cause.
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string name, CancellationToken ct,
        string? rejected = null)
    {
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct); }
        catch (HttpRequestException) { throw new RadioProviderException($"{name} could not be reached."); }
        if (response.IsSuccessStatusCode) { return response; }
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new RadioProviderException(status switch
        {
            401 or 403 => rejected ?? $"{name} rejected the API key. Check Settings → Live timing.",
            429 => $"{name} rate limit or quota reached.",
            _ => $"{name} returned HTTP {status.ToString(CultureInfo.InvariantCulture)}."
        });
    }
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, string name, CancellationToken ct)
    {
        try { return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone(); }
        catch (JsonException) { throw new RadioProviderException($"{name} returned an unreadable response."); }
    }
    public static string RequireKey(string? key, string name) => string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl)
        ? throw new RadioProviderException($"No {name} API key is saved. Add it in Settings → Live timing.") : key.Trim();
}

public sealed class OpenAiLlm(string? apiKey, string model, Uri? endpoint = null) : IRadioLlm, IDisposable
{
    private readonly HttpClient _http = RadioHttp.Create();
    private readonly string _key = RadioHttp.RequireKey(apiKey, "OpenAI");
    public string Provider => RadioProviders.OpenAi;
    public string Model => model;
    public async Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint ?? new Uri("https://api.openai.com/"), "v1/chat/completions"))
        {
            Content = JsonContent.Create(new
            {
                model, temperature = request.Temperature, max_completion_tokens = request.MaxOutputTokens,
                response_format = new { type = "json_object" },
                messages = new[] { new { role = "system", content = request.System }, new { role = "user", content = request.User } }
            })
        };
        message.Headers.Authorization = new("Bearer", _key);
        using var response = await RadioHttp.SendAsync(_http, message, "OpenAI", ct);
        var json = await RadioHttp.JsonAsync(response, "OpenAI", ct);
        try
        {
            var text = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            var usage = json.GetProperty("usage");
            return new(text, usage.GetProperty("prompt_tokens").GetInt64(), usage.GetProperty("completion_tokens").GetInt64());
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new RadioProviderException("OpenAI returned an unexpected response."); }
    }
    public void Dispose() => _http.Dispose();
}

public sealed class GeminiLlm(string? apiKey, string model) : IRadioLlm, IDisposable
{
    private readonly HttpClient _http = RadioHttp.Create();
    private readonly string _key = RadioHttp.RequireKey(apiKey, "Gemini");
    public string Provider => RadioProviders.Gemini;
    public string Model => model;
    public async Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent")
        {
            Content = JsonContent.Create(new
            {
                systemInstruction = new { parts = new[] { new { text = request.System } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = request.User } } } },
                // No thinking: latency matters more than reasoning for short scripts, and thinking tokens are billed as output.
                generationConfig = new { responseMimeType = "application/json", temperature = request.Temperature, maxOutputTokens = request.MaxOutputTokens,
                    thinkingConfig = new { thinkingBudget = 0 } }
            })
        };
        message.Headers.Add("x-goog-api-key", _key);
        using var response = await RadioHttp.SendAsync(_http, message, "Gemini", ct);
        var json = await RadioHttp.JsonAsync(response, "Gemini", ct);
        try
        {
            var parts = json.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
            var text = string.Concat(parts.EnumerateArray().Select(x => x.TryGetProperty("text", out var t) ? t.GetString() : ""));
            var usage = json.GetProperty("usageMetadata");
            long Read(string name) => usage.TryGetProperty(name, out var value) ? value.GetInt64() : 0;
            return new(text, Read("promptTokenCount"), Read("candidatesTokenCount") + Read("thoughtsTokenCount"));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        { throw new RadioProviderException("Gemini returned an unexpected response."); }
    }
    public void Dispose() => _http.Dispose();
}

/// <summary>Azure AI Speech neural voices (fi-FI Selma, Noora, Harri). Classic neural speech reads the text verbatim.</summary>
public sealed class AzureTts(string? apiKey, string region) : IRadioTts, IDisposable
{
    private readonly HttpClient _http = RadioHttp.Create();
    private readonly string _key = RadioHttp.RequireKey(apiKey, "Azure Speech");
    public string Provider => RadioProviders.Azure;
    public async Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(voice);
        if (string.IsNullOrEmpty(region) || !region.All(char.IsAsciiLetterOrDigit)) { throw new RadioProviderException("Enter the Azure Speech region, for example westeurope."); }
        var rate = ((voice.Rate - 1) * 100).ToString("+0;-0;+0", CultureInfo.InvariantCulture) + "%";
        var ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='fi-FI'><voice name='{SecurityElement.Escape(voice.Name)}'>"
            + $"<prosody rate='{rate}'>{SecurityElement.Escape(text)}</prosody></voice></speak>";
        using var message = new HttpRequestMessage(HttpMethod.Post, $"https://{region.ToLowerInvariant()}.tts.speech.microsoft.com/cognitiveservices/v1")
        { Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml") };
        message.Headers.Add("Ocp-Apim-Subscription-Key", _key);
        message.Headers.Add("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        message.Headers.UserAgent.ParseAdd("OpenSkiTime");
        // An Azure key works only in its resource's region, so a wrong region looks like a rejected key.
        using var response = await RadioHttp.SendAsync(_http, message, "Azure Speech", ct,
            $"Azure Speech rejected the key in region {region}. Check that the Azure region in the radio settings is the Speech resource's region, and the key in Settings → Live timing.");
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return new(data, "audio/mpeg", AudioDuration.Milliseconds(data, "audio/mpeg"));
    }
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Gemini speech models through Vertex AI (Agent Platform) generateContent. A language-model voice: the style prefix asks
/// it to read the text exactly, but it may still vary words. Vertex AI needs an identity with Vertex AI User: an API key
/// bound to a service account (restricted to the Agent Platform API) or a service account JSON key (OAuth). The PCM
/// output is resampled to 16 kHz mono WAV, which keeps a long line well within the clip size limit.
/// </summary>
public sealed class GeminiCloudTts : IRadioTts, IDisposable
{
    private const int Rate = 16000;
    private static readonly string[] Audio = ["AUDIO"];
    private readonly HttpClient _http = RadioHttp.Create();
    private readonly string? _key;
    private readonly GoogleServiceAccount? _account;
    private readonly string _model;
    public GeminiCloudTts(string? apiKey, string model)
    {
        _model = model;
        if (GoogleServiceAccount.IsJson(apiKey)) { _account = GoogleServiceAccount.Parse(apiKey!); }
        else { _key = RadioHttp.RequireKey(apiKey, "Google Gemini"); }
    }
    public string Provider => RadioProviders.GoogleGemini;
    // Vertex AI quotas for speech models are low; one short retry absorbs a burst of parallel lines.
    public async Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        try { return await SynthesizeOnceAsync(text, voice, ct); }
        catch (RadioProviderException ex) when (ex.Message.EndsWith("rate limit or quota reached.", StringComparison.Ordinal))
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            return await SynthesizeOnceAsync(text, voice, ct);
        }
    }
    private async Task<TtsAudio> SynthesizeOnceAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(voice);
        var path = _account is null ? "" : $"projects/{Uri.EscapeDataString(_account.ProjectId)}/locations/global/";
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"https://aiplatform.googleapis.com/v1/{path}publishers/google/models/{Uri.EscapeDataString(_model)}:generateContent")
        {
            Content = JsonContent.Create(new
            {
                // "Style: text" is the documented way to steer Gemini speech without the instruction being read aloud.
                contents = new[] { new { role = "user", parts = new[] { new { text = $"{voice.Style}: {text}" } } } },
                generationConfig = new
                {
                    responseModalities = Audio,
                    speechConfig = new { voiceConfig = new { prebuiltVoiceConfig = new { voiceName = voice.Name } } }
                }
            })
        };
        if (_account is not null) { message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await _account.TokenAsync(ct)); }
        else { message.Headers.Add("X-Goog-Api-Key", _key); }
        using var response = await RadioHttp.SendAsync(_http, message, "Google Gemini speech", ct,
            "Google Gemini speech was refused. Use an API key bound to a service account with the Vertex AI User role (Agent Platform API), saved as the Google Gemini key in Settings → Live timing.");
        var json = await RadioHttp.JsonAsync(response, "Google Gemini speech", ct);
        try
        {
            var audio = json.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray()
                .Select(x => x.TryGetProperty("inlineData", out var data) ? data : default).First(x => x.ValueKind == JsonValueKind.Object);
            var type = audio.GetProperty("mimeType").GetString() ?? "";
            var rate = type.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("rate=", StringComparison.Ordinal)) is { } r
                && int.TryParse(r[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : 24000;
            if (!type.StartsWith("audio/L16", StringComparison.OrdinalIgnoreCase) || rate is < 8000 or > 48000)
            { throw new RadioProviderException("Google Gemini speech returned an unexpected audio format."); }
            var wav = Pcm16.Wav(Pcm16.Resample(Convert.FromBase64String(audio.GetProperty("data").GetString() ?? ""), rate, Rate), Rate);
            return new(wav, "audio/wav", AudioDuration.Milliseconds(wav, "audio/wav"));
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        { throw new RadioProviderException("Google Gemini speech returned an unexpected response."); }
    }
    public void Dispose() { _http.Dispose(); _account?.Dispose(); }
}

/// <summary>16-bit little-endian mono PCM helpers.</summary>
public static class Pcm16
{
    /// <summary>Linear-interpolation resampling; speech above 8 kHz carries little, so 16 kHz keeps it clear.</summary>
    public static byte[] Resample(byte[] pcm, int from, int to)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (from == to || pcm.Length < 4) { return pcm; }
        var input = pcm.Length / 2;
        var output = (int)((long)input * to / from);
        var result = new byte[output * 2];
        short Sample(int i) => (short)(pcm[i * 2] | pcm[i * 2 + 1] << 8);
        for (var i = 0; i < output; i++)
        {
            var position = (double)i * from / to;
            var left = Math.Min((int)position, input - 1); var right = Math.Min(left + 1, input - 1);
            var value = (short)Math.Round(Sample(left) + (Sample(right) - Sample(left)) * (position - left));
            result[i * 2] = (byte)value; result[i * 2 + 1] = (byte)(value >> 8);
        }
        return result;
    }
    public static byte[] Wav(byte[] pcm, int rate)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        var data = new byte[44 + pcm.Length];
        void Write(int offset, int value, int bytes) { for (var i = 0; i < bytes; i++) { data[offset + i] = (byte)(value >> (8 * i)); } }
        "RIFF"u8.CopyTo(data); Write(4, 36 + pcm.Length, 4); "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        Write(16, 16, 4); Write(20, 1, 2); Write(22, 1, 2); Write(24, rate, 4); Write(28, rate * 2, 4); Write(32, 2, 2); Write(34, 16, 2);
        "data"u8.CopyTo(data.AsSpan(36)); Write(40, pcm.Length, 4);
        pcm.CopyTo(data, 44);
        return data;
    }
}

/// <summary>Google Cloud Text-to-Speech, Chirp 3: HD voices (fi-FI). Finnish has no custom pronunciation support there.</summary>
public sealed class GoogleTts(string? apiKey) : IRadioTts, IDisposable
{
    private readonly HttpClient _http = RadioHttp.Create();
    private readonly string _key = RadioHttp.RequireKey(apiKey, "Google Cloud Text-to-Speech");
    public string Provider => RadioProviders.Google;
    public async Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(voice);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://texttospeech.googleapis.com/v1/text:synthesize")
        {
            Content = JsonContent.Create(new
            {
                input = new { text }, voice = new { languageCode = "fi-FI", name = voice.Name },
                audioConfig = new { audioEncoding = "MP3", speakingRate = voice.Rate }
            })
        };
        message.Headers.Add("X-Goog-Api-Key", _key);
        using var response = await RadioHttp.SendAsync(_http, message, "Google Cloud Text-to-Speech", ct);
        var json = await RadioHttp.JsonAsync(response, "Google Cloud Text-to-Speech", ct);
        byte[] data;
        try { data = Convert.FromBase64String(json.GetProperty("audioContent").GetString() ?? ""); }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        { throw new RadioProviderException("Google Cloud Text-to-Speech returned an unexpected response."); }
        return new(data, "audio/mpeg", AudioDuration.Milliseconds(data, "audio/mpeg"));
    }
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// OpenAI gpt-4o-mini-tts. A language-model voice that takes style instructions; it may alter words, so it needs a
/// listening test for word fidelity before use.
/// </summary>
public sealed class OpenAiTts(string? apiKey, Uri? endpoint = null) : IRadioTts, IDisposable
{
    private readonly HttpClient _http = RadioHttp.Create();
    private readonly string _key = RadioHttp.RequireKey(apiKey, "OpenAI");
    public string Provider => RadioProviders.OpenAi;
    public async Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(voice);
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint ?? new Uri("https://api.openai.com/"), "v1/audio/speech"))
        {
            Content = JsonContent.Create(new
            {
                model = "gpt-4o-mini-tts", voice = voice.Name, input = text, response_format = "mp3",
                instructions = (voice.Style + " Speak Finnish. Read the input exactly as written; do not add, drop or change words.").Trim()
            })
        };
        message.Headers.Authorization = new("Bearer", _key);
        using var response = await RadioHttp.SendAsync(_http, message, "OpenAI speech", ct);
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return new(data, "audio/mpeg", AudioDuration.Milliseconds(data, "audio/mpeg"));
    }
    public void Dispose() => _http.Dispose();
}
