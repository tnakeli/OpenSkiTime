using System.Globalization;
using OpenSkiTime.LiveTiming.Client;

namespace OpenSkiTime.LiveTiming.Harness;

/// <summary>
/// A real-time synthetic two-run giant slalom published to a live timing server with the race radio, for listening
/// tests with real viewers. 60 % of the field is Finnish, the rest from other European nations; runs take 45-50 s with
/// about two racers on course and a quarter of the starts end in DNF. Every name and club is fictional; any match with a
/// real person is accidental. Keys come from Windows Credential Manager; real providers are billed.
/// </summary>
public static class RadioRace
{
    private static readonly string[] FinnishFirst = ["Aada", "Veeti", "Ilona", "Onni", "Eetu", "Noa", "Elsa", "Lumi", "Eino", "Venla",
        "Oiva", "Siiri", "Topi", "Helmi", "Väinö", "Pihla", "Urho", "Kerttu"];
    private static readonly string[] FinnishLast = ["LUMIVAARA", "HANKIRANTA", "VIISTOLA", "KEPPIMÄKI", "LASKUVUORI", "TÖRMÄLÄ", "VAUHTINIEMI",
        "KAARRESALO", "PORTTIMAA", "RINNEKANGAS", "PAKKASVIRTA", "HUURRELA", "NIETOSMAA", "SAUVAKOSKI", "KUMPARELAINEN", "PIPOLAINEN",
        "TYKKYLÄ", "LIUKASKALLIO"];
    private static readonly string[] FinnishClubs = ["Rinneseura", "Tunturiklubi", "Lumiveikot", "Pujotteluseura Porras", "Kumpareen Kiitäjät"];
    private static readonly (string First, string Last, string Nation, string Club)[] Foreign =
    [
        ("Göran", "SJÖBERG", "SWE", "Fjällklubben"), ("Kerstin", "LJUNGQVIST", "SWE", "Backens IF"), ("Maja", "SNØHEIM", "NOR", "Fjellaget"),
        ("Ola", "FJELLBAKKEN", "NOR", "Bakkelaget"), ("Volker", "STEINWEGER", "GER", "SC Bergwind"), ("Franziska", "PULVERSCHNEE", "GER", "SC Bergwind"),
        ("Sepp", "GIPFELSTÜRMER", "AUT", "SC Talblick"), ("Reto", "MATTERLI", "SUI", "SC Gletscher"), ("Giacomo", "CECCHINATO", "ITA", "Sci Club Neve"),
        ("Chiara", "DISCESA", "ITA", "Sci Club Neve"), ("Jean-Baptiste", "POUDREUSE", "FRA", "Club des Neiges"), ("Michał", "SZCZEPAŃSKI", "POL", "KS Śnieg"),
        ("Jiří", "SNĚHULÁK", "CZE", "SK Sníh"), ("Guillermo", "JIMÉNEZ", "ESP", "Club Nieve"), ("Søren", "BAKKEGAARD", "DEN", "Skiklubben"),
        ("Tine", "ČEBULJ", "SLO", "SK Sneg")
    ];

    public static async Task<int> RunAsync(string llmProvider, string llmModel, string ttsProvider, string endpoint, int count, string workerDll,
        string radioWorkerDll, string musicFolder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(musicFolder); ArgumentNullException.ThrowIfNull(llmProvider); ArgumentNullException.ThrowIfNull(ttsProvider);
        var random = new Random(20261023);
        var finns = (int)Math.Round(count * 0.6);
        var competitors = new List<LiveCompetitor>();
        var firstNames = FinnishFirst.OrderBy(_ => random.Next()).ToArray(); var lastNames = FinnishLast.OrderBy(_ => random.Next()).ToArray();
        for (var i = 0; i < finns; i++)
        { competitors.Add(new(0, lastNames[i % lastNames.Length], firstNames[i % firstNames.Length], "FIN", FinnishClubs[random.Next(FinnishClubs.Length)], "")); }
        foreach (var x in Foreign.OrderBy(_ => random.Next()).Take(count - finns)) { competitors.Add(new(0, x.Last, x.First, x.Nation, x.Club, "")); }
        // Bibs in a mixed order, as a draw would give.
        var field = competitors.OrderBy(_ => random.Next()).Select((x, i) => x with { Bib = i + 1 }).ToArray();
        var skill = field.ToDictionary(x => x.Bib, _ => random.Next(-150, 220));

        var key = PublisherKeyCredential.Target(endpoint);
        var publisherKey = new WindowsCredentialStore(key).Read() ?? throw new InvalidOperationException("Save the Cloud publisher key in OpenSkiTime Settings → Live timing first.");
        // A service account JSON key (needed for Gemini voices) is read from a file outside the repository, named by
        // OST_GOOGLE_SERVICE_ACCOUNT, because it does not fit in Credential Manager.
        var account = Environment.GetEnvironmentVariable("OST_GOOGLE_SERVICE_ACCOUNT");
        string? RadioKey(string provider) => provider == RadioProviders.Simulation ? null
            : provider == RadioProviders.GoogleGemini && !string.IsNullOrWhiteSpace(account) ? File.ReadAllText(account)
            : RadioKeyCredential.Read(provider.Split(':')[0], x => new WindowsCredentialStore(x)) ?? throw new InvalidOperationException($"Save the {provider} radio key in Settings first.");
        var configuration = new RadioConfiguration
        {
            CloudEnabled = true, BudgetUsd = 3m, LlmProvider = llmProvider, LlmModel = llmModel, Curation = true,
            // "elevenlabs:eleven_v4_turbo" picks a provider's model.
            TtsProvider = ttsProvider.Split(':')[0], TtsModel = ttsProvider.Contains(':', StringComparison.Ordinal) ? ttsProvider.Split(':')[1] : "",
            MusicFolder = musicFolder.Length == 0 ? "" : Path.GetFullPath(musicFolder),
            EventContext = "Synteettinen testikilpailu OpenSkiTimen radiolle Hauskarinteessä. Laskijat ja seurat ovat keksittyjä. "
                + "Aurinko paistaa, lunta on hyvin ja yleisöä on paikalla mukavasti."
        };
        configuration.Validate();

        var race = new Race(field, DateTimeOffset.UtcNow);
        await using var publisher = new PublisherProcess();
        await using var radio = new RadioProcess();
        publisher.Offer(race.Snapshot());
        await publisher.StartAsync(workerDll, new PublisherOptions(PublisherKind.Cloud, endpoint, PublisherKey: publisherKey), ct: ct);
        using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            while (publisher.Health.State != PublisherState.Running || publisher.ResumeSession is null) { await Task.Delay(500, ready.Token); }
        }
        var session = publisher.ResumeSession!;
        Console.WriteLine($"Public race view: {publisher.Health.PublicUrl}");
        radio.Offer(race.Snapshot());
        await radio.StartAsync(radioWorkerDll, new RadioWorkerOptions(configuration, 0m, RadioKey(llmProvider), RadioKey(ttsProvider)),
            [new(PublisherKind.Cloud, endpoint, session.SessionId, session.PublisherToken)], ct: ct);
        var status = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var h = radio.Health;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{DateTime.Now:HH:mm:ss} publisher {publisher.Health.State} · radio {h.State} {h.Phase} queue {h.QueueDepth} spent {h.SpentUsd:0.000} USD · last clip {h.LastClipAt?.ToLocalTime():HH:mm:ss} {h.LastClipHost} {h.Error}"));
                await Task.Delay(TimeSpan.FromSeconds(20), ct);
            }
        }, ct);
        // Racers already listed get a minute of "before start" talk while viewers join.
        await Task.Delay(TimeSpan.FromSeconds(60), ct);
        foreach (var run in new[] { 1, 2 })
        {
            if (run == 2)
            {
                await Task.Delay(TimeSpan.FromMinutes(3), ct);
                race.StartRun2();
                Publish();
            }
            var order = race.Order(run);
            var start = DateTimeOffset.UtcNow;
            var events = new List<(DateTimeOffset At, int Bib, LiveStatus Status, long? Time)>();
            for (var i = 0; i < order.Length; i++)
            {
                var bib = order[i];
                var started = start + TimeSpan.FromSeconds(i * 24 + random.Next(0, 3));
                events.Add((started, bib, LiveStatus.OnCourse, null));
                var hundredths = 4700L + skill[bib] + random.Next(-120, 120);
                if (random.NextDouble() < 0.25) { events.Add((started + TimeSpan.FromSeconds(random.Next(12, 40)), bib, LiveStatus.DNF, null)); }
                else { events.Add((started + TimeSpan.FromMilliseconds(hundredths * 10), bib, LiveStatus.Finished, hundredths)); }
            }
            foreach (var e in events.OrderBy(x => x.At))
            {
                var wait = e.At - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero) { await Task.Delay(wait, ct); }
                race.Set(run, e.Bib, e.Status, e.Time);
                Publish();
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} run {run} bib {e.Bib} {e.Status}");
            }
        }
        Console.WriteLine("Race complete. The radio and the race view stay on for five minutes (Ctrl+C stops).");
        try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { }
        return 0;

        void Publish() { var snapshot = race.Snapshot(); publisher.Offer(snapshot); radio.Offer(snapshot); }
    }

    private sealed class Race(LiveCompetitor[] field, DateTimeOffset start)
    {
        private readonly DateTimeOffset created = start;
        private readonly Dictionary<int, LiveResult>[] _runs = [field.ToDictionary(x => x.Bib, x => new LiveResult(x.Bib, LiveStatus.Ready, null, null, null, start)), []];
        private int[] _order2 = [];
        private DateTimeOffset _list2 = start;
        private long _version = 1;
        public int Current { get; private set; } = 1;
        public int[] Order(int run) => run == 1 ? [.. field.Select(x => x.Bib)] : _order2;
        /// <summary>Run 2: the finishers of run 1, slowest first, so the leader starts last.</summary>
        public void StartRun2()
        {
            _order2 = [.. _runs[0].Values.Where(x => x.Status == LiveStatus.Finished).OrderByDescending(x => x.Hundredths).Select(x => x.Bib)];
            _list2 = DateTimeOffset.UtcNow;
            _runs[1] = _order2.ToDictionary(x => x, x => new LiveResult(x, LiveStatus.Ready, null, null, null, _list2));
            Current = 2;
        }
        public void Set(int run, int bib, LiveStatus status, long? time)
        {
            var now = DateTimeOffset.UtcNow;
            var old = _runs[run - 1][bib];
            _runs[run - 1][bib] = old with { Status = status, Hundredths = time, At = now, StartedAt = old.StartedAt ?? now };
        }
        public LiveSnapshot Snapshot()
        {
            var now = DateTimeOffset.UtcNow;
            var runs = new List<LiveRun> { new(1, created, [.. field.Select(x => x.Bib)], Ranked(_runs[0].Values, null)) };
            if (Current == 2) { runs.Add(new(2, _list2, _order2, Ranked(_runs[1].Values, _runs[0]))); }
            return new(++_version, new("OpenSkiTime radiotesti", "Testitunturi", "GS", DateOnly.FromDateTime(now.UtcDateTime), false, "", "M", "Test", 0, "Hauskarinne", 2),
                field, Current, [.. runs], now);
        }
        private static LiveResult[] Ranked(IEnumerable<LiveResult> results, Dictionary<int, LiveResult>? first)
        {
            var list = results.ToArray();
            var finished = list.Where(x => x.Status == LiveStatus.Finished).ToArray();
            var best = finished.Select(x => x.Hundredths).Min();
            long? Total(LiveResult r) => first is not null && r.Status == LiveStatus.Finished && first[r.Bib].Hundredths is { } h ? h + r.Hundredths : null;
            var totals = finished.Select(Total).Where(x => x is not null).ToArray();
            return [.. list.Select(r =>
            {
                var ranked = r with
                {
                    Rank = r.Status == LiveStatus.Finished ? finished.Count(x => x.Hundredths < r.Hundredths) + 1 : null,
                    Difference = r.Status == LiveStatus.Finished ? r.Hundredths - best : null
                };
                return Total(r) is { } total
                    ? ranked with { TotalHundredths = total, TotalRank = totals.Count(x => x < total) + 1, TotalDifference = total - totals.Min() }
                    : ranked;
            })];
        }
    }
}
