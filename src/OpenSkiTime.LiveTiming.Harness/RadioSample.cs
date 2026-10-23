using System.Globalization;
using OpenSkiTime.LiveTiming.Client;
using OpenSkiTime.LiveTiming.Radio;
using OpenSkiTime.LiveTiming.Radio.Providers;

namespace OpenSkiTime.LiveTiming.Harness;

/// <summary>
/// Manual listening test: runs the radio pipeline over a synthetic race with fictional names and writes every clip and a
/// transcript outside the repository. Keys come from Windows Credential Manager (Settings → Live timing); this tool makes
/// real, billed provider calls and is never part of automated tests.
/// </summary>
public static class RadioSample
{
    // Fictional names chosen to exercise Finnish and foreign pronunciation; any match with a real person is accidental.
    private static readonly (string First, string Last, string Nation, string Club)[] Racers =
    [
        ("Aada", "LUMIVAARA", "FIN", "Rinneseura"), ("Veeti", "HANKIRANTA", "FIN", "Rinneseura"), ("Saga", "PORTTILA", "SWE", "Fjällklubben"),
        ("Lukas", "BERGHOFER", "AUT", "Skiclub Talblick"), ("Ilona", "KURVINEN", "FIN", "Tunturiklubi"), ("Onni", "VIISTOLA", "FIN", "Tunturiklubi"),
        ("Maja", "SNØHEIM", "NOR", "Fjellag"), ("Eetu", "KEPPIMÄKI", "FIN", "Rinneseura"), ("Noa", "LASKUVUORI", "FIN", "Tunturiklubi"),
        ("Elsa", "TÖRMÄLÄ", "FIN", "Rinneseura")
    ];

    public static async Task<int> RunAsync(string llmProvider, string llmModel, string ttsProvider, string output, string region, CancellationToken ct)
    {
        output = Path.GetFullPath(output);
        for (var directory = new DirectoryInfo(output); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "OpenSkiTime.slnx")))
            { Console.Error.WriteLine("Write listening-test clips outside the repository."); return 2; }
        }
        Directory.CreateDirectory(output);
        var configuration = new RadioConfiguration
        {
            LlmProvider = llmProvider == "templates" ? RadioProviders.Simulation : llmProvider, LlmModel = llmModel, TtsProvider = ttsProvider,
            TtsRegion = region, BudgetUsd = 2m, Curation = true, Lexicon = "Snøheim = Snööheim"
        };
        configuration.Validate();
        string? Key(string provider) => provider == RadioProviders.Simulation ? null : new WindowsCredentialStore(RadioKeyCredential.Target(provider)).Read();
        IRadioLlm llm = llmProvider == "templates" ? new FailingLlm() : RadioProviderFactory.Llm(configuration, Key(configuration.LlmProvider));
        var tts = RadioProviderFactory.Tts(configuration, Key(ttsProvider));
        var time = new SampleTime(new DateTimeOffset(2026, 10, 2, 9, 0, 0, TimeSpan.Zero));
        var sink = new FileSink(output);
        var engine = new RadioEngine(configuration, llm, tts, new RadioBudget(configuration.BudgetUsd, 0m), time, Console.WriteLine);
        engine.SetSinks([sink]);
        foreach (var state in Race(time))
        {
            engine.Offer(state);
            for (var i = 0; i < 4; i++) { time.Advance(TimeSpan.FromSeconds(5)); await engine.StepAsync(ct); }
        }
        await File.WriteAllLinesAsync(Path.Combine(output, "transcript.txt"), sink.Lines, ct);
        Console.WriteLine($"{sink.Lines.Count} clips written to {output}. Spent {engine.Budget.Spent.ToString("0.0000", CultureInfo.InvariantCulture)} USD.");
        (llm as IDisposable)?.Dispose(); (tts as IDisposable)?.Dispose();
        return 0;
    }
    private static IEnumerable<LiveSnapshot> Race(SampleTime time)
    {
        var competitors = Racers.Select((x, i) => new LiveCompetitor(i + 1, x.Last, x.First, x.Nation, x.Club, "")).ToArray();
        var at = time.GetUtcNow();
        var state = new LiveSnapshot(1, new("Synthetic Cup slalom", "Testitunturi", "SL", new(2026, 10, 2), false, "", "L", "U16", 2, "Harjoitusrinne"),
            competitors, 1, [new(1, at, competitors.Select(x => x.Bib).ToArray(), competitors.Select(c => new LiveResult(c.Bib, LiveStatus.Ready, null, null, null, at)).ToArray())], at);
        yield return state;
        long[] times = [4812, 4790, 4955, 4701, 0, 4888, 4720, -1, 5011, 4799];
        for (var bib = 1; bib <= competitors.Length; bib++)
        {
            yield return state = SyntheticRace.Update(state, bib, LiveStatus.OnCourse) with { UpdatedAt = time.GetUtcNow() };
            var value = times[bib - 1];
            state = value switch { 0 => SyntheticRace.Update(state, bib, LiveStatus.DNF), -1 => SyntheticRace.Update(state, bib, LiveStatus.DSQ), _ => SyntheticRace.Update(state, bib, LiveStatus.Finished, value) };
            yield return state = state with { UpdatedAt = time.GetUtcNow() };
        }
    }
    private sealed class FileSink(string folder) : IRadioClipSink
    {
        public List<string> Lines { get; } = [];
        public PublisherKind Channel => PublisherKind.Local;
        public string? Failure => null;
        public async Task UploadAsync(RadioClip clip, CancellationToken ct)
        {
            var name = $"{Lines.Count + 1:000}-{clip.Host}.{(clip.Audio.ContentType == "audio/wav" ? "wav" : "mp3")}";
            await File.WriteAllBytesAsync(Path.Combine(folder, name), clip.Audio.Data, ct);
            Lines.Add($"{name}\t{clip.Audio.DurationMs} ms\t{clip.Transcript}");
        }
        public Task HeartbeatAsync(RadioStatusUpdate status, CancellationToken ct) => Task.CompletedTask;
    }
    // Every model call fails, so the deterministic Finnish templates speak (the budget-only speech mode).
    private sealed class FailingLlm : IRadioLlm
    {
        public string Provider => RadioProviders.Simulation;
        public string Model => "simulation";
        public Task<LlmReply> CompleteAsync(LlmRequest request, CancellationToken ct) => throw new RadioProviderException("Templates only.");
    }
    private sealed class SampleTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
