namespace OpenSkiTime.LiveTiming.Radio;

public sealed record LlmPrice(string Provider, string Model, decimal InputPerMillion, decimal OutputPerMillion);
/// <summary>Speech price per million characters, or per million text and audio tokens for token-billed models.</summary>
public sealed record TtsPrice(string Provider, string Model, decimal PerMillionCharacters, decimal TextTokensPerMillion = 0m, decimal AudioTokensPerMillion = 0m, int AudioTokensPerSecond = 0);

/// <summary>
/// List prices in USD as published by the providers on <see cref="Date"/> (see docs/race-radio.md for sources). Free tiers
/// are ignored, so the budget is conservative. Update the table and its date when prices change.
/// </summary>
public static class RadioPrices
{
    public static readonly DateOnly Date = new(2026, 10, 23);
    public static readonly LlmPrice[] Llm =
    [
        new(RadioProviders.OpenAi, "gpt-4.1-mini", 0.40m, 1.60m),
        new(RadioProviders.OpenAi, "gpt-4.1-nano", 0.10m, 0.40m),
        new(RadioProviders.OpenAi, "gpt-4o-mini", 0.15m, 0.60m),
        new(RadioProviders.Gemini, "gemini-2.5-flash", 0.30m, 2.50m),
        new(RadioProviders.Gemini, "gemini-2.5-flash-lite", 0.10m, 0.40m),
        new(RadioProviders.Simulation, "simulation", 0m, 0m)
    ];
    public static readonly TtsPrice[] Tts =
    [
        new(RadioProviders.Azure, "neural", 15m),
        new(RadioProviders.Google, "chirp3-hd", 30m),
        // Gemini 3.8 Flash TTS: $1 / 1M text and $18 / 1M audio tokens (25 per second), the regular rates. The promotional
        // half price until 2026-12-31 is not used, so the hard limit never undercounts once it ends.
        new(RadioProviders.GoogleGemini, "gemini-3.8-flash-tts", 0m, 1m, 18m, 25),
        // ElevenLabs Eleven v4: $0.08 per 1,000 characters (API pricing page, 2026-10-24).
        new(RadioProviders.ElevenLabs, "eleven_v4", 80m),
        new(RadioProviders.ElevenLabs, "eleven_v4_turbo", 40m),
        // gpt-4o-mini-tts bills text input and audio output tokens; audio is estimated at 21 tokens per second ($12/1M ≈ $0.015/min).
        new(RadioProviders.OpenAi, "gpt-4o-mini-tts", 0m, 0.60m, 12m, 21),
        new(RadioProviders.Simulation, "simulation", 0m)
    ];
    public static LlmPrice? ForLlm(string provider, string model) => Llm.FirstOrDefault(x => x.Provider == provider && x.Model == model);
    /// <summary>The price of the provider's model, or of its default model when the model is not priced separately.</summary>
    public static TtsPrice ForTts(string provider, string model = "") =>
        Tts.FirstOrDefault(x => x.Provider == provider && x.Model == model) ?? Tts.First(x => x.Provider == provider);
    public static decimal Cost(LlmPrice price, long inputTokens, long outputTokens)
    {
        ArgumentNullException.ThrowIfNull(price);
        return (inputTokens * price.InputPerMillion + outputTokens * price.OutputPerMillion) / 1_000_000m;
    }
    public static decimal Cost(TtsPrice price, int characters, int durationMs)
    {
        ArgumentNullException.ThrowIfNull(price);
        // Token-billed speech: text tokens ≈ characters / 3, audio tokens from the measured (or estimated) duration.
        var audioTokens = (long)Math.Ceiling(durationMs / 1000m * price.AudioTokensPerSecond);
        return (characters * price.PerMillionCharacters + (characters + 2) / 3 * price.TextTokensPerMillion + audioTokens * price.AudioTokensPerMillion) / 1_000_000m;
    }
    /// <summary>Spoken Finnish runs at roughly 14 characters a second; 12 overestimates duration and so cost.</summary>
    public static int EstimatedDurationMs(int characters) => characters * 1000 / 12;
}

public enum RadioMode { Full, Lean, TemplatesOnly, Exhausted }

/// <summary>
/// Spending limit with a hard stop. Every billable call is checked against its estimated cost before it is made and charged
/// with the provider-reported usage (or counted characters and estimated tokens) afterwards. Spend can exceed the limit
/// only by the estimation error of the last call.
/// </summary>
public sealed class RadioBudget(decimal limit, decimal spent)
{
    private readonly Lock _gate = new();
    private decimal _spent = spent;
    public decimal Limit { get; } = limit;
    public decimal Spent { get { lock (_gate) { return _spent; } } }
    public decimal Remaining => Math.Max(0m, Limit - Spent);
    public bool CanAfford(decimal estimate) => Spent + estimate <= Limit;
    public void Charge(decimal amount) { if (amount > 0m) { lock (_gate) { _spent += amount; } } }
    /// <summary>Replaces a reservation made with <see cref="Charge"/> by the provider-reported (or counted) cost.</summary>
    public void Settle(decimal reserved, decimal actual) { lock (_gate) { _spent += Math.Max(0m, actual) - reserved; } }

    /// <summary>
    /// Full: dialogue, curation and producer. Lean (under 20 % left): no curation or producer model calls. TemplatesOnly
    /// (under 5 % left or the reserve for one more dialogue is gone): deterministic phrases and speech only.
    /// </summary>
    public RadioMode Mode(decimal dialogueEstimate, decimal speechEstimate)
    {
        var remaining = Remaining;
        if (Limit <= 0m || remaining < speechEstimate) { return RadioMode.Exhausted; }
        if (remaining < Limit * 0.05m || remaining < dialogueEstimate + speechEstimate) { return RadioMode.TemplatesOnly; }
        return remaining < Limit * 0.20m ? RadioMode.Lean : RadioMode.Full;
    }
    /// <summary>Idle commentary slows down as the budget is used: 60 s at the start, up to 240 s near the limit.</summary>
    public TimeSpan ProducerInterval
    {
        get
        {
            var used = Limit <= 0m ? 1m : Math.Min(1m, Spent / Limit);
            return TimeSpan.FromSeconds(60 * (1 + (double)used * 3));
        }
    }
}
