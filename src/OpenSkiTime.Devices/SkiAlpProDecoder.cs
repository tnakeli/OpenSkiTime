using System.Globalization;
using System.Text.Json;
using OpenSkiTime.Application;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Devices;

/// <summary>
/// Decodes the journalled rows of a Ski-Alp-Pro test import. Rows Ski-Alp-Pro itself does not count (a non-zero status:
/// false starts, deleted and moved impulses, classification marks) and its classified results are information only.
/// </summary>
public sealed class SkiAlpProDecoder(CaptureSession session) : ITimingDecoder
{
    // Ski-Alp-Pro stores the time of day as integer microseconds.
    private const long MicrosecondsPerDay = 86_400_000_000;

    public IReadOnlyList<TimingObservation> Complete() => [];

    public IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        SkiAlpProPacket? row;
        try { row = SkiAlpProPacket.Read(packet.Bytes); }
        catch (JsonException) { row = null; }
        if (row is null) { return [Make(packet, ObservationKind.Invalid, "Unreadable Ski-Alp-Pro row; original bytes retained.")]; }
        var text = $"{row.Table} row {row.Row.ToString(CultureInfo.InvariantCulture)} · bib {row.Bib.ToString(CultureInfo.InvariantCulture)}";
        if (row.Position is not { } position) { return [Make(packet, ObservationKind.Information, text)]; }
        if (row.Status != 0)
        { return [Make(packet, ObservationKind.Information, $"{text} · not counted by Ski-Alp-Pro (status {row.Status.ToString(CultureInfo.InvariantCulture)})")]; }
        if (position is < 0 or > 21 || !long.TryParse(row.Hour2, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds)
            || microseconds is <= 0 or >= MicrosecondsPerDay)
        { return [Make(packet, ObservationKind.Invalid, text + " · time or position cannot be read")]; }
        var ticks = session.Options.DeviceDate.ToDateTime(TimeOnly.MinValue).Ticks + microseconds * TimeSpan.TicksPerMicrosecond;
        var precision = Precision(microseconds, row.Accuracy);
        return [Make(packet, ObservationKind.Impulse, $"{text} · {TimingTime.FormatTimeOfDay(ticks, precision)}") with
        {
            Channel = position, DeviceTicks = ticks, Precision = precision, ClockId = $"{session.Id:N}:ski-alp-pro",
        }];
    }

    // The accuracy the event was timed with, or the decimals the value really has when it has more.
    private static int Precision(long microseconds, int accuracy)
    {
        var precision = Math.Clamp(accuracy, 0, 6);
        long step = 1;
        for (var i = precision; i < 6; i++) { step *= 10; }
        for (; microseconds % step != 0; step /= 10) { precision++; }
        return precision;
    }

    private TimingObservation Make(RawTimingPacket packet, ObservationKind kind, string message)
    {
        var key = $"{session.Id:N}:{packet.Sequence.ToString(CultureInfo.InvariantCulture)}:0";
        return new(key, session.Id, packet.Sequence, packet.Source, "ski-alp-pro:" + key, kind, null, null, 0, null, false, "", message);
    }
}
