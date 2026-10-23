using System.Text.Json;
using OpenSkiTime.LiveTiming.Radio;
using OpenSkiTime.LiveTiming.Radio.Providers;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// The radio pipeline through its real boundary: fake language model, speech and server sink, and an injected clock.
public sealed class RadioEngineTests
{
    private static readonly string[] Patterns = ["vertaus junaan"];
    private static readonly RadioConfiguration Config = new() { CloudEnabled = true, BudgetUsd = 5m, LlmProvider = RadioProviders.OpenAi, LlmModel = "gpt-4.1-mini", TtsProvider = RadioProviders.Azure };

    private sealed class Rig
    {
        public ManualTime Time { get; } = new(RadioRace.Start);
        public FakeSink Sink { get; init; } = new();
        public required FakeLlm Llm { get; init; }
        public FakeTts Tts { get; init; } = new();
        public RadioConfiguration Configuration { get; init; } = Config;
        public RadioBudget Budget { get; init; } = new(5m, 0m);
        private RadioEngine? _engine;
        public RadioEngine Engine => _engine ??= Create();
        private RadioEngine Create() { var e = new RadioEngine(Configuration, Llm, Tts, Budget, Time); e.SetSinks([Sink]); return e; }
        public LiveSnapshot State { get; set; } = RadioRace.Create();
        /// <summary>Publishes the first finish of the race and advances time until its batch is due.</summary>
        public async Task FirstFinishAsync()
        {
            // The first snapshot is history: the racer already on course is not announced.
            State = RadioRace.Set(RadioRace.Create(), 1, LiveStatus.OnCourse, at: Time.GetUtcNow());
            Engine.Offer(State); await Engine.StepAsync(CancellationToken.None);
            State = RadioRace.Set(State, 1, LiveStatus.Finished, 4701, Time.GetUtcNow());
            Engine.Offer(State); await Engine.StepAsync(CancellationToken.None);
            Time.Advance(TimeSpan.FromSeconds(6));
        }
    }
    private static string Script(params (string Host, string Text, bool Cut)[] lines) =>
        JsonSerializer.Serialize(new { topic = "maaliintulo", lines = lines.Select(x => new { host = x.Host, text = x.Text, interruptible = x.Cut }) });
    private static string Echo(LlmRequest request, Func<string, string>? change = null)
    {
        using var json = JsonDocument.Parse(request.User);
        return JsonSerializer.Serialize(new
        {
            lines = json.RootElement.GetProperty("lines").EnumerateArray().Select(x => new { host = x.GetProperty("host").GetString(), text = (change ?? (t => t))(x.GetProperty("text").GetString()!) }),
            patterns = Patterns
        });
    }

    [Fact]
    public async Task ModelScriptIsCuratedRenderedSpokenAndUploadedWithItsFacts()
    {
        var rig = new Rig
        {
            Llm = new((r, _) => Task.FromResult(FakeLlm.IsCuration(r) ? Echo(r, t => t.Replace("Upea", "Hieno", StringComparison.Ordinal))
                : Script(("Aino", "Upea lasku! Maalissa {racer:1}, aika {time:1}.", false), ("Kalle", "Hän johtaa {ord:run:ade} laskulla.", true))))
        };
        await rig.FirstFinishAsync();
        await rig.Engine.StepAsync(CancellationToken.None);
        var clips = rig.Sink.Clips.ToArray();
        Assert.Equal(2, clips.Length);
        Assert.Equal("Hieno lasku! Maalissa Aada Lumivaara, aika neljäkymmentäseitsemän pilkku nolla yksi sekuntia.", clips[0].Transcript);
        Assert.Equal("Hän johtaa ensimmäisellä laskulla.", clips[1].Transcript);
        Assert.Equal(["Aino", "Kalle"], clips.Select(x => x.Host));
        Assert.Equal(clips[0].BatchId, clips[1].BatchId);
        Assert.Equal(2, clips[0].Priority);
        Assert.Contains(new RadioCondition(RadioConditionKind.Time, 1, 1, Value: 4701), clips[0].Conditions);
        Assert.Contains(new RadioCondition(RadioConditionKind.CurrentRun, 1), clips[0].Conditions);
        Assert.Contains(new RadioCondition(RadioConditionKind.Status, 1, 1, LiveStatus.Finished), clips[0].Conditions);
        Assert.True(clips[1].Interruptible);
        // The model never saw a racer name; the speech provider received only rendered Finnish without slots or digits.
        Assert.All(rig.Llm.Requests, r => Assert.DoesNotContain("Lumivaara", r.User, StringComparison.OrdinalIgnoreCase));
        Assert.All(rig.Tts.Texts, t => Assert.False(t.Any(char.IsDigit) || t.Contains('{', StringComparison.Ordinal)));
        Assert.True(rig.Budget.Spent > 0m);
        Assert.Equal(RadioState.Running, rig.Engine.Health.State);
    }

    [Theory]
    [InlineData("digits")]
    [InlineData("name")]
    [InlineData("json")]
    [InlineData("throws")]
    public async Task InvalidOrFailedModelOutputFallsBackToTemplates(string failure)
    {
        var rig = new Rig
        {
            Llm = new((r, _) => failure switch
            {
                "digits" => Task.FromResult(Script(("Aino", "Aika 47,01!", true))),
                "name" => Task.FromResult(Script(("Aino", "Lumivaara johtaa!", true))),
                "json" => Task.FromResult("not json"),
                _ => throw new RadioProviderException("OpenAI returned HTTP 500.")
            })
        };
        await rig.FirstFinishAsync();
        await rig.Engine.StepAsync(CancellationToken.None);
        var spoken = rig.Sink.Clips.Select(x => x.Transcript).ToArray();
        Assert.Contains("Maalissa Aada Lumivaara, aika neljäkymmentäseitsemän pilkku nolla yksi sekuntia.", spoken);
        Assert.Contains("Hän on nyt kärjessä.", spoken);
        Assert.DoesNotContain(spoken, x => x.Any(char.IsDigit));
        Assert.Contains("Templates used", rig.Engine.Health.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HangingModelTimesOutOnTheInjectedClockAndTemplatesSpeak()
    {
        var called = new TaskCompletionSource();
        var rig = new Rig { Llm = new(async (_, ct) => { called.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return ""; }) };
        await rig.FirstFinishAsync();
        var step = rig.Engine.StepAsync(CancellationToken.None);
        await called.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Time.Advance(RadioEngine.DialogueTimeout);
        await step.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(rig.Sink.Clips, x => x.Transcript == "Hän on nyt kärjessä.");
        Assert.Equal("A radio provider did not answer in time. Templates used.", rig.Engine.Health.Error);
    }

    [Fact]
    public async Task CurationThatChangesSlotsOrHostsKeepsTheOriginalScript()
    {
        foreach (var change in new Func<string, string>[] { t => t.Replace("{time:1}", "{time:2}", StringComparison.Ordinal), t => t + " {rank:1:ess}", t => t.Replace("{racer:1}", "Aada", StringComparison.Ordinal) })
        {
            var rig = new Rig { Llm = new((r, _) => Task.FromResult(FakeLlm.IsCuration(r) ? Echo(r, change) : Script(("Aino", "Maalissa {racer:1}, aika {time:1}.", true)))) };
            await rig.FirstFinishAsync();
            await rig.Engine.StepAsync(CancellationToken.None);
            Assert.Contains(rig.Sink.Clips, x => x.Transcript == "Maalissa Aada Lumivaara, aika neljäkymmentäseitsemän pilkku nolla yksi sekuntia.");
        }
        var hosts = new Rig { Llm = new((r, _) => Task.FromResult(FakeLlm.IsCuration(r) ? Echo(r).Replace("Aino", "Kalle", StringComparison.Ordinal) : Script(("Aino", "Maalissa {racer:1}.", true)))) };
        await hosts.FirstFinishAsync();
        await hosts.Engine.StepAsync(CancellationToken.None);
        Assert.Contains(hosts.Sink.Clips, x => x.Transcript == "Maalissa Aada Lumivaara." && x.Host == "Aino");
    }

    [Fact]
    public async Task FailingOrHangingSpeechSkipsTheBatchAndTheEngineCarriesOn()
    {
        var calls = 0;
        var rig = new Rig
        {
            Llm = new((_, _) => throw new RadioProviderException("down")),
            Tts = new(async (_, ct) => { if (Interlocked.Increment(ref calls) <= 2) { await Task.Delay(Timeout.Infinite, ct); } })
        };
        rig.Engine.Offer(rig.State); await rig.Engine.StepAsync(CancellationToken.None);
        rig.State = RadioRace.Set(RadioRace.Set(rig.State, 1, LiveStatus.OnCourse, at: rig.Time.GetUtcNow()), 1, LiveStatus.Finished, 4701, rig.Time.GetUtcNow());
        rig.Engine.Offer(rig.State); await rig.Engine.StepAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromSeconds(6));
        var hanging = rig.Engine.StepAsync(CancellationToken.None);
        await Until(() => Volatile.Read(ref calls) >= 1);
        rig.Time.Advance(RadioEngine.SpeechTimeout);
        await hanging.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(rig.Sink.Clips);
        Assert.Equal("A radio provider did not answer in time.", rig.Engine.Health.Error);
        rig.State = RadioRace.Set(RadioRace.Set(rig.State, 2, LiveStatus.OnCourse, at: rig.Time.GetUtcNow()), 2, LiveStatus.DNF, at: rig.Time.GetUtcNow());
        rig.Engine.Offer(rig.State); await rig.Engine.StepAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromSeconds(13));
        var second = rig.Engine.StepAsync(CancellationToken.None);
        await Until(() => Volatile.Read(ref calls) >= 2);
        rig.Time.Advance(RadioEngine.SpeechTimeout);
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        rig.State = RadioRace.Set(RadioRace.Set(rig.State, 3, LiveStatus.OnCourse, at: rig.Time.GetUtcNow()), 3, LiveStatus.DNF, at: rig.Time.GetUtcNow());
        rig.Engine.Offer(rig.State); await rig.Engine.StepAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromSeconds(13));
        await rig.Engine.StepAsync(CancellationToken.None);
        Assert.Equal("Saga Porttila keskeytti.", Assert.Single(rig.Sink.Clips).Transcript);
    }

    [Fact]
    public async Task ClipIsDroppedWhenTheRaceContradictsItBeforeUpload()
    {
        Rig? rig = null;
        rig = new Rig
        {
            Llm = new((r, _) => Task.FromResult(FakeLlm.IsCuration(r) ? Echo(r) : Script(("Aino", "{racer:1} on {rank:1:ess} ajalla {time:1}.", true)))),
            // While the line is synthesized, a faster racer finishes: "first" is no longer true.
            Tts = new((text, _) =>
            {
                if (text.Contains("ensimmäisenä", StringComparison.Ordinal))
                { rig!.State = RadioRace.Set(RadioRace.Set(rig.State, 2, LiveStatus.OnCourse), 2, LiveStatus.Finished, 4600); rig.Engine.Offer(rig.State); }
                return Task.CompletedTask;
            })
        };
        await rig.FirstFinishAsync();
        await rig.Engine.StepAsync(CancellationToken.None);
        Assert.Single(rig.Tts.Texts);
        Assert.Empty(rig.Sink.Clips);
    }

    [Fact]
    public async Task BudgetStopsGenerationHardAndHeartbeatsSayExhausted()
    {
        var templatesOnly = new Rig { Llm = new((_, _) => Task.FromResult(Script(("Aino", "Maalissa {racer:1}.", true)))), Budget = new(0.005m, 0m) };
        await templatesOnly.FirstFinishAsync();
        await templatesOnly.Engine.StepAsync(CancellationToken.None);
        Assert.Empty(templatesOnly.Llm.Requests); // Too little left for a model call: deterministic phrases only.
        Assert.Contains(templatesOnly.Sink.Clips, x => x.Transcript == "Hän on nyt kärjessä.");
        var exhausted = new Rig { Llm = new((_, _) => Task.FromResult("")), Budget = new(0.001m, 0m) };
        await exhausted.FirstFinishAsync();
        await exhausted.Engine.StepAsync(CancellationToken.None);
        Assert.Empty(exhausted.Sink.Clips);
        Assert.Empty(exhausted.Tts.Texts);
        Assert.Equal(RadioState.BudgetExhausted, exhausted.Engine.Health.State);
        await exhausted.Engine.HeartbeatAsync(CancellationToken.None);
        Assert.Equal(new RadioStatusUpdate(true, true), exhausted.Sink.Heartbeats.Last());
        // Spend can pass the limit only by the error of the last estimated call.
        Assert.True(templatesOnly.Budget.Spent <= templatesOnly.Budget.Limit + RadioPrices.Cost(RadioPrices.ForTts(RadioProviders.Azure), 400, 0));
    }

    [Fact]
    public async Task ProducerFillsIdleTimeOnlyWithAllowedSourcesAndRetriesOnce()
    {
        var producerCalls = 0;
        var rig = new Rig
        {
            Llm = new((r, _) =>
            {
                if (FakeLlm.IsProducer(r)) { Interlocked.Increment(ref producerCalls); return Task.FromResult("""{"source":"standings","bib":null,"instruction":"Kerro tilanne."}"""); }
                return Task.FromResult(Script()); // An empty script is rejected; templates speak.
            })
        };
        rig.Engine.Offer(rig.State); await rig.Engine.StepAsync(CancellationToken.None);
        rig.Time.Advance(TimeSpan.FromSeconds(29)); await rig.Engine.StepAsync(CancellationToken.None);
        Assert.Equal(0, producerCalls);
        rig.Time.Advance(TimeSpan.FromSeconds(62)); await rig.Engine.StepAsync(CancellationToken.None);
        // Before the start there are no standings: the gate rejects the choice twice, then the code picks a valid source.
        Assert.Equal(2, producerCalls);
        Assert.Equal("Seuraavana lähtövuorossa on Aada Lumivaara.", Assert.Single(rig.Sink.Clips).Transcript);
        Assert.Equal(5, rig.Sink.Clips.Single().Priority);
        Assert.Contains(new RadioCondition(RadioConditionKind.Status, 1, 1, LiveStatus.Ready), rig.Sink.Clips.Single().Conditions);
    }

    [Fact]
    public async Task UploadFailureIsReportedWithoutStoppingTheRadio()
    {
        var rig = new Rig
        {
            Llm = new((_, _) => throw new RadioProviderException("down")),
            Sink = new FakeSink { OnUpload = _ => throw new RadioProviderException("Cloud live timing server could not be reached for radio.") }
        };
        await rig.FirstFinishAsync();
        await rig.Engine.StepAsync(CancellationToken.None);
        Assert.Equal("Cloud live timing server could not be reached for radio.", rig.Engine.Health.Error);
        Assert.Equal(RadioState.Running, rig.Engine.Health.State);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, timeout.Token); }
    }
}
