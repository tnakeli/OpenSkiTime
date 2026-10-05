using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenSkiTime.Application;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Devices;

public sealed class AlgeDecoderFactory : ITimingDecoderFactory
{
    public ITimingDecoder Create(CaptureSession session, string protocol, string source, string stream)
    {
        ArgumentNullException.ThrowIfNull(session);
        return protocol switch
        {
            "alge-ascii/v1" => new AlgeAsciiDecoder(session, source, stream),
            "alge-timy-sdk/v1" => new AlgeTimySdkDecoder(session, source, stream),
            "alge-results/v1" => new AlgeResultsDecoder(session),
            _ => new DiagnosticDecoder(session)
        };
    }
}

// The journal retains the complete SDK envelope before this conversion. Never use the
// vendor byte-array contents as an ASCII fallback: the current x64 SDK copies them incorrectly.
public sealed class AlgeTimySdkDecoder(CaptureSession session, string source, string stream) : ITimingDecoder
{
    private readonly AlgeAsciiDecoder _ascii = new(session, source, stream);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public bool HasPendingInput => _ascii.HasPendingInput;

    public IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        try
        {
            using var json = JsonDocument.Parse(packet.Bytes);
            _ = Convert.FromBase64String(json.RootElement.GetProperty("sdkBytes").GetString()!);
            var text = StrictUtf8.GetString(Convert.FromBase64String(json.RootElement.GetProperty("sdkTextUtf8").GetString()!));
            if (text.Any(c => c > 127)) { throw new FormatException("Non-ASCII SDK text."); }
            return _ascii.Feed(packet with { Bytes = Encoding.ASCII.GetBytes(text) });
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            var incomplete = _ascii.Complete(); // Do not join a later chunk onto a damaged/omitted fragment.
            TimingObservation invalid = new($"{session.Id:N}:{packet.Sequence}:sdk-error", session.Id, packet.Sequence, source,
                $"{session.Id:N}:{packet.Sequence}:sdk-error", ObservationKind.Invalid, null, null, 0, null, false, "",
                "Invalid/non-ASCII Timy USB SDK input; both original SDK representations are retained.");
            return incomplete.Append(invalid).ToArray();
        }
    }

    public IReadOnlyList<TimingObservation> Complete() => _ascii.Complete();
}

internal sealed class DiagnosticDecoder(CaptureSession session) : ITimingDecoder
{
    public IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet) => [new($"{session.Id:N}:{packet.Sequence}:notice",
        session.Id, packet.Sequence, packet.Source, $"{session.Id:N}:{packet.Sequence}:notice", ObservationKind.Invalid,
        null, null, 0, null, false, "", Encoding.UTF8.GetString(packet.Bytes))];
    public IReadOnlyList<TimingObservation> Complete() => [];
}

public sealed partial class AlgeAsciiDecoder(CaptureSession session, string source, string stream) : ITimingDecoder
{
    private readonly List<byte> _line = [];
    private long _firstSequence;
    private int _firstOffset;
    private long? _latestClock;
    private long _currentClock;
    private int _clockEpoch;
    private bool _oversized;
    public bool HasPendingInput => _oversized || _line.Any(b => b is not (32 or 9));

    [GeneratedRegex(@"^\s*(?<flag>[?mMcCdDinxXtT*])?\s*(?<number>\d{1,5})(?<star>\*)?\s+[cC](?<channel>[0-8])(?<manual>[mM])?\s+(?<time>\d{2}:\d{2}:\d{2}[.:]\d{1,7})(?:\s+\d{2}(?:\s*\d{1,4})?)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ImpulsePattern();

    public IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        var result = new List<TimingObservation>();
        for (var offset = 0; offset < packet.Bytes.Length; offset++)
        {
            var b = packet.Bytes[offset];
            if (b is 13 or 10)
            {
                if (_line.Count > 0 || _oversized)
                {
                    result.Add(Parse(Encoding.ASCII.GetString(_line.ToArray()), _oversized));
                    _line.Clear(); _oversized = false;
                }
                continue;
            }
            if (_line.Count == 0 && !_oversized) { _firstSequence = packet.Sequence; _firstOffset = offset; }
            if (_line.Count < 4096) { _line.Add(b); } else { _oversized = true; }
        }
        return result;
    }

    public IReadOnlyList<TimingObservation> Complete()
    {
        if (!_oversized && _line.All(b => b is 32 or 9)) { _line.Clear(); return []; }
        var observation = Make(ObservationKind.Invalid, "Incomplete device line at end of stream; original bytes retained.");
        _line.Clear(); _oversized = false;
        return [observation];
    }

    private TimingObservation Parse(string line, bool oversized)
    {
        if (oversized) { return Make(ObservationKind.Invalid, "Oversized device line; original bytes retained."); }
        // Framing bytes and line noise (NUL, STX/ETX, XON/XOFF) are not device text. They are left out for reading only:
        // the journal keeps the original bytes and the observation key still points at them.
        if (line.Any(c => char.IsControl(c) && c != '\t')) { line = new string(line.Where(c => !char.IsControl(c) || c == '\t').ToArray()); }
        var text = line.Trim();
        if (TimingTime.TryTimeOfDay(text, out var heartbeat, out _))
        {
            return AdvanceClock(heartbeat) ? Make(ObservationKind.Information, text) with
                { DeviceTicks = session.Options.DeviceDate.ToDateTime(TimeOnly.MinValue).Ticks + _currentClock,
                  ClockId = ClockId() }
                : Make(ObservationKind.Invalid, "Device clock moved backwards/reset: " + text);
        }
        if (text.StartsWith("TIMY:", StringComparison.Ordinal) || text.StartsWith("NSFV", StringComparison.Ordinal)
            || text.StartsWith("PROG:", StringComparison.Ordinal) || text.StartsWith("ALGE", StringComparison.Ordinal)
            || text is "OK" or "?" or "ACK") { return Make(ObservationKind.Information, text); }
        var match = ImpulsePattern().Match(line);
        var timeText = match.Groups["time"].Value;
        if (timeText.Length > 8 && timeText[8] == ':') { timeText = timeText[..8] + "." + timeText[9..]; }
        if (!match.Success || !TimingTime.TryTimeOfDay(timeText, out var tod, out var precision))
        { return Make(ObservationKind.Invalid, "Unrecognized device line: " + text); }
        var flag = match.Groups["flag"].Value;
        var channel = int.Parse(match.Groups["channel"].Value, CultureInfo.InvariantCulture);
        var number = int.Parse(match.Groups["number"].Value, CultureInfo.InvariantCulture);
        var kind = flag is "c" or "C" or "d" or "D" or "i" or "n" ? ObservationKind.DeviceCorrection : ObservationKind.Impulse;
        // A correction line repeats an earlier impulse, often minutes old. It is placed on the device clock but never
        // advances the clock or starts a new clock epoch: that would separate every later finish from its start.
        var clockOk = true;
        if (kind == ObservationKind.DeviceCorrection) { _currentClock = Placed(tod); } else { clockOk = AdvanceClock(tod); }
        var ticks = session.Options.DeviceDate.ToDateTime(TimeOnly.MinValue).Ticks + _currentClock;
        if (!clockOk) { kind = ObservationKind.Invalid; }
        var explicitBib = (flag == "*" || match.Groups["star"].Success) && number > 0 ? (int?)number : null;
        var normalizedChannel = session.Options.Position(channel) ?? channel + 100;
        return Make(kind, clockOk ? text : "Device clock moved backwards/reset. Review clock setup: " + text) with
        {
            Fingerprint = $"{source}:{session.Options.DeviceDate:yyyyMMdd}:{_currentClock}:{flag}:{number}:{channel}:{_clockEpoch}",
            Channel = normalizedChannel, PhysicalChannel = channel, DeviceTicks = ticks, Precision = precision,
            SuggestedBib = explicitBib, Manual = match.Groups["manual"].Success,
            ClockId = ClockId()
        };
    }

    // A synchronized multi-device capture shares one time-of-day basis. A clock reset starts a new epoch that belongs to
    // this device alone: the group's devices are no longer known to agree, and another device's independent reset must
    // not share it. Otherwise each session/stream keeps its own clock context, as before.
    private string ClockId() => session.Options.ClockGroup is { } group && _clockEpoch == 0
        ? $"sync:{group}:0" : $"{session.Id:N}:{source}:{stream}:{_clockEpoch}";

    // The time of day on the running clock, across midnight in either direction.
    private long Placed(long tod)
    {
        if (_latestClock is not { } last) { return tod; }
        var day = last / TimeSpan.TicksPerDay;
        var lastTod = last % TimeSpan.TicksPerDay;
        var placed = tod + day * TimeSpan.TicksPerDay;
        if (lastTod >= TimeSpan.FromHours(18).Ticks && tod < TimeSpan.FromHours(6).Ticks) { placed += TimeSpan.TicksPerDay; }
        else if (day > 0 && lastTod < TimeSpan.FromHours(6).Ticks && tod >= TimeSpan.FromHours(18).Ticks)
        { placed -= TimeSpan.TicksPerDay; } // late packet from just before midnight
        return placed;
    }

    private bool AdvanceClock(long tod)
    {
        _currentClock = Placed(tod);
        if (_latestClock is { } last && last - _currentClock > TimeSpan.FromMinutes(5).Ticks)
        { _clockEpoch++; _latestClock = _currentClock; return false; }
        _latestClock = Math.Max(_latestClock ?? _currentClock, _currentClock);
        return true;
    }

    private TimingObservation Make(ObservationKind kind, string message) => new(
        $"{session.Id:N}:{_firstSequence}:{_firstOffset}", session.Id, _firstSequence, source,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source + ":" + message))),
        kind, null, null, 0, null, false, "", message);
}

public sealed class AlgeResultsDecoder(CaptureSession session) : ITimingDecoder
{
    public IReadOnlyList<TimingObservation> Complete() => [];

    public IReadOnlyList<TimingObservation> Feed(RawTimingPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        try
        {
            using var json = JsonDocument.Parse(packet.Bytes);
            var root = json.RootElement;
            if (root.TryGetProperty("status", out var status) && status.GetInt32() != 0)
            { return [Invalid(packet, "ALGE Results returned an unsuccessful response.")]; }
            var data = root.TryGetProperty("data", out var array) ? array : root;
            var entries = data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [data];
            var result = new List<TimingObservation>();
            for (var index = 0; index < entries.Length; index++)
            {
                try { result.Add(Parse(entries[index], packet, index)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException or KeyNotFoundException)
                { result.Add(Invalid(packet, "Malformed ALGE Results trigger; original JSON retained.", index)); }
            }
            return result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException or KeyNotFoundException)
        { return [Invalid(packet, "Malformed ALGE Results response; original JSON retained.")]; }
    }

    private TimingObservation Parse(JsonElement trigger, RawTimingPacket packet, int index)
    {
        // A push message wraps the trigger: its own type says whether the trigger was added, changed or deleted.
        string? change = null;
        if (trigger.TryGetProperty("dto", out var dto))
        {
            change = trigger.TryGetProperty("type", out var changeType) && changeType.ValueKind == JsonValueKind.String ? changeType.GetString() : null;
            trigger = dto;
        }
        var deleted = change == "ENTITY_DELETE";
        var device = trigger.GetProperty("deviceId").GetString() ?? "";
        var stamp = trigger.GetProperty("timestamp").GetInt64();
        var channelText = trigger.GetProperty("timingChannel").GetString() ?? "";
        if (channelText.Length != 2 || channelText[0] != 'C' || !int.TryParse(channelText.AsSpan(1), out var channel) || channel is < 0 or > 8)
        { return Invalid(packet, "Unknown MT1 channel.", index); }
        var type = trigger.GetProperty("type").GetString();
        var ticks = checked(DateTime.UnixEpoch.Ticks + stamp);
        var timeOffset = trigger.TryGetProperty("timeOffset", out var offset) ? offset.GetInt32() : 0;
        var valid = trigger.TryGetProperty("valid", out var v) && v.ValueKind == JsonValueKind.True;
        var blocked = trigger.TryGetProperty("blocked", out var b) && b.ValueKind == JsonValueKind.True;
        var falling = trigger.TryGetProperty("fallingEdge", out var f) && f.ValueKind == JsonValueKind.True;
        // A deleted trigger is a device correction for review, never the impulse again: with the same fingerprint it would
        // only be a silent duplicate of the original.
        var kind = deleted || type == "ClearTrigger" ? ObservationKind.DeviceCorrection
            : type == "StartNumberTrigger" && valid && !blocked && falling ? ObservationKind.Impulse : ObservationKind.Invalid;
        // Push delivers every channel of a subscribed device. A channel no role uses is kept as raw input and shown in the
        // Settings signal monitor, but does not enter race timing.
        var position = session.Options.Position(channel, device);
        if (position is null && session.Options.Routes is not null && (kind == ObservationKind.Impulse || deleted)) { kind = ObservationKind.Information; }
        int? bib = null;
        if (trigger.TryGetProperty("startNumber", out var number) && number.ValueKind == JsonValueKind.Object
            && number.TryGetProperty("type", out var numberType) && numberType.GetString() == "MANUAL"
            && number.TryGetProperty("startNumber", out var numeric) && numeric.TryGetInt32(out var n) && n > 0) { bib = n; }
        // Semantic fingerprint: JSON whitespace/property order cannot create a second impulse.
        // A changed bib/validity/type remains a separate reviewable observation, never a silent overwrite.
        var fingerprint = $"{device}:{stamp}:{channel}:{type}:{valid}:{blocked}:{falling}:{bib}" + (deleted ? ":deleted" : "");
        // Timing uses the time of day only, like Timy and MT1 serial: the device clock time set in ALGE Results (stored
        // instant plus the device's own time offset) placed on the capture's device date. Races are never held at night.
        // The raw JSON keeps both original values; the calendar date is kept only for reading a race day's device memory.
        var clockTime = new DateTime(checked(ticks + timeOffset * TimeSpan.TicksPerMinute), DateTimeKind.Unspecified);
        var local = session.Options.DeviceDate.ToDateTime(TimeOnly.MinValue).Ticks + clockTime.TimeOfDay.Ticks;
        var clockId = session.Options.ClockGroup is { } group ? $"sync:{group}:0" : "alge-results";
        return new($"{session.Id:N}:{packet.Sequence}:json:{index}", session.Id, packet.Sequence, device,
            "mt1:" + fingerprint, kind, position ?? channel + 10,
            local, 5, bib, false, clockId, $"{device} {channelText} · {TimingTime.FormatTimeOfDay(local)} · {type}"
                + (deleted ? " · deleted on ALGE Results; review the assignment of the original trigger" : ""))
            { PhysicalChannel = channel, CalendarDate = DateOnly.FromDateTime(clockTime) };
    }

    private static TimingObservation Invalid(RawTimingPacket packet, string message, int index = 0) => new(
        $"{packet.SessionId:N}:{packet.Sequence}:invalid:{index}", packet.SessionId, packet.Sequence, packet.Source,
        $"{packet.SessionId:N}:{packet.Sequence}:invalid:{index}", ObservationKind.Invalid, null, null, 0, null, false, "", message);
}
