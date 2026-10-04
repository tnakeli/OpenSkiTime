using System.Text.Json.Serialization;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Application;

// Persistent race timing is configured per timing role: Role -> Source -> Connection -> Channel.
// B Clock roles are supporting evidence only; they never feed authoritative A timing.
public enum TimingRoleKind { Start, Finish, Intermediate, BackupStart, BackupFinish }

public readonly record struct TimingRole(TimingRoleKind Kind, int Index = 0)
{
    public static TimingRole Start => new(TimingRoleKind.Start);
    public static TimingRole Finish => new(TimingRoleKind.Finish);
    public static TimingRole BackupStart => new(TimingRoleKind.BackupStart);
    public static TimingRole BackupFinish => new(TimingRoleKind.BackupFinish);
    public static TimingRole Intermediate(int index) => index >= 1
        ? new(TimingRoleKind.Intermediate, index) : throw new DomainValidationException("Intermediate numbers start at 1.");
    [JsonIgnore] public bool IsBackup => Kind is TimingRoleKind.BackupStart or TimingRoleKind.BackupFinish;
    [JsonIgnore] public bool IsPrimary => !IsBackup;
    [JsonIgnore] public string Label => Kind switch
    {
        TimingRoleKind.Start => "Start",
        TimingRoleKind.Finish => "Finish",
        TimingRoleKind.Intermediate => $"Intermediate {Index}",
        TimingRoleKind.BackupStart => "B Clock Start",
        _ => "B Clock Finish"
    };
    public override string ToString() => Label;
}

public enum TimingSourceType { TimyUsb, Mt1Serial, AlgeResults, Simulator, ReplayFile }

public static class TimingSourceTypes
{
    public const string TimyUsbLabel = "Timy 2/3 · USB";
    public const string Mt1SerialLabel = "MT1 · USB / serial";
    public const string AlgeResultsLabel = "MT1 · ALGE Results";
    public const string SimulatorLabel = "Simulator";
    public const string ReplayFileLabel = "Replay file";

    // Labels are also stored as CaptureOptions.Device in existing series files. Keep them unchanged.
    public static string Label(TimingSourceType source) => source switch
    {
        TimingSourceType.TimyUsb => TimyUsbLabel,
        TimingSourceType.Mt1Serial => Mt1SerialLabel,
        TimingSourceType.AlgeResults => AlgeResultsLabel,
        TimingSourceType.Simulator => SimulatorLabel,
        TimingSourceType.ReplayFile => ReplayFileLabel,
        _ => throw new DomainValidationException("Unknown timing source.")
    };

    public static TimingSourceType? Parse(string? label) => label switch
    {
        TimyUsbLabel => TimingSourceType.TimyUsb,
        Mt1SerialLabel => TimingSourceType.Mt1Serial,
        AlgeResultsLabel => TimingSourceType.AlgeResults,
        SimulatorLabel => TimingSourceType.Simulator,
        ReplayFileLabel => TimingSourceType.ReplayFile,
        _ => null
    };

    // Primary capture keeps its training simulator. B Clock and device reads use real/replayed device input only.
    public static IReadOnlyList<TimingSourceType> Primary { get; } =
        [TimingSourceType.TimyUsb, TimingSourceType.Mt1Serial, TimingSourceType.AlgeResults, TimingSourceType.Simulator, TimingSourceType.ReplayFile];
    public static IReadOnlyList<TimingSourceType> Backup { get; } =
        [TimingSourceType.TimyUsb, TimingSourceType.Mt1Serial, TimingSourceType.AlgeResults, TimingSourceType.ReplayFile];
    public static IReadOnlyList<TimingSourceType> DeviceRead => Backup;

    public static bool SupportsIntermediates(TimingSourceType source) => source != TimingSourceType.AlgeResults;
}

// Source-specific connection details. Only the fields relevant to Source are used; secrets are never stored here.
public sealed record TimingConnection(TimingSourceType Source)
{
    public string UsbId { get; init; } = "";
    public string Port { get; init; } = "";
    public int BaudRate { get; init; } = 38400;
    public string Firmware { get; init; } = "Not queried";
    public string AlgeDeviceId { get; init; } = "";
    public string AlgeUsername { get; init; } = "";
    public string ReplayPath { get; init; } = "";

    [JsonIgnore] public string SourceLabel => TimingSourceTypes.Label(Source);

    // Identity of the physical connection/stream that one capture source reads. ALGE Results device IDs are per role
    // and polled through one account connection, so they are not part of this key.
    [JsonIgnore] public string ConnectionKey => Source switch
    {
        TimingSourceType.TimyUsb => "usb:" + UsbId.Trim().ToUpperInvariant(),
        TimingSourceType.Mt1Serial => "serial:" + Port.Trim().ToUpperInvariant() + ":" + BaudRate,
        TimingSourceType.AlgeResults => "alge:" + AlgeUsername.Trim().ToUpperInvariant(),
        TimingSourceType.Simulator => "simulator",
        _ => "replay:" + ReplayPath.Trim()
    };

    [JsonIgnore] public string Endpoint => Source switch
    {
        TimingSourceType.TimyUsb => "Timy USB " + UsbId.Trim(),
        TimingSourceType.Mt1Serial => Port.Trim(),
        TimingSourceType.Simulator => "Simulator",
        TimingSourceType.ReplayFile => "Replay",
        _ => throw new InvalidOperationException("ALGE Results endpoints depend on the mapped device IDs.")
    };

    [JsonIgnore] public string Summary => Source switch
    {
        TimingSourceType.TimyUsb => UsbId.Trim().Length == 0 ? "first connected Timy" : "USB ID " + UsbId.Trim(),
        TimingSourceType.Mt1Serial => Port.Trim().Length == 0 ? "no COM port" : $"{Port.Trim()} · {BaudRate}",
        TimingSourceType.AlgeResults => AlgeDeviceId.Trim().Length == 0 ? "no device ID" : "device " + AlgeDeviceId.Trim(),
        TimingSourceType.Simulator => "training",
        _ => ReplayPath.Trim().Length == 0 ? "no file" : Path.GetFileName(ReplayPath.Trim())
    };

    public void Validate(string role)
    {
        if (!Enum.IsDefined(Source)) { throw new DomainValidationException($"{role}: choose a timing source."); }
        if (Source == TimingSourceType.Mt1Serial && (BaudRate is < 1200 or > 115200))
        { throw new DomainValidationException($"{role}: unsupported serial baud rate."); }
        if (Source == TimingSourceType.AlgeResults && AlgeDeviceId.Trim().Length != 0 && !AlgeDeviceId.Trim().All(char.IsAsciiDigit))
        { throw new DomainValidationException($"{role}: the ALGE Results device ID must contain digits only."); }
    }
}

public sealed record TimingSourceAssignment(TimingRole Role, TimingConnection Connection, int Channel)
{
    [JsonIgnore] public string ChannelLabel => "C" + Channel;
}

// Operator assistance thresholds for live B comparison. They never change timing or FIS rules.
public sealed record BackupClockWarnings(int StartWarningMilliseconds = 1, int FinishWarningMilliseconds = 10,
    int MissingSignalWaitSeconds = 5)
{
    public void Validate()
    {
        if (StartWarningMilliseconds is < 1 or > 10000 || FinishWarningMilliseconds is < 1 or > 10000)
        { throw new DomainValidationException("B Clock warning thresholds must be 1–10000 ms."); }
        if (MissingSignalWaitSeconds is < 1 or > 300)
        { throw new DomainValidationException("Missing signal wait must be 1–300 seconds."); }
    }
}

public sealed record TimingRoleConfiguration(IReadOnlyList<TimingSourceAssignment> Assignments)
{
    public BackupClockWarnings BackupWarnings { get; init; } = new();
    // Explicit local-clock UTC offset (minutes) for comparing a UTC B Clock (ALGE Results) with a local A clock, or vice versa.
    // Recorded with each B capture; original timestamps are never rewritten.
    public int? BackupClockUtcOffsetMinutes { get; init; }

    public static TimingRoleConfiguration Empty { get; } = new([]);

    public TimingSourceAssignment? Find(TimingRole role) => Assignments.FirstOrDefault(x => x.Role == role);
    [JsonIgnore] public TimingSourceAssignment? Start => Find(TimingRole.Start);
    [JsonIgnore] public TimingSourceAssignment? Finish => Find(TimingRole.Finish);
    [JsonIgnore] public TimingSourceAssignment? BackupStart => Find(TimingRole.BackupStart);
    [JsonIgnore] public TimingSourceAssignment? BackupFinish => Find(TimingRole.BackupFinish);
    [JsonIgnore] public IReadOnlyList<TimingSourceAssignment> Intermediates => Assignments
        .Where(x => x.Role.Kind == TimingRoleKind.Intermediate).OrderBy(x => x.Role.Index).ToArray();
    [JsonIgnore] public bool HasPrimary => Start is not null && Finish is not null;
    [JsonIgnore] public bool HasBackupClock => BackupStart is not null || BackupFinish is not null;

    public TimingRoleConfiguration With(TimingSourceAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        return this with { Assignments = Assignments.Where(x => x.Role != assignment.Role).Append(assignment).ToArray() };
    }

    public TimingRoleConfiguration Without(TimingRole role) => this with { Assignments = Assignments.Where(x => x.Role != role).ToArray() };

    public void Validate()
    {
        if (Assignments is null) { throw new DomainValidationException("Timing role assignments are missing."); }
        BackupWarnings.Validate();
        if (BackupClockUtcOffsetMinutes is < -840 or > 840)
        { throw new DomainValidationException("B Clock UTC offset must be between -840 and 840 minutes, or blank."); }
        if (Assignments.Select(x => x.Role).Distinct().Count() != Assignments.Count)
        { throw new DomainValidationException("Each timing role can be assigned only once."); }
        foreach (var assignment in Assignments)
        {
            if (!Enum.IsDefined(assignment.Role.Kind) || (assignment.Role.Kind == TimingRoleKind.Intermediate) != (assignment.Role.Index >= 1))
            { throw new DomainValidationException("Unknown timing role."); }
            if (assignment.Channel is < 0 or > 8) { throw new DomainValidationException($"{assignment.Role}: choose channel C0–C8."); }
            assignment.Connection.Validate(assignment.Role.Label);
            if (assignment.Role.IsBackup && !TimingSourceTypes.Backup.Contains(assignment.Connection.Source))
            { throw new DomainValidationException($"{assignment.Role}: the simulator cannot be a B Clock."); }
        }
        var indexes = Intermediates.Select(x => x.Role.Index).ToArray();
        if (!indexes.SequenceEqual(Enumerable.Range(1, indexes.Length)))
        { throw new DomainValidationException("Assign intermediates in course order without gaps (Intermediate 1, 2, …)."); }
        if (Assignments.Any(x => x.Role.IsPrimary) && !HasPrimary)
        { throw new DomainValidationException("Assign both Start and Finish."); }
        if (HasBackupClock && (BackupStart is null || BackupFinish is null))
        { throw new DomainValidationException("Assign both B Clock Start and B Clock Finish, or leave B Clock unconfigured."); }
        if (HasPrimary) { _ = PrimaryCaptureOptions(DateOnly.FromDayNumber(0)); }
        if (HasBackupClock) { _ = BackupCaptureOptions(DateOnly.FromDayNumber(0)); }
        if (HasPrimary && HasBackupClock) { ValidateIndependentBackup(); }
    }

    // A single capture source reads all primary roles. Roles on different physical connections are a technical restriction
    // of the current A capture pipeline (one ordered journal and clock context per capture), so they are rejected explicitly.
    public CaptureOptions PrimaryCaptureOptions(DateOnly deviceDate, DateTimeOffset? fromUtc = null)
    {
        var start = Start ?? throw new DomainValidationException("Assign the Start timing role in Settings.");
        var finish = Finish ?? throw new DomainValidationException("Assign the Finish timing role in Settings.");
        var roles = new[] { start, finish }.Concat(Intermediates).ToArray();
        var connection = SharedConnection(roles, "Primary timing");
        if (connection.Source == TimingSourceType.AlgeResults && Intermediates.Count > 0)
        { throw new DomainValidationException("ALGE Results supports start/finish only. Use USB/serial for intermediate capture."); }
        var options = Compile(connection, start, finish, deviceDate, fromUtc)
            with { IntermediateChannels = Intermediates.Select(x => x.Channel).ToArray() };
        options.Validate();
        return options;
    }

    public CaptureOptions BackupCaptureOptions(DateOnly deviceDate, DateTimeOffset? fromUtc = null)
    {
        var start = BackupStart ?? throw new DomainValidationException("B Clock is not configured.");
        var finish = BackupFinish ?? throw new DomainValidationException("B Clock is not configured.");
        var connection = SharedConnection([start, finish], "B Clock");
        var options = Compile(connection, start, finish, deviceDate, fromUtc) with { ComparisonUtcOffsetMinutes = BackupClockUtcOffsetMinutes };
        AuxiliaryTimingValidation.Validate(AuxiliaryTimingRole.B, options);
        return options;
    }

    private static TimingConnection SharedConnection(TimingSourceAssignment[] roles, string group)
    {
        var first = roles[0].Connection;
        foreach (var other in roles.Skip(1))
        {
            if (other.Connection.Source != first.Source || other.Connection.ConnectionKey != first.ConnectionKey
                || (first.Source != TimingSourceType.AlgeResults && other.Connection with { AlgeDeviceId = "" } != first with { AlgeDeviceId = "" }))
            {
                throw new DomainValidationException($"{group}: {roles[0].Role} and {other.Role} use different connections. "
                    + "All roles in this group must currently share one device connection (use different channels). "
                    + "ALGE Results may use different device IDs on one account.");
            }
        }
        return first;
    }

    private static CaptureOptions Compile(TimingConnection connection, TimingSourceAssignment start, TimingSourceAssignment finish,
        DateOnly deviceDate, DateTimeOffset? fromUtc)
    {
        var alge = connection.Source == TimingSourceType.AlgeResults;
        var startDevice = alge ? start.Connection.AlgeDeviceId.Trim() : null;
        var finishDevice = alge ? finish.Connection.AlgeDeviceId.Trim() : null;
        var endpoint = alge ? $"{startDevice}/{start.Channel};{finishDevice}/{finish.Channel}" : connection.Endpoint;
        var options = new CaptureOptions(connection.SourceLabel, endpoint, deviceDate, start.Channel, finish.Channel,
            connection.Source is TimingSourceType.Simulator or TimingSourceType.ReplayFile, connection.Firmware,
            startDevice, finishDevice, alge ? fromUtc : null)
        { BaudRate = connection.BaudRate };
        if (start.Channel == finish.Channel && (!alge || startDevice == finishDevice))
        { throw new DomainValidationException($"{start.Role} and {finish.Role} must use different channels on the same device."); }
        return options;
    }

    private void ValidateIndependentBackup()
    {
        var a = Start!.Connection; var b = BackupStart!.Connection;
        if (a.Source == TimingSourceType.TimyUsb && b.Source == TimingSourceType.TimyUsb
            && (a.UsbId.Trim().Length == 0 || b.UsbId.Trim().Length == 0 || string.Equals(a.UsbId.Trim(), b.UsbId.Trim(), StringComparison.OrdinalIgnoreCase)))
        { throw new DomainValidationException("For two Timy USB clocks, enter separate explicit USB device IDs for primary timing and B Clock."); }
        if (a.Source == TimingSourceType.Mt1Serial && b.Source == TimingSourceType.Mt1Serial
            && string.Equals(a.Port.Trim(), b.Port.Trim(), StringComparison.OrdinalIgnoreCase))
        { throw new DomainValidationException("Primary timing and B Clock cannot use the same serial port."); }
        if (a.Source == TimingSourceType.AlgeResults && b.Source == TimingSourceType.AlgeResults)
        {
            var primary = new[] { Start, Finish }.Select(x => x!.Connection.AlgeDeviceId.Trim()).Where(x => x.Length != 0);
            var backup = new[] { BackupStart, BackupFinish }.Select(x => x!.Connection.AlgeDeviceId.Trim()).Where(x => x.Length != 0);
            if (primary.Intersect(backup, StringComparer.Ordinal).Any())
            { throw new DomainValidationException("Primary timing and B Clock cannot share an ALGE Results device ID."); }
        }
    }

    // Reconstructs role assignments from a capture session's saved options, for files opened without saved preferences.
    public static TimingRoleConfiguration FromCaptureOptions(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var source = TimingSourceTypes.Parse(options.Device) ?? throw new DomainValidationException("Unknown saved timing source.");
        var connection = new TimingConnection(source)
        {
            UsbId = source == TimingSourceType.TimyUsb && options.Endpoint.StartsWith("Timy USB", StringComparison.Ordinal) ? options.Endpoint[8..].Trim() : "",
            Port = source == TimingSourceType.Mt1Serial ? options.Endpoint : "",
            BaudRate = options.BaudRate, Firmware = options.Firmware
        };
        var assignments = new List<TimingSourceAssignment>
        {
            new(TimingRole.Start, connection with { AlgeDeviceId = options.StartDeviceId ?? "" }, options.StartChannel),
            new(TimingRole.Finish, connection with { AlgeDeviceId = options.FinishDeviceId ?? "" }, options.FinishChannel)
        };
        assignments.AddRange(options.IntermediateChannels.Select((channel, i) => new TimingSourceAssignment(TimingRole.Intermediate(i + 1), connection, channel)));
        return new(assignments);
    }
}
