namespace OpenSkiTime.LiveTiming.Radio;

public sealed record RadioVoice(string Provider, string Name, double Rate = 1.0, string Style = "");

/// <summary>Fictional hosts in a two-host format inspired by mikradio. Alpine fields often include juniors, so both are respectful.</summary>
public sealed record RadioPersona(string Name, string Role, string Style, IReadOnlyDictionary<string, RadioVoice> Voices);

public static class RadioPersonas
{
    public static readonly RadioPersona[] Hosts =
    [
        new("Aino", "selostaja", "Rauhallinen, täsmällinen ja lämmin pääselostaja. Kertoo tapahtumat selkeästi ja lyhyesti, ensin olennainen.",
            new Dictionary<string, RadioVoice>
            {
                [RadioProviders.Azure] = new(RadioProviders.Azure, "fi-FI-SelmaNeural", 1.08),
                [RadioProviders.Google] = new(RadioProviders.Google, "fi-FI-Chirp3-HD-Kore", 1.05),
                // ElevenLabs default library voices (voice IDs); any voice of the account can be set per host in the panel.
                [RadioProviders.ElevenLabs] = new(RadioProviders.ElevenLabs, "EXAVITQu4vr4xnSDxMaL", 1.05),
                [RadioProviders.GoogleGemini] = new(RadioProviders.GoogleGemini, "Kore", 1.0, "Lue naisen äänellä suomeksi täsmälleen sellaisenaan, rauhallisena ja lämpimänä urheiluselostajana"),
                [RadioProviders.OpenAi] = new(RadioProviders.OpenAi, "coral", 1.05, "Calm, clear and warm Finnish sports radio presenter. Read the text exactly as written."),
                [RadioProviders.Simulation] = new(RadioProviders.Simulation, "a")
            }),
        new("Kalle", "kommentaattori", "Innostunut mutta asiallinen asiantuntijakommentaattori. Tuo tunnelmaa ja taustaa, kunnioittaa jokaista laskijaa.",
            new Dictionary<string, RadioVoice>
            {
                [RadioProviders.Azure] = new(RadioProviders.Azure, "fi-FI-HarriNeural", 1.1),
                [RadioProviders.Google] = new(RadioProviders.Google, "fi-FI-Chirp3-HD-Charon", 1.08),
                [RadioProviders.ElevenLabs] = new(RadioProviders.ElevenLabs, "JBFqnCBsd6RMkjVDRZzb", 1.08),
                [RadioProviders.GoogleGemini] = new(RadioProviders.GoogleGemini, "Charon", 1.0, "Lue syvällä miehen äänellä suomeksi täsmälleen sellaisenaan, innostuneena mutta asiallisena hiihtokommentaattorina"),
                [RadioProviders.OpenAi] = new(RadioProviders.OpenAi, "ash", 1.08, "Enthusiastic but respectful Finnish ski commentator. Read the text exactly as written."),
                [RadioProviders.Simulation] = new(RadioProviders.Simulation, "b")
            })
    ];
    public static RadioPersona Host(int index) => Hosts[index % Hosts.Length];
    public static RadioPersona? Find(string name) => Hosts.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    /// <summary>The voice for this host and provider; an operator voice name replaces the default.</summary>
    public static RadioVoice Voice(RadioPersona host, string provider, string? configured)
    {
        ArgumentNullException.ThrowIfNull(host);
        var voice = host.Voices[provider];
        return string.IsNullOrWhiteSpace(configured) ? voice : voice with { Name = configured.Trim() };
    }
}
