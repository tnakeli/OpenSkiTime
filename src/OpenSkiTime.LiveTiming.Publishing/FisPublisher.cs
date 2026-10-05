using System.Globalization;
using System.Xml.Linq;

namespace OpenSkiTime.LiveTiming.Publishing;

public sealed class FisPublisher(IFisLiveTimingTransport transport, string password, TimeProvider? clock = null) : IAsyncDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private LiveSnapshot? _sent;
    private long _sequence;
    private string _codex = "";
    private int? _activeRun;
    public async Task PublishAsync(LiveSnapshot snapshot, bool refresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Validate();
        if (!snapshot.Competition.IsFis || !int.TryParse(snapshot.Competition.Codex, out var codex) || codex < 1
            || snapshot.Competition.Discipline is not ("SL" or "GS" or "SG" or "DH" or "SC")
            || snapshot.Competition.Gender is not ("M" or "L" or "A") || string.IsNullOrWhiteSpace(password)
            || snapshot.Competitors.Any(x => string.IsNullOrWhiteSpace(x.FisCode) || string.IsNullOrWhiteSpace(x.Nation)))
        { throw new LiveValidationException("FIS live timing needs an alpine FIS competition, codex, gender, password and complete competitor codes/nations."); }
        _codex = snapshot.Competition.Codex;
        var structural = _sent is null || _sent.Competition != snapshot.Competition || !_sent.Competitors.SequenceEqual(snapshot.Competitors)
            || _sent.CurrentRun != snapshot.CurrentRun || _sent.Runs.Length != snapshot.Runs.Length
            // A correction in an earlier run changes subsequent cumulative standings even when
            // the later run's individual result rows have not changed. Restore all runs together.
            || snapshot.Runs.Where(r => r.Number < snapshot.CurrentRun).Any(r =>
            {
                var previous = _sent.Runs.FirstOrDefault(p => p.Number == r.Number);
                return previous is null || previous.Results.Length != r.Results.Length
                    || r.Results.Any(x => !previous.Results.Any(p => p.Bib == x.Bib && StandalonePublisher.Equivalent(p, x)));
            })
            || snapshot.Runs.Any(r => !_sent.Runs.Any(p => p.Number == r.Number && p.StartOrder.SequenceEqual(r.StartOrder)))
            || snapshot.Runs.Any(r => r.Results.Any(x => _sent.Runs.FirstOrDefault(p => p.Number == r.Number)?.Results.FirstOrDefault(p => p.Bib == x.Bib)?.Splits
                .Any(p => !x.Splits.Any(s => s.Number == p.Number)) == true))
            || snapshot.Runs.Any(r => r.Results.Any(x => x.Status is LiveStatus.Ready or LiveStatus.Review
                && _sent.Runs.FirstOrDefault(p => p.Number == r.Number)?.Results.FirstOrDefault(p => p.Bib == x.Bib)?.Status != x.Status))
            // FIS has no message that takes back a finish or a status. A racer returned to the course (an ignored false
            // finish, a cleared DNF) is restored by rebuilding the run, as for any other removal.
            || snapshot.Runs.Any(r => r.Results.Any(x => x.Status == LiveStatus.OnCourse
                && HasResult(_sent.Runs.FirstOrDefault(p => p.Number == r.Number)?.Results.FirstOrDefault(p => p.Bib == x.Bib))));
        if (refresh || structural)
        {
            // A startlist clears that run's results (v53 p41). Restore every known run in ascending order.
            await SendAsync(RaceInfo(snapshot), ct);
            foreach (var run in snapshot.Runs.OrderBy(x => x.Number))
            {
                await SendAsync(StartList(snapshot, run), ct);
                await ActivateAsync(run.Number, ct, force: true);
                var finishes = FinishValues(snapshot, run);
                foreach (var result in run.Results) { await SendResultAsync(run, result, null, null, finishes, ct); }
            }
            await ActivateAsync(snapshot.CurrentRun, ct);
        }
        else
        {
            foreach (var run in snapshot.Runs)
            {
                var sentRun = _sent!.Runs.First(x => x.Number == run.Number);
                // Rank and difference describe the standings, so a finish is sent again whenever its transmitted values
                // change, also when only another racer's result moved them (in Run 2, the combined standings).
                var finishes = FinishValues(snapshot, run);
                var sentFinishes = FinishValues(_sent, sentRun);
                var changes = run.Results.Where(r => !sentRun.Results.Any(p => p.Bib == r.Bib && StandalonePublisher.Equivalent(p, r))
                    || finishes.GetValueOrDefault(r.Bib) != sentFinishes.GetValueOrDefault(r.Bib)).ToArray();
                if (changes.Length == 0) { continue; }
                await ActivateAsync(run.Number, ct);
                foreach (var result in changes)
                {
                    await SendResultAsync(run, result, sentRun.Results.FirstOrDefault(p => p.Bib == result.Bib),
                        sentFinishes.TryGetValue(result.Bib, out var sent) ? sent : null, finishes, ct);
                }
            }
            await ActivateAsync(snapshot.CurrentRun, ct);
        }
        var current = snapshot.Runs.First(x => x.Number == snapshot.CurrentRun);
        var next = current.StartOrder.FirstOrDefault(bib => current.Results.FirstOrDefault(r => r.Bib == bib)?.Status is null or LiveStatus.Ready);
        if (next > 0) { await SendAsync(new XElement("raceevent", new XAttribute("timestamp", Stamp(snapshot.UpdatedAt)), new XElement("nextstart", new XAttribute("bib", next))), ct); }
        _sent = snapshot;
    }
    public Task KeepAliveAsync(CancellationToken ct) => SendAsync(new XElement("keepalive"), ct);
    public void Disconnect() { transport.Disconnect(); _activeRun = null; }
    private async Task ActivateAsync(int run, CancellationToken ct, bool force = false)
    {
        if (force || _activeRun != run) { await SendAsync(Active(run), ct); _activeRun = run; }
    }
    private Task SendAsync(XElement content, CancellationToken ct)
    {
        var seq = ++_sequence;
        var root = new XElement("livetiming", new XAttribute("codex", _codex), new XAttribute("passwd", password),
            new XAttribute("sequence", seq), new XAttribute("timestamp", Stamp(_clock.GetUtcNow())), content);
        var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting);
        return transport.SendAsync(xml, seq, ct);
    }
    private static XElement RaceInfo(LiveSnapshot s) => new("raceinfo",
        new XElement("event", s.Competition.Name), new XElement("name", s.Competition.Name), new XElement("slope", s.Competition.Slope),
        new XElement("discipline", s.Competition.Discipline), new XElement("gender", s.Competition.Gender),
        new XElement("category", s.Competition.Category), new XElement("place", s.Competition.Place),
        s.Runs.Select(r => new XElement("run", new XAttribute("no", r.Number), new XElement("discipline", ""),
            new XElement("year", s.Competition.Date.Year), new XElement("month", s.Competition.Date.Month), new XElement("day", s.Competition.Date.Day),
            new XElement("racedef", Enumerable.Range(1, s.Competition.IntermediateCount).Select(i => new XElement("inter", new XAttribute("i", i))), new XElement("finish")))));
    private static XElement StartList(LiveSnapshot s, LiveRun run) => new("startlist", new XAttribute("runno", run.Number), new XAttribute("timestamp", Stamp(run.ListCreatedAt)),
        run.StartOrder.Select((bib, i) => { var c = s.Competitors.First(x => x.Bib == bib); return new XElement("racer", new XAttribute("order", i+1),
            new XElement("bib", bib), new XElement("lastname", c.LastName.ToUpperInvariant()), new XElement("firstname", c.FirstName), new XElement("nat", c.Nation), new XElement("fiscode", c.FisCode)); }));
    private XElement Active(int run) => new("command", new XAttribute("timestamp", Stamp(_clock.GetUtcNow())), new XElement("active", new XAttribute("runno", run)));
    // correction="y" marks a change to a result FIS already has (v53 p69): a finish or a DNS/DNF/DSQ/NPS sent before.
    // A first finish or status after a start is a new result.
    private static bool HasResult(LiveResult? sent) => sent?.Status is LiveStatus.Finished or LiveStatus.DNS or LiveStatus.DNF
        or LiveStatus.DSQ or LiveStatus.NPS;
    // Alpine finish carries this run's net time; FIS adds the earlier runs itself (v53 p23). Difference and rank describe
    // the standings: from Run 2 on, the combined time of every run so far, ex aequo for equal totals. A racer without a
    // finished time in every earlier run has no combined time and is sent with the run time only (v53 p57).
    private static Dictionary<int, FinishValue> FinishValues(LiveSnapshot state, LiveRun run)
    {
        var earlier = state.Runs.Where(x => x.Number < run.Number).ToArray();
        var finished = run.Results.Where(x => x.Status == LiveStatus.Finished).ToArray();
        var totals = finished.ToDictionary(x => x.Bib, x => earlier.All(e => e.Results.Any(p => p.Bib == x.Bib && p.Status == LiveStatus.Finished))
            ? x.Hundredths!.Value + earlier.Sum(e => e.Results.First(p => p.Bib == x.Bib && p.Status == LiveStatus.Finished).Hundredths!.Value)
            : (long?)null);
        var ranked = totals.Values.OfType<long>().ToArray();
        return finished.ToDictionary(x => x.Bib, x => totals[x.Bib] is { } total
            ? new FinishValue(x.Hundredths!.Value, total - ranked.Min(), ranked.Count(t => t < total) + 1)
            : new FinishValue(x.Hundredths!.Value, null, null));
    }
    private sealed record FinishValue(long Time, long? Diff, int? Rank);
    private async Task SendResultAsync(LiveRun run, LiveResult r, LiveResult? old, FinishValue? sentFinish,
        IReadOnlyDictionary<int, FinishValue> finishes, CancellationToken ct)
    {
        var items = new List<XElement>();
        if (r.StartedAt is { } started && (old is null || old.StartedAt != started))
        { items.Add(new("start", new XAttribute("bib", r.Bib), new XAttribute("timestamp", Stamp(started)))); }
        foreach (var split in r.Splits)
        {
            var previous = old?.Splits.FirstOrDefault(x => x.Number == split.Number);
            if (previous == split) { continue; }
            var times = run.Results.SelectMany(x => x.Splits.Where(i => i.Number == split.Number)).Select(x => x.Hundredths).ToArray();
            items.Add(TimeElement("inter", r.Bib, split.Hundredths, split.Hundredths - times.Min(), times.Count(x => x < split.Hundredths) + 1, split.At, previous is not null,
                new XAttribute("i", split.Number)));
        }
        var status = r.Status switch { LiveStatus.DNS => "dns", LiveStatus.DNF => "dnf", LiveStatus.DSQ => "dq", LiveStatus.NPS => "nps", _ => null };
        if (status is not null && old?.Status != r.Status)
        { items.Add(new(status, new XAttribute("bib", r.Bib), new XAttribute("timestamp", Stamp(r.At)), HasResult(old) && status != "nps" ? new XAttribute("correction", "y") : null)); }
        if (finishes.TryGetValue(r.Bib, out var finish) && (old?.Status != LiveStatus.Finished || finish != sentFinish))
        { items.Add(TimeElement("finish", r.Bib, finish.Time, finish.Diff, finish.Rank, r.At, HasResult(old))); }
        if (items.Count > 0) { await SendAsync(new XElement("raceevent", new XAttribute("timestamp", Stamp(r.At)), items), ct); }
    }
    private static XElement TimeElement(string tag, int bib, long elapsed, long? diff, int? rank, DateTimeOffset at, bool correction, XAttribute? extra = null)
        => new(tag, new XAttribute("bib", bib), new XAttribute("timestamp", Stamp(at)), correction ? new XAttribute("correction", "y") : null, extra,
            new XElement("time", Time(elapsed)), diff is { } gap ? new XElement("diff", Time(gap)) : null, rank is { } place ? new XElement("rank", place) : null);
    private static string Time(long value) => value >= 6000 ? string.Create(CultureInfo.InvariantCulture, $"{value / 6000}:{value / 100 % 60:00}.{value % 100:00}") : string.Create(CultureInfo.InvariantCulture, $"{value / 100}.{value % 100:00}");
    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    public ValueTask DisposeAsync() => transport.DisposeAsync();
}
