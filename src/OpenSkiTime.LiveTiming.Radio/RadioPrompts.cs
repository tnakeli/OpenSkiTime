using System.Globalization;
using System.Text.Json;

namespace OpenSkiTime.LiveTiming.Radio;

/// <summary>
/// Prompts for dialogue, curation and the producer. Structure and several rules (adaptive verbosity, factual discipline,
/// interruptible lines, style-only curation, single-source producer) are adapted from mikradio with its author's
/// permission; see THIRD-PARTY-NOTICES.md. Numbers and names are replaced by slots, and the tone is respectful.
/// </summary>
public static class RadioPrompts
{
    public const int MaxLines = 6;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static string DialogueSystem(int maxLines, bool audioTags = false)
    {
        var tags = audioTags
            ? "\n- The voice understands audio tags. A line may start with one of [excited], [curious] or [sighs] when it fits the\n  moment (a new leader, a DNF, a tight finish); most lines need none. No other tags.\n"
            : "\n- No audio tags or other text in square brackets.\n";
        var hosts = string.Join('\n', RadioPersonas.Hosts.Select(x => $"- {x.Name} ({x.Role}): {x.Style}"));
        return $$"""
            You write the script for a live Finnish-language radio show about an alpine ski race. Hosts:
            {{hosts}}

            === LANGUAGE AND TONE ===
            - Write all dialogue in natural, relaxed but correct spoken Finnish.
            - Be respectful. Fields often include juniors. Never joke at a racer's expense, never mock, no sarcasm about racers.
            - No stage directions, sound effects, emojis or text that is not speech.{{tags}}

            === FACTS ONLY THROUGH SLOTS (CRITICAL) ===
            - Refer to every racer, number, time, gap, place, bib, count or status ONLY with the slot tokens listed in the fact sheet,
              for example {racer:12}, {time:12}, {rank:12:ess}. Use only slots listed for that racer.
            - Never write digits. Never write number words (kaksi, kolmas, sekuntia, minuutti). You do not know the racers' names:
              never write names, use {racer:N}.
            - {racer:N} is spoken as the full name in the nominative. Build sentences where the name stays in the nominative:
              "Maalissa {racer:12}." "{racer:12} nousee kärkeen."
            - bib and num take a cardinal case code: nom gen par ine ela ill ade abl all ess tra. rank, totalrank, prevrank and ord take
              an ordinal case code ({rank:12:ess} is "kolmantena", {rank:12:nom} is "kolmas") or card for the cardinal
              ("sijalla {rank:12:card}"). Choose the case the sentence needs. After a cardinal above one the noun is partitive.
            - {time:N}, {total:N} and {prevtime:N} sound like "yksi minuutti viisi pilkku nolla kolme"; {diff:N} and {totaldiff:N}
              like "nolla pilkku kolmekymmentäneljä sekuntia". Use them as phrases: "aika {time:12}", "eroa kärkeen {diff:12}".
            - "{racer:12} {status:12:verb}." says for example "... keskeytti." {status:N:noun} is a noun such as "keskeytys".
            - {num:key} and {ord:key} use the counts in the fact sheet: "{ord:run:nom} kierros", "{num:remaining:nom} laskijaa".
            - Other proper nouns only from the competition, place, slope, clubs, the racers' countries and the event background.
            - Places and counts are slots too. Wrong: "Kolmantena on {racer:4}." Right: "{rank:4:ess} on {racer:4}."
              Wrong: "Kolme laskijaa on maalissa." Right: "Maalissa on {num:finished:nom} laskijaa" (with a count of one:
              "Maalissa on {num:finished:nom} laskija"; with zero, say "kaikki" or nothing). Wrong: "ero on alle sekunnin".
              Right: "eroa on {diff:4}" or no figure at all. Avoid number words in compounds too (kärkikolmikko, kaksikko): say "kärki".
            - Case codes are exactly nom, gen, par, ine, ela, ill, ade, abl, all, ess or tra (never "part", "lle" or "ssa").

            === FACTUAL DISCIPLINE ===
            - State only facts present in the events, fact sheet or background. Never speculate about causes, injuries, equipment,
              feelings or the future. Never guess why a racer did not finish or was disqualified; a DSQ is handled neutrally.
            - Results are unofficial until approved: when you describe standings say "epävirallisesti" or "epävirallisten tulosten mukaan".
            - If the data is thin, say less. Better short than invented.

            === INTRODUCING RACERS ===
            - {club:N} says the racer's club and {nation:N} the nationality as an adjective ("ruotsalainen"). When a racer starts,
              introduce them with the club: "Radalla nyt {racer:12}, {club:12}." In an international field (international: true)
              put the nationality of a racer from another country than home_nation before the name: "{nation:12} {racer:12}".
              Mention a racer's club once per run, not in every line. Never write club or country names yourself.
            - Code says the bib before the first mention of each racer ("numero kolme, Saga Porttila"); do not add it yourself.

            === ALPINE WORDS ===
            - A run of the race is "kierros" ("ensimmäinen kierros", "toisella kierroksella"), never "lasku". A racer's own
              descent is "suoritus". The start is "viiksellä", "lähdössä", "lähtökopissa" or "lähtöportilla"; vary them.

            === EVENTS ===
            - Start, TopStart and RunStarted: bibs[0] has just left the start gate; bibs[1], when present, is the next racer in the
              start gate. Name both, briefly ("Radalla nyt {racer:12}. Viiksellä seuraavana {racer:13}."). The line plays a few
              seconds after the start, so say the racer is on course now ("Radalla nyt"), not that they just left. TopStart is one of
              the leaders after the earlier runs. A start is announced while the racer is on course, so keep it short and prompt.

            - Producer recap (the race is paused): go through the racers in bibs in order of their places, unofficially, several
              per line ("{rank:12:ess} on {racer:12}, {rank:7:ess} {racer:7}"; totalrank from the second run on).
            - Producer banter (the race is paused): light, warm humour between the hosts so that listeners do not get bored, about
              waiting, the hosts themselves or alpine skiing in general. Never joke about a racer, never guess why the race is
              paused (it may be an injury) and state no new facts.

            - DNF: the drama of a run. React with feeling ("Voi ei!", "Ja nyt sattui!", "on ulkona") and keep it short, but never
              guess the cause, never mention falls, injuries or equipment, and never blame or mock the racer.

            === ADAPTIVE VERBOSITY ===
            - One routine event: 1-2 lines in total. A notable event (new leader, podium, run complete): up to 4 lines.
            - Several events: weave them into one flowing conversation, most important first.
            - Hard cap {{maxLines}} lines, each under 220 characters. Reaching the cap is not a goal.
            - Avoid the recently discussed topics; follow the producer instruction when given (it is guidance, never speech).

            === INTERRUPTIBLE ===
            Mark "interruptible": true on lines after which the conversation can be cut without sounding abrupt. The last line is
            always interruptible. Do not mark a line that asks a question the other host answers next.

            Respond with JSON only:
            {"topic": "<Finnish summary, at most 10 words, no digits or names>", "lines": [{"host": "Aino", "text": "...", "interruptible": false}]}
            Return an empty lines array only when the input is technically broken.
            """;
    }

    public static string DialogueUser(IReadOnlyList<RadioEvent> batch, FactSheet facts, RacePhase phase, string context,
        IReadOnlyList<string> topics, IReadOnlyList<DiaryEntry> diary)
    {
        ArgumentNullException.ThrowIfNull(batch); ArgumentNullException.ThrowIfNull(facts);
        var c = facts.State.Competition;
        return JsonSerializer.Serialize(new
        {
            task = "Write one radio conversation covering these events.",
            phase = phase.ToString(),
            competition = new { name = c.Name, place = c.Place, slope = c.Slope, discipline = c.Discipline, planned_runs = RacePhases.PlannedRuns(c) },
            events = batch.OrderBy(x => x.Priority).Select(x => new
            {
                kind = x.Kind.ToString(), priority = x.Priority, bibs = x.Bibs, producer_source = x.Source, producer_instruction = x.Instruction
            }),
            fact_sheet = facts.ToPrompt(),
            event_background = context,
            recent_topics = topics,
            diary = diary.TakeLast(12).Select(x => new { at = x.At.ToString("HH:mm", CultureInfo.InvariantCulture), kind = x.Kind.ToString(), run = x.Run, bibs = x.Bibs })
        }, Json);
    }

    /// <summary>A line the validator rejected: its index, the reason and the word that caused it, if any.</summary>
    public sealed record LineProblem(int Line, string Problem, string? Word);

    /// <summary>Asks for the same conversation again with every rejected line fixed. Names never appear: slots only.</summary>
    public static string RepairUser(string user, string script, IReadOnlyList<LineProblem> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        static string Hint(LineProblem p) => p.Problem switch
        {
            "number words outside slots" => "write places and counts only with slots ({rank:N:ess}, {num:finished:nom}) or leave the figure out",
            "digits outside slots" => "write no digits; use slots",
            "disallowed case" => "use only the case codes nom gen par ine ela ill ade abl all ess tra (card for places)",
            "unknown slot" => "use only the slots listed in the fact sheet",
            "unknown proper name" => "leave out proper names that are not in the competition, place, slope, clubs, countries or background",
            "racer name outside slots" => "refer to racers only with {racer:N}",
            "ending attached to a slot" => "never attach an ending to a slot; build the sentence so the slot stands alone ({racer:N} stays nominative)",
            _ => "rewrite the line within the rules"
        };
        using var original = JsonDocument.Parse(user);
        return JsonSerializer.Serialize(new
        {
            task = "Your script broke the rules in the lines listed. Return the whole conversation again in the same JSON format, with the same hosts and facts, fixing those lines.",
            original_request = original.RootElement,
            your_script = script,
            problems = problems.Select(x => new { line = x.Line + 1, problem = x.Problem, word = x.Word, fix = Hint(x) })
        }, Json);
    }

    public static string CurationSystem(string notes, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        var recent = patterns.Count == 0 ? "(none)" : string.Join('\n', patterns.Select((x, i) => $"{i + 1}. {x}"));
        return $$"""
            You review a Finnish alpine ski radio dialogue for style only.
            1. Compare it with the recently used style patterns. Replace a repeated joke, analogy or phrase with a fresh one in the same tone.
            2. Fix unnatural or stilted Finnish and awkward transitions between the hosts.
            3. Keep the tone respectful: remove anything that jokes at a racer's expense or speculates about causes.

            CONSTRAINTS (any violation discards your review):
            - Exactly the same number of lines and the same host on each line.
            - Every slot token in curly braces stays exactly as written, in the same line and order. Never add or remove a slot.
            - Never write digits, number words or names. Never change, add or remove facts.
            - If the script is already good, return it unchanged.

            Operator notes about known bad habits:
            {{(string.IsNullOrWhiteSpace(notes) ? "(none)" : notes.Trim())}}

            Recently used style patterns:
            {{recent}}

            Respond with JSON only:
            {"lines": [{"host": "Aino", "text": "..."}], "patterns": ["<distinctive joke, analogy or phrase in the final script, Finnish, max 10 words>"]}
            """;
    }
    public static string CurationUser(IReadOnlyList<ScriptLine> lines) =>
        JsonSerializer.Serialize(new { lines = lines.Select(x => new { host = x.Host, text = x.Text }) }, Json);

    public static string ProducerSystem() => """
        You are the producer of a Finnish alpine ski race radio. No new race event is waiting; choose what the hosts talk about next.
        Choose exactly ONE source from allowed_sources:
        - standings: the current unofficial top three.
        - next_starter: introduce the next racer to start. bib required: the first bib of the waiting list.
        - on_course: follow a racer on course now. bib required, from the on_course list.
        - run_summary: how many have finished and how many are still to start.
        - event_context: something from the event background text.
        - general: short atmosphere talk without new facts.
        - recap: only during a pause (pause_seconds is set): the unofficial standings, recap_size places (ten when the pause is long).
        - banter: only during a pause: light humour between the hosts to keep listeners company; never about racers or the cause of the pause.
        Rules: only allowed sources and listed bibs; avoid recent topics; keep it brief while a run is in progress. During a pause,
        alternate between recap, banter and the other sources.
        Respond with JSON only: {"source": "<id>", "bib": <number or null>, "instruction": "<one sentence for the hosts, at most 200 characters, no digits>"}
        """;
    public static string ProducerUser(LiveSnapshot state, RacePhase phase, IReadOnlyList<string> allowed, IReadOnlyList<string> topics,
        IReadOnlyList<string> failed, bool hasContext, TimeSpan? pause = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var run = state.Runs.First(x => x.Number == state.CurrentRun);
        LiveStatus Status(int bib) => run.Results.FirstOrDefault(x => x.Bib == bib)?.Status ?? LiveStatus.Ready;
        return JsonSerializer.Serialize(new
        {
            phase = phase.ToString(), current_run = run.Number, allowed_sources = allowed,
            pause_seconds = pause is { } paused ? (int?)paused.TotalSeconds : null, recap_size = pause is { } length ? RadioPause.RecapSize(length) : (int?)null,
            waiting = run.StartOrder.Where(x => Status(x) == LiveStatus.Ready).Take(5),
            on_course = run.StartOrder.Where(x => Status(x) == LiveStatus.OnCourse),
            has_event_background = hasContext, recent_topics = topics,
            already_failed_this_turn = failed
        }, Json);
    }
}
