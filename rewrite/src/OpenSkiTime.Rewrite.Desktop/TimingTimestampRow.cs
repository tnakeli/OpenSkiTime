using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed record TimingTimestampCell(Guid ListId, ObservationReview Review, TimingDecision Decision)
{
    public string Key => Review.Observation.Key;
    public string Time => TimingTime.FormatTimeOfDay(Review.Observation.DeviceTicks);
    public int Channel => Review.Observation.Channel!.Value;
    public string Position => Channel == 0 ? "Start" : Channel == 1 ? "Finish" : $"Interm {Channel - 1}";
    public string State => Review.Ignored ? "Ignored" : Review.Bib is null ? "Unassigned" : $"Bib {Review.Bib}";
    public string Hint => $"{Position} · {Time} · {State}. Drop a competitor here to assign this timestamp.";
}

public sealed record TimingTimestampRow(string Key, int? Bib, string Name, IReadOnlyList<TimingTimestampCell?> Cells)
{
    public static IReadOnlyList<TimingTimestampRow> Create(TimingSnapshot snapshot, int intermediateCount, bool showIgnored)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var channels = Enumerable.Range(2, intermediateCount).Prepend(0).Append(1).ToArray();
        var input = snapshot.Observations.Where(x => x.Observation.Kind == ObservationKind.Impulse
            && x.Observation.DeviceTicks is not null && x.DuplicateOf is null
            && x.Observation.Channel is { } channel && channels.Contains(channel)
            && (!x.Ignored || showIgnored)).ToArray();
        TimingTimestampCell Cell(ObservationReview review) => new(snapshot.ListId, review,
            TimingEngine.CurrentDecision(new(DecisionKind.Assignment, review.Observation.Key), snapshot.Audit));
        var rows = new List<TimingTimestampRow>();
        foreach (var pulse in input.Where(x => x.Bib is null).Reverse())
        {
            rows.Add(new(pulse.Observation.Key, null, pulse.Ignored ? "Ignored impulse" : "Unassigned impulse",
                channels.Select(c => c == pulse.Observation.Channel ? Cell(pulse) : null).ToArray()));
        }
        var byBib = input.Where(x => x.Bib is not null).GroupBy(x => x.Bib!.Value).ToDictionary(x => x.Key, x => x.ToArray());
        foreach (var result in snapshot.Results.OrderBy(x => x.Entry.Position))
        {
            if (!byBib.TryGetValue(result.Bib, out var pulses)) { continue; }
            var byChannel = pulses.GroupBy(x => x.Observation.Channel!.Value).ToDictionary(x => x.Key, x => x.ToArray());
            // Keep extra assigned impulses separately; never hide them behind a single chosen timestamp.
            for (var i = 0; i < byChannel.Values.Max(x => x.Length); i++)
            {
                rows.Add(new($"{result.Bib}:{i}", result.Bib, result.Name + (i == 0 ? "" : " · extra impulse"),
                    channels.Select(c => byChannel.TryGetValue(c, out var values) && i < values.Length ? Cell(values[i]) : null).ToArray()));
            }
        }
        return rows;
    }
}

public sealed record TimingDragCompetitor(Guid Workspace, Guid ListId, int Bib, string Name, string? StartKey, IReadOnlyList<string> AssignedKeys);
