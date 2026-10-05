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

    // Primary capture and the live B Clock both offer the training simulator. Device reads (timing report) read
    // recorded device input only, so they never list the simulator.
    public static IReadOnlyList<TimingSourceType> Primary { get; } =
        [TimingSourceType.TimyUsb, TimingSourceType.Mt1Serial, TimingSourceType.AlgeResults, TimingSourceType.Simulator, TimingSourceType.ReplayFile];
    public static IReadOnlyList<TimingSourceType> Backup => Primary;
    public static IReadOnlyList<TimingSourceType> DeviceRead { get; } =
        [TimingSourceType.TimyUsb, TimingSourceType.Mt1Serial, TimingSourceType.AlgeResults, TimingSourceType.ReplayFile];

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
        if (Assignments.Select(x => x.Role).Distinct().Count() != Assignments.Count)
        { throw new DomainValidationException("Each timing role can be assigned only once."); }
        foreach (var assignment in Assignments)
        {
            if (!Enum.IsDefined(assignment.Role.Kind) || (assignment.Role.Kind == TimingRoleKind.Intermediate) != (assignment.Role.Index >= 1))
            { throw new DomainValidationException("Unknown timing role."); }
            if (assignment.Channel is < 0 or > 8) { throw new DomainValidationException($"{assignment.Role}: choose channel C0–C8."); }
            assignment.Connection.Validate(assignment.Role.Label);
            if (assignment.Role.IsBackup && !TimingSourceTypes.Backup.Contains(assignment.Connection.Source))
            { throw new DomainValidationException($"{assignment.Role}: choose a supported B Clock source."); }
        }
        var indexes = Intermediates.Select(x => x.Role.Index).ToArray();
        if (!indexes.SequenceEqual(Enumerable.Range(1, indexes.Length)))
        { throw new DomainValidationException("Assign intermediates in course order without gaps (Intermediate 1, 2, …)."); }
        if (Assignments.Any(x => x.Role.IsPrimary) && !HasPrimary)
        { throw new DomainValidationException("Assign both Start and Finish."); }
        if (HasBackupClock && (BackupStart is null || BackupFinish is null))
        { throw new DomainValidationException("Assign both B Clock Start and B Clock Finish, or leave B Clock unconfigured."); }
        if (HasPrimary) { _ = PrimaryCapture(DateOnly.FromDayNumber(0), clockGroup: "validation"); }
        if (HasBackupClock) { _ = BackupCapture(DateOnly.FromDayNumber(0), clockGroup: "validation"); }
        if (HasPrimary && HasBackupClock) { ValidateIndependentBackup(); }
    }

    // One capture source per physical connection. Roles may use any combination of devices and source types.
    // When more than one connection is used, the sessions share an explicit synchronized clock group so elapsed times
    // may span devices. All devices are set to the same clock time.
    public IReadOnlyList<TimingCaptureSource> PrimaryCapture(DateOnly deviceDate, DateTimeOffset? fromUtc = null, string? clockGroup = null)
    {
        var start = Start ?? throw new DomainValidationException("Assign the Start timing role in Settings.");
        var finish = Finish ?? throw new DomainValidationException("Assign the Finish timing role in Settings.");
        var roles = new[] { start, finish }.Concat(Intermediates).ToArray();
        return Capture(roles, "Primary timing", deviceDate, fromUtc, clockGroup,
            legacy: connection => connection.Source != TimingSourceType.AlgeResults || Intermediates.Count == 0);
    }

    public IReadOnlyList<TimingCaptureSource> BackupCapture(DateOnly deviceDate, DateTimeOffset? fromUtc = null, string? clockGroup = null)
    {
        var start = BackupStart ?? throw new DomainValidationException("B Clock is not configured.");
        var finish = BackupFinish ?? throw new DomainValidationException("B Clock is not configured.");
        var sources = Capture([start, finish], "B Clock", deviceDate, fromUtc, clockGroup, legacy: _ => true);
        foreach (var source in sources) { AuxiliaryTimingValidation.Validate(AuxiliaryTimingRole.B, source.Options); }
        return sources;
    }

    private static TimingCaptureSource[] Capture(TimingSourceAssignment[] roles, string group, DateOnly deviceDate,
        DateTimeOffset? fromUtc, string? clockGroup, Func<TimingConnection, bool> legacy)
    {
        var connections = roles.GroupBy(x => x.Connection.ConnectionKey, StringComparer.Ordinal).ToArray();
        if (connections.Select(x => x.First().Connection.Source is TimingSourceType.Simulator or TimingSourceType.ReplayFile).Distinct().Count() > 1)
        { throw new DomainValidationException($"{group}: training sources (Simulator, Replay file) and real devices cannot be mixed."); }
        var timys = connections.Where(x => x.First().Connection.Source == TimingSourceType.TimyUsb).ToArray();
        if (timys.Length > 1 && timys.Any(x => x.First().Connection.UsbId.Trim().Length == 0))
        { throw new DomainValidationException($"{group}: with several Timy USB devices, enter an explicit USB device ID for each."); }
        var ports = connections.Where(x => x.First().Connection.Source == TimingSourceType.Mt1Serial)
            .Select(x => x.First().Connection.Port.Trim().ToUpperInvariant()).ToArray();
        if (ports.Distinct().Count() != ports.Length)
        { throw new DomainValidationException($"{group}: a serial port can be used by one connection only (same baud rate for all its roles)."); }
        if (roles.Where(x => x.Connection.Source == TimingSourceType.AlgeResults).GroupBy(x => (x.Connection.AlgeDeviceId.Trim(), x.Channel)).Any(x => x.Count() > 1))
        { throw new DomainValidationException($"{group}: each ALGE Results device channel can serve one role only."); }
        if (connections.Length == 1)
        {
            var only = connections[0].ToArray();
            var connection = only[0].Connection;
            if (legacy(connection) && only.Length >= 2 && only[0].Role.Kind is TimingRoleKind.Start or TimingRoleKind.BackupStart)
            {
                // A single connection keeps the original session mapping, so existing behavior and files are unchanged.
                var options = Compile(connection, only[0], only[1], deviceDate, fromUtc)
                    with { IntermediateChannels = only.Skip(2).Select(x => x.Channel).ToArray() };
                options.Validate();
                return [new(connection, options, only.Select(x => x.Role).ToArray())];
            }
        }
        var shared = clockGroup ?? Guid.NewGuid().ToString("N");
        var result = new List<TimingCaptureSource>();
        foreach (var connectionRoles in connections)
        {
            var items = connectionRoles.ToArray();
            var connection = items[0].Connection;
            var alge = connection.Source == TimingSourceType.AlgeResults;
            var routes = items.Select(x => new CaptureChannelRoute(x.Channel, Position(x.Role), alge ? x.Connection.AlgeDeviceId.Trim() : null)).ToArray();
            if (routes.Select(x => (x.Channel, x.DeviceId)).Distinct().Count() != routes.Length)
            {
                throw new DomainValidationException($"{group}: {string.Join(", ", items.Select(x => x.Role.Label))} use the same channel on "
                    + $"{connection.SourceLabel} ({connection.Summary}). Use a different channel for each role on one device.");
            }
            var endpoint = alge ? string.Join(";", routes.Select(x => $"{x.DeviceId}/{x.Channel}")) : connection.Endpoint;
            var options = new CaptureOptions(connection.SourceLabel, endpoint, deviceDate,
                Simulation: connection.Source is TimingSourceType.Simulator or TimingSourceType.ReplayFile, Firmware: connection.Firmware,
                FromUtc: alge ? fromUtc : null)
            {
                BaudRate = connection.BaudRate, Routes = routes, ClockGroup = shared
            };
            options.Validate();
            result.Add(new(connection, options, items.Select(x => x.Role).ToArray()));
        }
        return result.ToArray();
    }

    public static int Position(TimingRole role) => role.Kind switch
    {
        TimingRoleKind.Start or TimingRoleKind.BackupStart => 0,
        TimingRoleKind.Finish or TimingRoleKind.BackupFinish => 1,
        _ => role.Index + 1
    };

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

    // Primary timing and B Clock must be physically independent devices.
    private void ValidateIndependentBackup()
    {
        var primary = Assignments.Where(x => x.Role.IsPrimary).Select(x => x.Connection).ToArray();
        var backup = Assignments.Where(x => x.Role.IsBackup).Select(x => x.Connection).ToArray();
        // Simulated B evidence belongs to training runs only; it must never sit beside real A timing in a race file.
        if (backup.Any(x => x.Source == TimingSourceType.Simulator)
            && primary.Any(x => x.Source is not (TimingSourceType.Simulator or TimingSourceType.ReplayFile)))
        { throw new DomainValidationException("The B Clock simulator is for training only. Use it with a Simulator or Replay file primary timing in a test event file."); }
        foreach (var a in primary)
        {
            foreach (var b in backup)
            {
                if (a.Source == TimingSourceType.TimyUsb && b.Source == TimingSourceType.TimyUsb
                    && (a.UsbId.Trim().Length == 0 || b.UsbId.Trim().Length == 0 || string.Equals(a.UsbId.Trim(), b.UsbId.Trim(), StringComparison.OrdinalIgnoreCase)))
                { throw new DomainValidationException("For Timy USB clocks in both primary timing and B Clock, enter separate explicit USB device IDs."); }
                if (a.Source == TimingSourceType.Mt1Serial && b.Source == TimingSourceType.Mt1Serial
                    && string.Equals(a.Port.Trim(), b.Port.Trim(), StringComparison.OrdinalIgnoreCase))
                { throw new DomainValidationException("Primary timing and B Clock cannot use the same serial port."); }
                if (a.Source == TimingSourceType.AlgeResults && b.Source == TimingSourceType.AlgeResults
                    && a.AlgeDeviceId.Trim().Length != 0 && a.AlgeDeviceId.Trim() == b.AlgeDeviceId.Trim())
                { throw new DomainValidationException("Primary timing and B Clock cannot share an ALGE Results device ID."); }
            }
        }
    }

    // Reconstructs role assignments from the saved options of one capture start (one or several device sessions),
    // for files opened without saved preferences.
    public static TimingRoleConfiguration FromCaptureOptions(params CaptureOptions[] sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var assignments = new List<TimingSourceAssignment>();
        foreach (var options in sessions)
        {
            var source = TimingSourceTypes.Parse(options.Device) ?? throw new DomainValidationException("Unknown saved timing source.");
            var connection = new TimingConnection(source)
            {
                UsbId = source == TimingSourceType.TimyUsb && options.Endpoint.StartsWith("Timy USB", StringComparison.Ordinal) ? options.Endpoint[8..].Trim() : "",
                Port = source == TimingSourceType.Mt1Serial ? options.Endpoint : "",
                BaudRate = options.BaudRate, Firmware = options.Firmware
            };
            if (options.Routes is { } routes)
            {
                assignments.AddRange(routes.Select(x => new TimingSourceAssignment(
                    x.Position switch { 0 => TimingRole.Start, 1 => TimingRole.Finish, _ => TimingRole.Intermediate(x.Position - 1) },
                    connection with { AlgeDeviceId = x.DeviceId ?? "" }, x.Channel)));
                continue;
            }
            assignments.Add(new(TimingRole.Start, connection with { AlgeDeviceId = options.StartDeviceId ?? "" }, options.StartChannel));
            assignments.Add(new(TimingRole.Finish, connection with { AlgeDeviceId = options.FinishDeviceId ?? "" }, options.FinishChannel));
            assignments.AddRange(options.IntermediateChannels.Select((channel, i) => new TimingSourceAssignment(TimingRole.Intermediate(i + 1), connection, channel)));
        }
        return new(assignments);
    }
}

// One physical connection of a capture: the existing driver configuration, its compiled session options and served roles.
public sealed record TimingCaptureSource(TimingConnection Connection, CaptureOptions Options, IReadOnlyList<TimingRole> Roles);
