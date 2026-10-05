using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;
using Xunit;

namespace OpenSkiTime.Tests;

public sealed class TimingRoleConfigurationTests
{
    private static readonly DateOnly Date = new(2026, 12, 12);
    private static readonly TimingConnection TimyA = new(TimingSourceType.TimyUsb) { UsbId = "1", Firmware = "1.9" };
    private static readonly TimingConnection Mt1B = new(TimingSourceType.Mt1Serial) { Port = "COM7", BaudRate = 38400 };

    private static TimingRoleConfiguration Example(int intermediates = 2) => new(
        new[] { new TimingSourceAssignment(TimingRole.Start, TimyA, 0), new(TimingRole.Finish, TimyA, 1) }
            .Concat(Enumerable.Range(1, intermediates).Select(i => new TimingSourceAssignment(TimingRole.Intermediate(i), TimyA, i + 1)))
            .Concat([new(TimingRole.BackupStart, Mt1B, 0), new(TimingRole.BackupFinish, Mt1B, 1)]).ToArray())
    { BackupWarnings = new(1, 10, 5) };

    [Fact]
    public void SingleDeviceRolesCompileToTheOriginalSessionMapping()
    {
        var configuration = Example();
        configuration.Validate();
        var a = Assert.Single(configuration.PrimaryCapture(Date)).Options;
        Assert.Equal(TimingSourceTypes.TimyUsbLabel, a.Device);
        Assert.Equal("Timy USB 1", a.Endpoint);
        Assert.Equal((0, 1), (a.StartChannel, a.FinishChannel));
        Assert.Equal([2, 3], a.IntermediateChannels);
        Assert.Equal("1.9", a.Firmware);
        Assert.Null(a.Routes); Assert.Null(a.ClockGroup);
        Assert.False(a.Simulation);
        var b = Assert.Single(configuration.BackupCapture(Date)).Options;
        Assert.Equal((TimingSourceTypes.Mt1SerialLabel, "COM7", 0, 1), (b.Device, b.Endpoint, b.StartChannel, b.FinishChannel));
        Assert.Empty(b.IntermediateChannels);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void ArbitraryIntermediateCountsUseACollectionInCourseOrder(int count)
    {
        var configuration = Example(count);
        configuration.Validate();
        Assert.Equal(Enumerable.Range(1, count), configuration.Intermediates.Select(x => x.Role.Index));
        Assert.Equal(Enumerable.Range(2, count), Assert.Single(configuration.PrimaryCapture(Date)).Options.IntermediateChannels);
        Assert.Equal("Intermediate " + Math.Max(count, 1), TimingRole.Intermediate(Math.Max(count, 1)).Label);
    }

    [Fact]
    public void IntermediateGapsAndDuplicateChannelsOnOneDeviceAreRejected()
    {
        var gap = Example(0).With(new(TimingRole.Intermediate(2), TimyA, 3));
        Assert.Throws<DomainValidationException>(gap.Validate);
        var duplicate = Example(1).With(new(TimingRole.Intermediate(1), TimyA, 1));
        Assert.Throws<DomainValidationException>(duplicate.Validate);
        Assert.Throws<DomainValidationException>(() => TimingRole.Intermediate(0));
    }

    [Fact]
    public void BClockIsOptionalButBothRolesAreRequiredWhenUsed()
    {
        var withoutB = Example().Without(TimingRole.BackupStart).Without(TimingRole.BackupFinish);
        withoutB.Validate();
        Assert.False(withoutB.HasBackupClock);
        Assert.Throws<DomainValidationException>(() => withoutB.BackupCapture(Date));
        Assert.Throws<DomainValidationException>(Example().Without(TimingRole.BackupFinish).Validate);
        Assert.True(Example().HasBackupClock);
    }

    [Fact]
    public void AnyDeviceCanServeAnyRoleWithOneSessionPerConnectionInOneClockGroup()
    {
        var timyStart = TimyA;
        var timyFinish = new TimingConnection(TimingSourceType.TimyUsb) { UsbId = "2" };
        var serial = new TimingConnection(TimingSourceType.Mt1Serial) { Port = "COM3" };
        var configuration = new TimingRoleConfiguration([
            new(TimingRole.Start, timyStart, 0), new(TimingRole.Finish, timyFinish, 0),
            new(TimingRole.Intermediate(1), serial, 0), new(TimingRole.Intermediate(2), timyStart, 4)]);
        configuration.Validate();
        var sources = configuration.PrimaryCapture(Date, clockGroup: "race-1");
        Assert.Equal(3, sources.Count);
        Assert.All(sources, x => Assert.Equal("race-1", x.Options.ClockGroup));
        var start = sources.Single(x => x.Connection.UsbId == "1");
        Assert.Equal([new CaptureChannelRoute(0, 0), new CaptureChannelRoute(4, 3)], start.Options.Routes!);
        Assert.Equal([TimingRole.Start, TimingRole.Intermediate(2)], start.Roles);
        Assert.Equal(1, sources.Single(x => x.Connection.UsbId == "2").Options.Position(0));
        Assert.Equal(2, sources.Single(x => x.Connection.Source == TimingSourceType.Mt1Serial).Options.Position(0));
        Assert.Null(start.Options.Position(1));
    }

    [Fact]
    public void AlgeResultsIntermediatesMayComeFromDifferentClubAccountsAndDevices()
    {
        var clubA = new TimingConnection(TimingSourceType.AlgeResults) { AlgeUsername = "club-a@example.test" };
        var clubB = new TimingConnection(TimingSourceType.AlgeResults) { AlgeUsername = "club-b@example.test" };
        var configuration = new TimingRoleConfiguration([
            new(TimingRole.Start, TimyA, 0), new(TimingRole.Finish, TimyA, 1),
            new(TimingRole.Intermediate(1), clubA with { AlgeDeviceId = "231203037" }, 0),
            new(TimingRole.Intermediate(2), clubB with { AlgeDeviceId = "231203016" }, 1)]);
        configuration.Validate();
        var sources = configuration.PrimaryCapture(Date, clockGroup: "g");
        Assert.Equal(3, sources.Count);
        var first = sources.Single(x => x.Connection.AlgeUsername == "club-a@example.test").Options;
        Assert.Equal([new CaptureChannelRoute(0, 2, "231203037")], first.Routes!);
        Assert.Equal("231203037/0", first.Endpoint);
        Assert.Equal(2, first.Position(0, "231203037"));
        Assert.Null(first.Position(0, "231203016"));
        Assert.Equal(3, sources.Single(x => x.Connection.AlgeUsername == "club-b@example.test").Options.Position(1, "231203016"));
    }

    [Fact]
    public void AlgeResultsRolesOnOneAccountShareOneConnection()
    {
        var account = new TimingConnection(TimingSourceType.AlgeResults) { AlgeUsername = "timekeeper" };
        var configuration = new TimingRoleConfiguration([
            new(TimingRole.Start, account with { AlgeDeviceId = "101" }, 0),
            new(TimingRole.Finish, account with { AlgeDeviceId = "202" }, 0)]);
        configuration.Validate();
        var from = new DateTimeOffset(2026, 12, 12, 9, 0, 0, TimeSpan.Zero);
        var options = Assert.Single(configuration.PrimaryCapture(Date, from)).Options;
        Assert.Equal(("101", "202", "101/0;202/0", from), (options.StartDeviceId, options.FinishDeviceId, options.Endpoint, options.FromUtc));
        var withSplit = configuration.With(new(TimingRole.Intermediate(1), account with { AlgeDeviceId = "303" }, 2));
        withSplit.Validate();
        var routed = Assert.Single(withSplit.PrimaryCapture(Date)).Options;
        Assert.Equal(3, routed.Routes!.Length);
        Assert.Throws<DomainValidationException>(configuration.With(new(TimingRole.Finish, account with { AlgeDeviceId = "101" }, 0)).Validate);
    }

    [Fact]
    public void AmbiguousOrSharedPhysicalConnectionsAreRejected()
    {
        var automatic = new TimingConnection(TimingSourceType.TimyUsb);
        Assert.Throws<DomainValidationException>(new TimingRoleConfiguration([
            new(TimingRole.Start, automatic, 0), new(TimingRole.Finish, TimyA with { UsbId = "2" }, 1)]).Validate);
        Assert.Throws<DomainValidationException>(new TimingRoleConfiguration([
            new(TimingRole.Start, Mt1B, 0), new(TimingRole.Finish, Mt1B with { BaudRate = 9600 }, 1)]).Validate);
        Assert.Throws<DomainValidationException>(new TimingRoleConfiguration([
            new(TimingRole.Start, new(TimingSourceType.Simulator), 0), new(TimingRole.Finish, TimyA, 1)]).Validate);
    }

    [Fact]
    public void PrimaryAndBClockMustBeIndependentPhysicalDevices()
    {
        var timyB = new TimingConnection(TimingSourceType.TimyUsb) { UsbId = "1" };
        Assert.Throws<DomainValidationException>(Example()
            .With(new(TimingRole.BackupStart, timyB, 0)).With(new(TimingRole.BackupFinish, timyB, 1)).Validate);
        Assert.Throws<DomainValidationException>(new TimingRoleConfiguration([
            new(TimingRole.Start, Mt1B, 0), new(TimingRole.Finish, Mt1B, 1),
            new(TimingRole.BackupStart, Mt1B, 2), new(TimingRole.BackupFinish, Mt1B, 3)]).Validate);
        var splitB = Example().With(new(TimingRole.BackupFinish, Mt1B with { Port = "COM8" }, 1));
        splitB.Validate();
        Assert.Equal(2, splitB.BackupCapture(Date).Count);
    }

    [Fact]
    public void ExistingSourceTypesRemainAvailableWithUnchangedLabels()
    {
        Assert.Equal(["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Simulator", "Replay file"],
            TimingSourceTypes.Primary.Select(TimingSourceTypes.Label));
        Assert.Equal(["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Simulator", "Replay file"],
            TimingSourceTypes.Backup.Select(TimingSourceTypes.Label));
        Assert.Equal(["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Replay file"],
            TimingSourceTypes.DeviceRead.Select(TimingSourceTypes.Label));
        foreach (var source in TimingSourceTypes.Primary) { Assert.Equal(source, TimingSourceTypes.Parse(TimingSourceTypes.Label(source))); }
    }

    [Fact]
    public void BClockSimulatorIsATrainingSourceBesideTrainingPrimaryTimingOnly()
    {
        var simulator = new TimingConnection(TimingSourceType.Simulator);
        // Real A timing with a simulated B Clock would mix training evidence into a race file.
        var realA = Example().With(new(TimingRole.BackupStart, simulator, 0)).With(new(TimingRole.BackupFinish, simulator, 1));
        Assert.Contains("training only", Assert.Throws<DomainValidationException>(realA.Validate).Message, StringComparison.Ordinal);

        foreach (var primary in new[] { simulator, new TimingConnection(TimingSourceType.ReplayFile) { ReplayPath = "a.txt" } })
        {
            var training = new TimingRoleConfiguration([new(TimingRole.Start, primary, 0), new(TimingRole.Finish, primary, 1),
                new(TimingRole.BackupStart, simulator, 3), new(TimingRole.BackupFinish, simulator, 4)]);
            training.Validate();
            var b = Assert.Single(training.BackupCapture(Date)).Options;
            Assert.Equal(TimingSourceTypes.SimulatorLabel, b.Device);
            Assert.True(b.Simulation);
            Assert.Equal((3, 4), (b.Channel(0), b.Channel(1)));
            AuxiliaryTimingValidation.Validate(AuxiliaryTimingRole.B, b);
        }
        // B Clock alone (primary not yet assigned) may be saved with the simulator.
        new TimingRoleConfiguration([new(TimingRole.BackupStart, simulator, 0), new(TimingRole.BackupFinish, simulator, 1)]).Validate();
    }

    [Fact]
    public void SimulatorAndReplayRemainTrainingSources()
    {
        foreach (var connection in new[] { new TimingConnection(TimingSourceType.Simulator), new TimingConnection(TimingSourceType.ReplayFile) { ReplayPath = "x.txt" } })
        {
            var options = Assert.Single(new TimingRoleConfiguration([new(TimingRole.Start, connection, 0), new(TimingRole.Finish, connection, 1)]).PrimaryCapture(Date)).Options;
            Assert.True(options.Simulation);
            Assert.Equal(connection.SourceLabel, options.Device);
        }
    }

    [Fact]
    public void SavedCaptureOptionsRoundTripIntoRoleAssignments()
    {
        var original = Assert.Single(Example().PrimaryCapture(Date)).Options;
        var restored = TimingRoleConfiguration.FromCaptureOptions(original);
        restored.Validate();
        var again = Assert.Single(restored.PrimaryCapture(Date)).Options;
        Assert.Equal(original with { IntermediateChannels = [] }, again with { IntermediateChannels = [] });
        Assert.Equal(original.IntermediateChannels, again.IntermediateChannels);

        var multi = new TimingRoleConfiguration([new(TimingRole.Start, TimyA, 0), new(TimingRole.Finish, TimyA with { UsbId = "2" }, 0)]);
        var sessions = multi.PrimaryCapture(Date, clockGroup: "g").Select(x => x.Options).ToArray();
        var rebuilt = TimingRoleConfiguration.FromCaptureOptions(sessions);
        rebuilt.Validate();
        Assert.Equal(("1", "2"), (rebuilt.Start!.Connection.UsbId, rebuilt.Finish!.Connection.UsbId));
    }

    [Fact]
    public void BackupWarningThresholdsAreValidated()
    {
        Assert.Throws<DomainValidationException>((Example() with { BackupWarnings = new(0, 10, 5) }).Validate);
        Assert.Throws<DomainValidationException>((Example() with { BackupWarnings = new(1, 10, 0) }).Validate);
    }

    [Fact]
    public void BClockStatusReportsSimpleHealthWithoutFalseFailures()
    {
        Assert.Equal(BackupClockHealth.NotConfigured, BackupClockStatus.Evaluate(false, false, null, null).Health);
        Assert.False(BackupClockStatus.Evaluate(false, false, null, null).IsProblem);
        Assert.Equal(BackupClockHealth.DeviceUnavailable, BackupClockStatus.Evaluate(true, false, null, null).Health);
        Assert.Equal(BackupClockHealth.DeviceUnavailable, BackupClockStatus.Evaluate(true, true, "AUXILIARY INPUT NOT SAVED", null).Health);
        Assert.Equal(BackupClockHealth.Ok, BackupClockStatus.Evaluate(true, true, null, new([], [])).Health);
        var waiting = new BackupComparisonRow(1, "Start", 10, null, null, "");
        Assert.Equal(BackupClockHealth.WaitingForB, BackupClockStatus.Evaluate(true, true, null, new([waiting], [])).Health);
        Assert.Equal(BackupClockHealth.Ok, BackupClockStatus.Evaluate(true, true, null, new([waiting with { Monitored = false }], [])).Health);
        Assert.Equal(BackupClockHealth.MissingSignal, BackupClockStatus.Evaluate(true, true, null,
            new([waiting with { Warning = "B signal missing" }], ["x"])).Health);
        Assert.Equal(BackupClockHealth.TimeDifferenceWarning, BackupClockStatus.Evaluate(true, true, null,
            new([new(1, "Finish", 10, 20, 10, "A/B time difference")], ["x"])).Health);
    }

    [Theory]
    [InlineData("C0", 1, false)]
    [InlineData("C0", 2, true)]
    [InlineData("C1", 10, false)]
    [InlineData("C1", 11, true)]
    public void ConfiguredThresholdsDriveStartAndFinishWarnings(string channel, int differenceMilliseconds, bool warns)
    {
        var list = TimingRulesTests.List(1); var capture = TimingRulesTests.Session(list);
        var a = new AlgeAsciiDecoder(capture, "A", "1").Feed(TimingRulesTests.Packet(capture, 1, $"0001* {channel} 12:00:00.0000\r"));
        var audit = new TimingAudit(1, list.Id, TimingRulesTests.At, "Operator", "Source", new(DecisionKind.Assignment, a[0].Key),
            new(DecisionKind.Assignment, a[0].Key, Bib: 1));
        var empty = TimingEngine.Replay(list, [], [], 0, 1);
        var timed = TimingEngine.Replay(list, a, [audit], 0, 1);
        var warnings = new BackupClockWarnings(1, 10, 5);
        var policy = new BackupComparisonPolicy(warnings.StartWarningMilliseconds * TimeSpan.TicksPerMillisecond,
            warnings.FinishWarningMilliseconds * TimeSpan.TicksPerMillisecond, MissingGraceTicks: warnings.MissingSignalWaitSeconds * TimeSpan.TicksPerSecond);
        var monitor = new BackupMonitor(); var session = Guid.NewGuid();
        monitor.Update(session, empty, [], TimingRulesTests.At, policy);
        var b = a[0] with { Key = "B", DeviceTicks = a[0].DeviceTicks + differenceMilliseconds * TimeSpan.TicksPerMillisecond };
        var comparison = monitor.Update(session, timed, [b], TimingRulesTests.At, policy);
        Assert.Equal(warns, comparison.Warnings.Count > 0);
        Assert.Equal(warns ? BackupClockHealth.TimeDifferenceWarning : BackupClockHealth.Ok,
            BackupClockStatus.Evaluate(true, true, null, comparison).Health);
        // B comparison never changes the authoritative A observation.
        Assert.Equal(a[0].DeviceTicks, timed.Observations.Single().Observation.DeviceTicks);
    }

    [Fact]
    public void MissingBBecomesMissingSignalOnlyAfterConfiguredWait()
    {
        var list = TimingRulesTests.List(1); var capture = TimingRulesTests.Session(list);
        var a = new AlgeAsciiDecoder(capture, "A", "1").Feed(TimingRulesTests.Packet(capture, 1, "0001* C1 12:00:00.0000\r"));
        var timed = TimingEngine.Replay(list, a, [new(1, list.Id, TimingRulesTests.At, "Operator", "Source",
            new(DecisionKind.Assignment, a[0].Key), new(DecisionKind.Assignment, a[0].Key, Bib: 1))], 0, 1);
        var policy = new BackupComparisonPolicy(MissingGraceTicks: 3 * TimeSpan.TicksPerSecond);
        var monitor = new BackupMonitor(); var session = Guid.NewGuid();
        monitor.Update(session, TimingEngine.Replay(list, [], [], 0, 1), [], TimingRulesTests.At, policy);
        Assert.Equal(BackupClockHealth.WaitingForB, BackupClockStatus.Evaluate(true, true, null,
            monitor.Update(session, timed, [], TimingRulesTests.At, policy)).Health);
        Assert.Equal(BackupClockHealth.WaitingForB, BackupClockStatus.Evaluate(true, true, null,
            monitor.Update(session, timed, [], TimingRulesTests.At.AddSeconds(2.9), policy)).Health);
        Assert.Equal(BackupClockHealth.MissingSignal, BackupClockStatus.Evaluate(true, true, null,
            monitor.Update(session, timed, [], TimingRulesTests.At.AddSeconds(3), policy)).Health);
    }
}
