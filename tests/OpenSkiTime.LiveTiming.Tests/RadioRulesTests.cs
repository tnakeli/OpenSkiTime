using OpenSkiTime.LiveTiming.Radio;
using OpenSkiTime.LiveTiming.Radio.Providers;
using Xunit;

namespace OpenSkiTime.LiveTiming.Tests;

// Deterministic radio rules: event detection, batching, phases, stale clips, producer gate and memories.
public sealed class RadioRulesTests
{
    [Fact]
    public void DetectorAnnouncesChangesNotHistory()
    {
        var start = RadioRace.Create();
        var now = RadioRace.Start.AddMinutes(10);
        Assert.Empty(RadioEventDetector.Detect(null, RadioRace.Set(start, 1, LiveStatus.Finished, 4800), now));
        var onCourse = RadioRace.Set(start, 1, LiveStatus.OnCourse);
        var started = Assert.Single(RadioEventDetector.Detect(start, onCourse, now));
        Assert.Equal(RadioEventKind.RunStarted, started.Kind);
        // "Radalla on nyt ..." is stale once that racer has left the course.
        Assert.Equal(new RadioCondition(RadioConditionKind.Status, 1, 1, LiveStatus.OnCourse), Assert.Single(started.Conditions));
        // Start and finish coalesced into one snapshot: the run start is announced without naming anyone on course.
        var coalesced = Assert.Single(RadioEventDetector.Detect(start, RadioRace.Set(onCourse, 1, LiveStatus.DNF), now), x => x.Kind == RadioEventKind.RunStarted);
        Assert.Empty(coalesced.Bibs); Assert.Empty(coalesced.Conditions);
        var first = RadioRace.Set(onCourse, 1, LiveStatus.Finished, 4800);
        var podium = Assert.Single(RadioEventDetector.Detect(onCourse, first, now));
        Assert.Equal((RadioEventKind.Podium, 2), (podium.Kind, podium.Priority));
        // The announced place is a condition: the first finisher's "leads" is stale once someone is faster.
        Assert.Contains(new RadioCondition(RadioConditionKind.Rank, 1, 1, Value: 1), podium.Conditions);
        var leader = RadioRace.Set(first, 2, LiveStatus.Finished, 4700);
        Assert.Equal(RadioEventKind.NewLeader, Assert.Single(RadioEventDetector.Detect(first, leader, now)).Kind);
        // Matching the leader's time shares the lead; nobody is displaced.
        var tied = RadioRace.Set(leader, 5, LiveStatus.Finished, 4700);
        Assert.Equal(RadioEventKind.Podium, Assert.Single(RadioEventDetector.Detect(leader, tied, now)).Kind);
        var fourth = RadioRace.Set(RadioRace.Set(leader, 3, LiveStatus.Finished, 4900), 4, LiveStatus.Finished, 5000);
        var events = RadioEventDetector.Detect(leader, fourth, now);
        Assert.Equal([RadioEventKind.Podium, RadioEventKind.Finish], events.Select(x => x.Kind));
        Assert.Equal(3, events[1].Priority);
        var corrected = RadioRace.Set(fourth, 4, LiveStatus.Finished, 4990);
        Assert.Equal(RadioEventKind.Correction, Assert.Single(RadioEventDetector.Detect(fourth, corrected, now)).Kind);
        var dns = RadioRace.Set(corrected, 5, LiveStatus.DNS);
        var last = RadioEventDetector.Detect(corrected, dns, now);
        Assert.Equal([RadioEventKind.DNS, RadioEventKind.RunComplete], last.Select(x => x.Kind));
        Assert.Equal([2, 1, 3], last[1].Bibs);
        Assert.Equal([RacePhase.BetweenRuns, RacePhase.AfterRace], last[1].Phases);
    }

    [Fact]
    public void BatcherFlushesByHighestPriorityAndSize()
    {
        var batcher = new EventBatcher();
        var t0 = RadioRace.Start;
        RadioEvent Event(int priority, DateTimeOffset at) => new(RadioEventKind.Finish, priority, 1, [1], at, TimeSpan.FromMinutes(5), [], [], 1);
        batcher.Add(Event(3, t0), t0);
        Assert.Equal(t0.AddSeconds(12), batcher.Deadline);
        Assert.Null(batcher.TakeDue(t0.AddSeconds(6)));
        // A higher priority shortens the delay from its own arrival.
        batcher.Add(Event(2, t0.AddSeconds(2)), t0.AddSeconds(2));
        Assert.Equal(t0.AddSeconds(7), batcher.Deadline);
        var batch = batcher.TakeDue(t0.AddSeconds(7))!;
        Assert.Equal([2, 3], batch.Select(x => x.Priority));
        Assert.Equal(0, batcher.Count);
        for (var i = 0; i < EventBatcher.MaxBatch; i++) { batcher.Add(Event(5, t0), t0); }
        Assert.Equal(EventBatcher.MaxBatch, batcher.TakeDue(t0)!.Count);
        // Events that went stale while waiting are dropped instead of narrated late.
        batcher.Add(Event(4, t0) with { Conditions = [new(RadioConditionKind.Status, 1, 1, LiveStatus.OnCourse)] }, t0);
        batcher.Prune(RadioRace.Create(), t0);
        Assert.Equal(0, batcher.Count);
        // News for another phase is dropped, so one batch never mixes disjoint phase restrictions.
        batcher.Add(Event(2, t0) with { Phases = [RacePhase.AfterRace] }, t0);
        batcher.Prune(RadioRace.Create(), t0);
        Assert.Equal(0, batcher.Count);
        // Run 1 news is dropped once the race has moved on to Run 2.
        batcher.Add(Event(2, t0), t0);
        var second = RadioRace.Create() with { CurrentRun = 2, Runs = [.. RadioRace.Create().Runs, RadioRace.Create().Runs[0] with { Number = 2 }] };
        batcher.Prune(second, t0);
        Assert.Equal(0, batcher.Count);
    }

    [Fact]
    public void PhaseIsDerivedFromTheCurrentRunAndThePublisherClock()
    {
        var start = RadioRace.Create();
        var now = RadioRace.Start;
        Assert.Equal(RacePhase.BeforeStart, RacePhases.Derive(start, now));
        var running = RadioRace.Set(start, 1, LiveStatus.OnCourse, at: now);
        Assert.Equal(RacePhase.RunInProgress, RacePhases.Derive(running, now.AddHours(1)));
        var waiting = RadioRace.Set(running, 1, LiveStatus.Finished, 4800, now.AddMinutes(1));
        Assert.Equal(RacePhase.RunInProgress, RacePhases.Derive(waiting, now.AddMinutes(4)));
        Assert.Equal(RacePhase.Interrupted, RacePhases.Derive(waiting, now.AddMinutes(5)));
        var done = Enumerable.Range(2, 4).Aggregate(waiting, (s, bib) => RadioRace.Set(s, bib, LiveStatus.Finished, 4800 + bib));
        Assert.Equal(RacePhase.BetweenRuns, RacePhases.Derive(done, now));
        Assert.Equal(RacePhase.AfterRace, RacePhases.Derive(done with { Competition = done.Competition with { Discipline = "SG" } }, now));
        var second = done with { CurrentRun = 2, Runs = [.. done.Runs, done.Runs[0] with { Number = 2, Results = start.Runs[0].Results }] };
        Assert.Equal(RacePhase.BetweenRuns, RacePhases.Derive(second, now));
    }

    [Fact]
    public void ClipsAreStaleAfterExpiryPhaseChangeOrAContradictingSnapshot()
    {
        var state = RadioRace.Set(RadioRace.Create(), 1, LiveStatus.Finished, 4800);
        var now = RadioRace.Start.AddMinutes(1);
        RadioCondition[] leader = [new(RadioConditionKind.Rank, 1, 1, Value: 1), new(RadioConditionKind.CurrentRun, 1)];
        Assert.True(RadioPlayback.Playable(state, now, now.AddMinutes(1), [], leader));
        Assert.False(RadioPlayback.Playable(state, now.AddMinutes(1), now.AddMinutes(1), [], leader));
        Assert.False(RadioPlayback.Playable(state, now, now.AddMinutes(1), [RacePhase.AfterRace], leader));
        var beaten = RadioRace.Set(state, 2, LiveStatus.Finished, 4700);
        Assert.False(RadioPlayback.Playable(beaten, now, now.AddMinutes(1), [], leader));
        var onCourse = RadioRace.Set(state, 2, LiveStatus.OnCourse);
        RadioCondition[] start = [new(RadioConditionKind.Status, 1, 2, LiveStatus.OnCourse)];
        Assert.True(RadioPlayback.Playable(onCourse, now, now.AddMinutes(1), [], start));
        Assert.False(RadioPlayback.Playable(RadioRace.Set(onCourse, 2, LiveStatus.DNF), now, now.AddMinutes(1), [], start));
    }

    [Fact]
    public void PlayControlNeedsEnabledHealthyFundedUnpausedRadio()
    {
        var state = RadioRace.Create();
        var now = RadioRace.Start;
        Assert.True(RadioPlayback.Available(true, false, now.AddSeconds(-29), state, now));
        Assert.False(RadioPlayback.Available(false, false, now, state, now));
        Assert.False(RadioPlayback.Available(true, true, now, state, now));
        Assert.False(RadioPlayback.Available(true, false, now.AddSeconds(-31), state, now));
        Assert.False(RadioPlayback.Available(true, false, null, state, now));
        Assert.False(RadioPlayback.Available(true, false, now, state with { Paused = true }, now));
        Assert.False(RadioPlayback.Available(true, false, now, null, now));
    }

    [Fact]
    public void UploadValidationRejectsWrongAudioOversizeAndLongLifetimes()
    {
        var wav = SimulationTts.Tone(500, 440);
        var valid = new RadioClipUpload(Guid.NewGuid(), Guid.NewGuid(), 0, 1, "Aino", 3, true, 120, [], [], "audio/wav", 500, Convert.ToBase64String(wav));
        RadioPlayback.Validate(valid, out var audio);
        Assert.Equal(wav, audio);
        foreach (var invalid in new[]
        {
            valid with { ContentType = "audio/mpeg" }, valid with { ContentType = "text/html" }, valid with { Audio = "not base64!" },
            valid with { LifetimeSeconds = 601 }, valid with { LifetimeSeconds = 0 }, valid with { Host = "Aino1" }, valid with { Line = 1 },
            valid with { Priority = 0 }, valid with { Audio = Convert.ToBase64String(new byte[RadioPlayback.MaxClipBytes + 1]) },
            valid with { Conditions = [.. Enumerable.Repeat(new RadioCondition(RadioConditionKind.CurrentRun, 1), RadioPlayback.MaxConditions + 1)] }
        })
        { Assert.Throws<LiveValidationException>(() => RadioPlayback.Validate(invalid, out _)); }
        RadioPlayback.Validate(valid with { ContentType = "audio/mpeg", Audio = Convert.ToBase64String([0xFF, 0xF3, 0x44, 0xC4, 0x00]) }, out _);
    }

    [Fact]
    public void ProducerGateAllowsOnlyPhaseSourcesAndParticipants()
    {
        var state = RadioRace.Set(RadioRace.Create(), 1, LiveStatus.OnCourse);
        Assert.Null(ProducerGate.Problem(new(ProducerSources.NextStarter, 2, "Esittele"), state, RacePhase.RunInProgress, false));
        Assert.Equal("racer is not the next to start", ProducerGate.Problem(new(ProducerSources.NextStarter, 1, ""), state, RacePhase.RunInProgress, false));
        Assert.Equal("racer is not the next to start", ProducerGate.Problem(new(ProducerSources.NextStarter, 3, ""), state, RacePhase.RunInProgress, false));
        Assert.Equal("racer is not the next to start", ProducerGate.Problem(new(ProducerSources.NextStarter, 42, ""), state, RacePhase.RunInProgress, false));
        Assert.Null(ProducerGate.Problem(new(ProducerSources.OnCourse, 1, ""), state, RacePhase.RunInProgress, false));
        Assert.Equal("source not allowed in this phase", ProducerGate.Problem(new(ProducerSources.OnCourse, 1, ""), state, RacePhase.BetweenRuns, false));
        Assert.Equal("no standings yet", ProducerGate.Problem(new(ProducerSources.Standings, null, ""), state, RacePhase.RunInProgress, false));
        Assert.Equal("no operator context", ProducerGate.Problem(new(ProducerSources.EventContext, null, ""), state, RacePhase.BeforeStart, false));
        Assert.Equal("unknown source", ProducerGate.Problem(new("weather", null, ""), state, RacePhase.BeforeStart, true));
        var next = ProducerGate.Deterministic(state, RacePhase.RunInProgress, 0);
        Assert.NotNull(next); Assert.Null(ProducerGate.Problem(next, state, RacePhase.RunInProgress, false));
        // A run summary speaks every count through a slot, also "yksi", so each count guards the clip.
        var summary = FinnishTemplates.Lines([ProducerGate.Event(new(ProducerSources.RunSummary, null, ""), RadioRace.Set(state, 1, LiveStatus.Finished, 4800), RadioRace.Start)],
            FactSheet.Build(RadioRace.Set(state, 1, LiveStatus.Finished, 4800), []), 0);
        var guarded = RadioSlots.Conditions(summary[0].Text, FactSheet.Build(RadioRace.Set(state, 1, LiveStatus.Finished, 4800), [])).ToArray();
        Assert.Contains(new RadioCondition(RadioConditionKind.StatusCount, 1, Status: LiveStatus.Finished, Value: 1), guarded);
        Assert.Contains(new RadioCondition(RadioConditionKind.StatusCount, 1, Status: LiveStatus.Ready, Value: 4), guarded);
        // Between runs the standings are the previous run's, spoken with its ranks.
        var done = Enumerable.Range(1, 5).Aggregate(RadioRace.Create(), (s, bib) => RadioRace.Set(s, bib, LiveStatus.Finished, 4800 - bib));
        var between = done with { CurrentRun = 2, Runs = [.. done.Runs, RadioRace.Create().Runs[0] with { Number = 2 }] };
        var standings = ProducerGate.Event(new(ProducerSources.Standings, null, ""), between, RadioRace.Start);
        var betweenFacts = FactSheet.Build(between, standings.Bibs);
        Assert.Equal(new RadioCondition(RadioConditionKind.StatusCount, 2, Status: LiveStatus.Finished, Value: 0), Assert.Single(standings.Conditions));
        Assert.False(RadioPlayback.Holds(RadioRace.Set(RadioRace.Set(between, 1, LiveStatus.OnCourse), 1, LiveStatus.Finished, 4700), standings.Conditions[0]));
        var line = Assert.Single(FinnishTemplates.Lines([standings], betweenFacts, 0));
        Assert.Null(RadioSlots.Problem(line.Text, betweenFacts, new LineRules(between, ["Aino", "Kalle"], "")));
        Assert.Equal("Epävirallinen tilanne: ensimmäisenä on Onni Viistola, toisena Ilona Kurvinen, kolmantena Saga Porttila.",
            RadioSlots.Render(line.Text, betweenFacts, FinnishVerbalizer.Lexicon("")));
        var e = ProducerGate.Event(new(ProducerSources.NextStarter, 2, ""), state, RadioRace.Start);
        Assert.Equal(new RadioCondition(RadioConditionKind.NextStarter, 1, 2), Assert.Single(e.Conditions));
        Assert.True(RadioPlayback.Holds(state, e.Conditions[0]));
        // Bib 3 moved ahead of bib 2 in the start queue: bib 2 is still waiting but no longer next.
        var reordered = state with { Runs = [state.Runs[0] with { StartOrder = [1, 3, 2, 4, 5] }] };
        Assert.False(RadioPlayback.Holds(reordered, e.Conditions[0]));
    }

    [Fact]
    public void MemoriesExpireAndEvictByRelevance()
    {
        var now = RadioRace.Start;
        var topics = new TopicMemory(TimeSpan.FromHours(2));
        topics.Add("uusi johtaja ensimmäisellä laskulla", now);
        topics.Add("yksi kaksi kolme neljä viisi kuusi seitsemän kahdeksan yhdeksän kymmenen yksitoista", now.AddMinutes(30));
        Assert.Equal(2, topics.Recent(now.AddMinutes(90)).Count);
        Assert.Equal(10, topics.Recent(now.AddMinutes(90))[1].Split(' ').Length);
        Assert.Single(topics.Recent(now.AddHours(2)));
        var styles = new StyleMemory(2);
        styles.AddRange(["a", "b", "c"]);
        Assert.Equal(["b", "c"], styles.Recent);
        var diary = new EventDiary(2);
        RadioEvent Event(int relevance, int minute) => new(RadioEventKind.Finish, 3, 1, [1], now.AddMinutes(minute), TimeSpan.Zero, [], [], relevance);
        diary.Add(Event(5, 1)); diary.Add(Event(3, 2)); diary.Add(Event(2, 3)); diary.Add(Event(8, 4)); diary.Add(Event(1, 5));
        Assert.Equal([5, 8], diary.Entries.Select(x => x.Relevance));
    }

    [Fact]
    public void BudgetModesAndHardStop()
    {
        var budget = new RadioBudget(1m, 0m);
        Assert.Equal(RadioMode.Full, budget.Mode(0.01m, 0.003m));
        budget.Charge(0.90m); budget.Settle(0.90m, 0.85m);
        Assert.Equal(RadioMode.Lean, budget.Mode(0.01m, 0.003m));
        budget.Charge(0.14m);
        Assert.Equal(RadioMode.TemplatesOnly, budget.Mode(0.01m, 0.003m));
        budget.Charge(0.008m);
        Assert.Equal(RadioMode.Exhausted, budget.Mode(0.01m, 0.003m));
        Assert.False(budget.CanAfford(0.003m));
        Assert.Equal(RadioMode.Exhausted, new RadioBudget(0m, 0m).Mode(0m, 0m));
        Assert.True(new RadioBudget(1m, 0m).ProducerInterval < new RadioBudget(1m, 0.9m).ProducerInterval);
    }

    [Fact]
    public void AudioDurationsComeFromTheAudio()
    {
        Assert.Equal(1500, AudioDuration.Milliseconds(SimulationTts.Tone(1500, 440), "audio/wav"));
        // Two MPEG-2 layer III frames, 24 kHz, 48 kbit/s: 576 samples each.
        var frame = new byte[144]; frame[0] = 0xFF; frame[1] = 0xF3; frame[2] = 0x64;
        Assert.Equal(48, AudioDuration.Milliseconds([.. frame, .. frame], "audio/mpeg"));
    }
}
