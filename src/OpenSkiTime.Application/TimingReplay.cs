using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

public static class TimingReplay
{
    // Session ownership is explicit: an open connection owned by the caller is not
    // an interrupted capture. Without that context, preserve recovery diagnostics.
    public static TimingSnapshot Restore(TimingReplayData data, ITimingDecoderFactory factory, IReadOnlySet<Guid>? activeSessionIds = null)
    {
        ArgumentNullException.ThrowIfNull(data); ArgumentNullException.ThrowIfNull(factory);
        var observations = new List<TimingObservation>();
        foreach (var session in data.Sessions)
        {
            session.Options.Validate();
            var decoders = new Dictionary<string, ITimingDecoder>(StringComparer.Ordinal);
            foreach (var packet in data.Packets.Where(x => x.SessionId == session.Id).OrderBy(x => x.Sequence))
            {
                var key = $"{packet.Protocol}:{packet.Source}:{packet.Stream}";
                if (!decoders.TryGetValue(key, out var decoder))
                { decoder = factory.Create(session, packet.Protocol, packet.Source, packet.Stream); decoders[key] = decoder; }
                observations.AddRange(Decode(decoder, packet));
            }
            foreach (var decoder in decoders.Values) { observations.AddRange(decoder.Complete()); }
            if (!session.CleanStop && activeSessionIds?.Contains(session.Id) != true)
            {
                observations.Add(new($"{session.Id:N}:interrupted", session.Id, 0, session.Options.Endpoint,
                    $"interrupted:{session.Id:N}", ObservationKind.Invalid, null, null, 0, null, false, "",
                    "Capture ended unexpectedly. Recover missing impulses from device memory/backup and review before continuing."));
            }
        }
        return TimingEngine.Replay(data.List, observations, data.Audit, 0, 1);
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
