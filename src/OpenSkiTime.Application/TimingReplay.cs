using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public sealed record TimingReplayUnit(IReadOnlyList<CaptureSession> Sessions, IReadOnlyList<(CaptureSession Session, RawTimingPacket Packet)> Packets);

public static class TimingReplay
{
    // Session ownership is explicit: an open connection owned by the caller is not
    // an interrupted capture. Without that context, preserve recovery diagnostics.
    public static TimingSnapshot Restore(TimingReplayData data, ITimingDecoderFactory factory, IReadOnlySet<Guid>? activeSessionIds = null)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(factory);
        var observations = new List<TimingObservation>();
        foreach (var unit in Units(data.Sessions, data.Packets))
        {
            var decoders = new StreamDecoders();
            foreach (var session in unit.Sessions) { session.Options.Validate(); }
            foreach (var (session, packet) in unit.Packets)
            {
                var decoder = decoders.For(factory, session, packet, observations);
                observations.AddRange(Decode(decoder, packet));
            }
            observations.AddRange(decoders.Complete());
            foreach (var session in unit.Sessions.Where(x => !x.CleanStop && activeSessionIds?.Contains(x.Id) != true))
            {
                observations.Add(new($"{session.Id:N}:interrupted", session.Id, 0, session.Options.Endpoint,
                    $"interrupted:{session.Id:N}", ObservationKind.Invalid, null, null, 0, null, false, "",
                    "Capture ended unexpectedly. Recover missing impulses from device memory/backup and review before continuing."));
            }
        }
        return TimingEngine.Replay(data.List, observations, data.Audit, 0, 1);
    }

    // Consecutive sessions of one multi-device capture (same clock group) replay as one unit, merged in receive order
    // while each session keeps its own sequence order. Every other session replays alone, exactly as before.
    public static IReadOnlyList<TimingReplayUnit> Units(IReadOnlyList<CaptureSession> sessions, IReadOnlyList<RawTimingPacket> packets)
    {
        ArgumentNullException.ThrowIfNull(sessions); ArgumentNullException.ThrowIfNull(packets);
        var bySession = packets.GroupBy(x => x.SessionId).ToDictionary(x => x.Key, x => x.OrderBy(p => p.Sequence).ToArray());
        var units = new List<TimingReplayUnit>();
        for (var i = 0; i < sessions.Count;)
        {
            var unit = new List<CaptureSession> { sessions[i] };
            var group = sessions[i].Options.ClockGroup;
            while (group is not null && i + unit.Count < sessions.Count && sessions[i + unit.Count].Options.ClockGroup == group)
            { unit.Add(sessions[i + unit.Count]); }
            i += unit.Count;
            var queues = unit.Select(x => bySession.GetValueOrDefault(x.Id) ?? []).ToArray();
            var positions = new int[unit.Count];
            var ordered = new List<(CaptureSession, RawTimingPacket)>(queues.Sum(x => x.Length));
            while (true)
            {
                var next = -1;
                for (var s = 0; s < unit.Count; s++)
                {
                    if (positions[s] < queues[s].Length
                        && (next < 0 || queues[s][positions[s]].ReceivedAt < queues[next][positions[next]].ReceivedAt)) { next = s; }
                }
                if (next < 0) { break; }
                ordered.Add((unit[next], queues[next][positions[next]++]));
            }
            units.Add(new(unit, ordered));
        }
        return units;
    }

    internal static IReadOnlyList<TimingObservation> Decode(ITimingDecoder decoder, RawTimingPacket packet, bool includeInformation = false)
    {
        try { return decoder.Feed(packet).Where(x => includeInformation || x.Kind != ObservationKind.Information).ToArray(); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [new($"{packet.SessionId:N}:{packet.Sequence}:decoder-error", packet.SessionId, packet.Sequence, packet.Source,
                $"{packet.SessionId:N}:{packet.Sequence}:decoder-error", ObservationKind.Invalid, null, null, 0, null, false, "",
                "Device decoder failed; original bytes retained. Review this input before using results.")];
        }
    }

    public static string InputVersion(TimingReplayData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        // Every result assignment/change is audited. Heartbeats and unassigned gate noise
        // must not invalidate Run 2 while the device remains connected.
        return "results/v2:audit=" + (data.Audit.Count == 0 ? 0 : data.Audit[^1].Id).ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool InputVersionMatches(string? saved, string current)
    {
        ArgumentNullException.ThrowIfNull(current);
        // Older lists included raw packet counts/session closure before the audit suffix.
        return saved == current || (saved is not null && current.StartsWith("results/v2:audit=", StringComparison.Ordinal) && !saved.StartsWith("results/", StringComparison.Ordinal)
            && saved.EndsWith(current["results/v2".Length..], StringComparison.Ordinal));
    }
}
