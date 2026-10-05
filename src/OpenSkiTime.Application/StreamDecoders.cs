using OpenSkiTime.Timing;

namespace OpenSkiTime.Application;

// One decoder per device stream of a capture. A packet from another stream of the same device source (a reconnect, or
// its disconnect notice) supersedes that source's earlier streams: a line left unfinished there can never be continued,
// so it is completed as incomplete input at that packet, instead of blocking a run change until capture stops. Live
// capture, replay and auxiliary reads all use this, so a replay reproduces the live observations.
internal sealed class StreamDecoders
{
    private readonly Dictionary<(Guid Session, string Protocol, string Source, string Stream), ITimingDecoder> _decoders = [];

    public bool HasPendingInput => _decoders.Values.Any(x => x.HasPendingInput);

    public ITimingDecoder For(ITimingDecoderFactory factory, CaptureSession session, RawTimingPacket packet, List<TimingObservation> superseded)
    {
        foreach (var (key, decoder) in _decoders)
        {
            if (key.Session == session.Id && key.Source == packet.Source && key.Stream != packet.Stream && decoder.HasPendingInput)
            { superseded.AddRange(decoder.Complete()); }
        }
        var own = (session.Id, packet.Protocol, packet.Source, packet.Stream);
        if (!_decoders.TryGetValue(own, out var current))
        {
            current = factory.Create(session, packet.Protocol, packet.Source, packet.Stream);
            _decoders.Add(own, current);
        }
        return current;
    }

    // Completes every stream, in the order the streams first appeared.
    public IReadOnlyList<TimingObservation> Complete()
    {
        var completed = _decoders.Values.SelectMany(x => x.Complete()).ToArray();
        _decoders.Clear();
        return completed;
    }

    public void Clear() => _decoders.Clear();
}
