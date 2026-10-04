namespace OpenSkiTime.Timing;

public sealed record BackupComparisonRow(int Bib, string Position, long? ATicks, long? BTicks,
    long? DifferenceTicks, string Warning, bool IsElapsed = false)
{
    // False for A observations that existed before this B session connected; they are never reported as missing.
    public bool Monitored { get; init; } = true;
}
public sealed record BackupMonitorSnapshot(IReadOnlyList<BackupComparisonRow> Rows, IReadOnlyList<string> Warnings);

// Operational comparison only. All clock context is supplied and this class cannot alter timing results.
public sealed class BackupMonitor
{
    private Guid? _session;
    private Guid? _list;
    private readonly HashSet<string> _beforeConnection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _firstSeen = new(StringComparer.Ordinal);
    private TimingSnapshot? _lastA;
    private IReadOnlyList<TimingObservation>? _lastB;
    private long _lastTolerance;
    private IReadOnlyList<EvidenceMatch> _matches = [];
    private Dictionary<string, TimingObservation> _aByKey = new(StringComparer.Ordinal);
    private Dictionary<string, TimingObservation> _bByKey = new(StringComparer.Ordinal);

    public BackupMonitorSnapshot Update(Guid? session, TimingSnapshot? a, IReadOnlyList<TimingObservation> b,
        DateTimeOffset now, BackupComparisonPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(b); ArgumentNullException.ThrowIfNull(policy);
        if (session is null || a is null) { Reset(); return new([], []); }
        if (_session != session || _list != a.ListId)
        {
            Reset(); _session = session; _list = a.ListId;
            foreach (var item in a.Observations) { _beforeConnection.Add(item.Observation.Key); }
        }
        if (!ReferenceEquals(a, _lastA) || !ReferenceEquals(b, _lastB) || policy.PairingToleranceTicks != _lastTolerance)
        {
            var observations = a.Observations.ToDictionary(x => x.Observation.Key, x => x.Observation, StringComparer.Ordinal);
            var targets = a.Results.SelectMany(result => new[] { (Key: result.StartKey, Channel: 0), (Key: result.FinishKey, Channel: 1) }
                .Where(x => x.Key is not null && observations.TryGetValue(x.Key, out var o) && o.DeviceTicks is not null)
                .Select(x => new EvidenceTarget(x.Key!, result.Bib, x.Channel, observations[x.Key!].DeviceTicks!.Value))).ToArray();
            foreach (var target in targets)
            {
                if (!_beforeConnection.Contains(target.Key)) { _firstSeen.TryAdd(target.Key, now); }
            }
            var evidence = b.Where(x => x.Kind == ObservationKind.Impulse && x.DeviceTicks is not null && x.Channel is 0 or 1)
                .Select(x => new EvidenceTimestamp(x.Key, x.DeviceTicks!.Value, x.Precision, x.Channel, x.Message)).ToArray();
            _matches = TimingEvidenceMatching.Match(targets, evidence, policy.PairingToleranceTicks);
            _aByKey = observations;
            _bByKey = b.ToDictionary(x => x.Key, StringComparer.Ordinal);
            _lastA = a; _lastB = b; _lastTolerance = policy.PairingToleranceTicks;
        }
        var rows = new List<BackupComparisonRow>();
        foreach (var match in _matches)
        {
            var warning = _firstSeen.TryGetValue(match.Target.Key, out var seen)
                ? policy.Warning(match.Target.Channel, match.DifferenceTicks, now - seen) : null;
            if (warning is not null && match.State.StartsWith("Ambiguous", StringComparison.Ordinal))
            { warning = "B match ambiguous · check timestamps"; }
            rows.Add(new(match.Target.Bib, match.Target.Channel == 0 ? "Start" : "Finish", match.Target.Ticks,
                match.Evidence?.Ticks, match.DifferenceTicks, warning ?? "") { Monitored = _firstSeen.ContainsKey(match.Target.Key) });
        }
        var byPosition = rows.ToDictionary(x => (x.Bib, x.Position));
        var matchedB = _matches.Where(x => x.Evidence is not null).ToDictionary(x => (x.Target.Bib, x.Target.Channel), x => x.Evidence!.Key);
        foreach (var result in a.Results)
        {
            byPosition.TryGetValue((result.Bib, "Start"), out var start);
            byPosition.TryGetValue((result.Bib, "Finish"), out var finish);
            if (start?.ATicks is not { } aStart || finish?.ATicks is not { } aFinish
                || start.BTicks is not { } bStart || finish.BTicks is not { } bFinish) { continue; }
            var monitored = result.FinishKey is { } finishKey && _firstSeen.ContainsKey(finishKey);
            var aContinuous = _aByKey[result.StartKey!].ClockId == _aByKey[result.FinishKey!].ClockId;
            var bContinuous = _bByKey[matchedB[(result.Bib, 0)]].ClockId == _bByKey[matchedB[(result.Bib, 1)]].ClockId;
            if (!aContinuous || !bContinuous)
            {
                rows.Add(new(result.Bib, "Elapsed", null, null, null,
                    monitored ? "Clock context changed · verify start/finish continuity" : "", true));
                continue;
            }
            var elapsedA = aFinish - aStart; var elapsedB = bFinish - bStart;
            if (elapsedA < 0 || elapsedB < 0) { continue; }
            var delta = elapsedB - elapsedA;
            var warning = monitored && Math.Abs(delta) > policy.StartWarningTicks + policy.FinishWarningTicks
                ? "A/B elapsed-time difference" : "";
            rows.Add(new(result.Bib, "Elapsed", elapsedA, elapsedB, delta, warning, true));
        }
        return new(rows, rows.Where(x => x.Warning.Length != 0)
            .Select(x => $"Bib {x.Bib} {x.Position.ToLowerInvariant()}: {x.Warning}").ToArray());
    }

    public void Reset()
    {
        _session = _list = null; _beforeConnection.Clear(); _firstSeen.Clear();
        _lastA = null; _lastB = null; _matches = [];
        _aByKey.Clear(); _bByKey.Clear();
    }
}

public sealed class BackupDisplayWindow
{
    private DateTimeOffset? _until;
    public void Show(DateTimeOffset now) => _until = now.AddSeconds(30);
    public void Hide() => _until = null;
    public bool IsVisible(DateTimeOffset now) => _until is { } until && now < until;
    public int RemainingSeconds(DateTimeOffset now) => IsVisible(now) ? (int)Math.Ceiling((_until!.Value - now).TotalSeconds) : 0;
}
