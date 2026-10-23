using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Radio.Providers;

/// <summary>
/// Offline, cost-free providers for rehearsals and tests: valid slot lines for the events and short tone clips. They make
/// no network calls and never leave this computer.
/// </summary>
public sealed class SimulationLlm : IRadioLlm
{
    public string Provider => RadioProviders.Simulation;
    public string Model => "simulation";
    public Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string text;
        if (request.System.StartsWith("You are the producer", StringComparison.Ordinal))
        { text = """{"source":"general","bib":null,"instruction":"Lyhyt tunnelmapuheenvuoro."}"""; }
        else if (request.System.StartsWith("You review", StringComparison.Ordinal))
        {
            using var lines = JsonDocument.Parse(request.User);
            text = JsonSerializer.Serialize(new { lines = lines.RootElement.GetProperty("lines"), patterns = Array.Empty<string>() });
        }
        else
        {
            using var prompt = JsonDocument.Parse(request.User);
            var bibs = prompt.RootElement.GetProperty("events").EnumerateArray().SelectMany(x => x.GetProperty("bibs").EnumerateArray().Select(b => b.GetInt32())).Distinct().Take(3);
            var lines = bibs.Select((bib, i) => new { host = RadioPersonas.Host(i).Name, text = $"Simulaatiossa mukana {{racer:{bib}}}.", interruptible = true }).ToList();
            if (lines.Count == 0) { lines.Add(new { host = RadioPersonas.Host(0).Name, text = "Tämä on radion simulaatio.", interruptible = true }); }
            text = JsonSerializer.Serialize(new { topic = "simulaatio", lines });
        }
        return Task.FromResult(new LlmReply(text, request.System.Length / 3, text.Length / 3));
    }
}

public sealed class SimulationTts : IRadioTts
{
    public string Provider => RadioProviders.Simulation;
    public Task<TtsAudio> SynthesizeAsync(string text, RadioVoice voice, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentNullException.ThrowIfNull(voice);
        var duration = Math.Clamp(RadioPrices.EstimatedDurationMs(text.Length), 500, 6000);
        return Task.FromResult(new TtsAudio(Tone(duration, voice.Name == "b" ? 523 : 440), "audio/wav", duration));
    }
    /// <summary>8 kHz 8-bit mono WAV: a quiet 150 ms tone, then silence for the rest of the line.</summary>
    public static byte[] Tone(int durationMs, int frequency)
    {
        const int rate = 8000;
        var samples = rate * durationMs / 1000;
        var data = new byte[44 + samples];
        void Write(int offset, int value, int bytes) { for (var i = 0; i < bytes; i++) { data[offset + i] = (byte)(value >> (8 * i)); } }
        "RIFF"u8.CopyTo(data); Write(4, 36 + samples, 4); "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        Write(16, 16, 4); Write(20, 1, 2); Write(22, 1, 2); Write(24, rate, 4); Write(28, rate, 4); Write(32, 1, 2); Write(34, 8, 2);
        "data"u8.CopyTo(data.AsSpan(36)); Write(40, samples, 4);
        for (var i = 0; i < samples; i++)
        { data[44 + i] = (byte)(128 + (i < rate * 150 / 1000 ? (int)(24 * Math.Sin(2 * Math.PI * frequency * i / rate)) : 0)); }
        return data;
    }
}

/// <summary>Creates the configured providers. Keys come from the panel over private IPC, never from arguments or files.</summary>
public static class RadioProviderFactory
{
    public static IRadioLlm Llm(RadioConfiguration configuration, string? key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (RadioPrices.ForLlm(configuration.LlmProvider, configuration.LlmProvider == RadioProviders.Simulation ? "simulation" : configuration.LlmModel) is null)
        { throw new RadioProviderException("Choose a language model from the price table so spending can be limited."); }
        return configuration.LlmProvider switch
        {
            RadioProviders.OpenAi => new OpenAiLlm(key, configuration.LlmModel),
            RadioProviders.Gemini => new GeminiLlm(key, configuration.LlmModel),
            _ => new SimulationLlm()
        };
    }
    public static IRadioTts Tts(RadioConfiguration configuration, string? key)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.TtsProvider switch
        {
            RadioProviders.Azure => new AzureTts(key, configuration.TtsRegion),
            RadioProviders.Google => new GoogleTts(key),
            RadioProviders.OpenAi => new OpenAiTts(key),
            _ => new SimulationTts()
        };
    }
}
