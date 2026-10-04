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
    public void RolesCompileToExistingCaptureOptionsForSharedPrimaryAndSeparateBDevice()
    {
        var configuration = Example();
        configuration.Validate();
        var a = configuration.PrimaryCaptureOptions(Date);
        Assert.Equal(TimingSourceTypes.TimyUsbLabel, a.Device);
        Assert.Equal("Timy USB 1", a.Endpoint);
        Assert.Equal((0, 1), (a.StartChannel, a.FinishChannel));
        Assert.Equal([2, 3], a.IntermediateChannels);
        Assert.Equal("1.9", a.Firmware);
        Assert.False(a.Simulation);
        var b = configuration.BackupCaptureOptions(Date);
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
        Assert.Equal(Enumerable.Range(2, count), configuration.PrimaryCaptureOptions(Date).IntermediateChannels);
        Assert.Equal("Intermediate " + Math.Max(count, 1), TimingRole.Intermediate(Math.Max(count, 1)).Label);
    }

    [Fact]
    public void IntermediateGapsAndDuplicateChannelsAreRejected()
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
        Assert.Throws<DomainValidationException>(() => withoutB.BackupCaptureOptions(Date));
        Assert.Throws<DomainValidationException>(Example().Without(TimingRole.BackupFinish).Validate);
        Assert.True(Example().HasBackupClock);
    }

    [Fact]
    public void PrimaryRolesOnDifferentConnectionsAreAnExplicitRestriction()
    {
        var other = TimyA with { UsbId = "2" };
        var split = Example().With(new(TimingRole.Finish, other, 1));
        var error = Assert.Throws<DomainValidationException>(split.Validate);
        Assert.Contains("different connections", error.Message, StringComparison.Ordinal);
        Assert.Throws<DomainValidationException>(Example().With(new(TimingRole.BackupFinish, Mt1B with { Port = "COM8" }, 1)).Validate);
    }

    [Fact]
    public void AlgeResultsRolesMayUseDifferentDevicesOnOneAccountButNoIntermediates()
    {
        var account = new TimingConnection(TimingSourceType.AlgeResults) { AlgeUsername = "timekeeper" };
        var configuration = new TimingRoleConfiguration([
            new(TimingRole.Start, account with { AlgeDeviceId = "101" }, 0),
            new(TimingRole.Finish, account with { AlgeDeviceId = "202" }, 0)]);
        configuration.Validate();
        var from = new DateTimeOffset(2026, 12, 12, 9, 0, 0, TimeSpan.Zero);
        var options = configuration.PrimaryCaptureOptions(Date, from);
        Assert.Equal(("101", "202", "101/0;202/0", from), (options.StartDeviceId, options.FinishDeviceId, options.Endpoint, options.FromUtc));
        Assert.Throws<DomainValidationException>(configuration.With(new(TimingRole.Intermediate(1), account with { AlgeDeviceId = "101" }, 2)).Validate);
        Assert.Throws<DomainValidationException>(configuration.With(new(TimingRole.Finish, account with { AlgeDeviceId = "101" }, 0)).Validate);
    }

    [Fact]
    public void PrimaryAndBClockMustBeIndependentPhysicalDevices()
    {
        var timyB = new TimingConnection(TimingSourceType.TimyUsb) { UsbId = "1" };
        Assert.Throws<DomainValidationException>(Example()
            .With(new(TimingRole.BackupStart, timyB, 0)).With(new(TimingRole.BackupFinish, timyB, 1)).Validate);
        var anyTimy = TimyA with { UsbId = "" };
        var automatic = Example().With(new(TimingRole.Start, anyTimy, 0)).With(new(TimingRole.Finish, anyTimy, 1))
            .Without(TimingRole.Intermediate(1)).Without(TimingRole.Intermediate(2))
            .With(new(TimingRole.BackupStart, timyB with { UsbId = "3" }, 0)).With(new(TimingRole.BackupFinish, timyB with { UsbId = "3" }, 1));
        Assert.Throws<DomainValidationException>(automatic.Validate);
        var serialA = Mt1B;
        Assert.Throws<DomainValidationException>(new TimingRoleConfiguration([
            new(TimingRole.Start, serialA, 0), new(TimingRole.Finish, serialA, 1),
            new(TimingRole.BackupStart, serialA, 2), new(TimingRole.BackupFinish, serialA, 3)]).Validate);
    }

    [Fact]
    public void ExistingSourceTypesRemainAvailableWithUnchangedLabels()
    {
        Assert.Equal(["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Simulator", "Replay file"],
            TimingSourceTypes.Primary.Select(TimingSourceTypes.Label));
        Assert.Equal(["Timy 2/3 · USB", "MT1 · USB / serial", "MT1 · ALGE Results", "Replay file"],
            TimingSourceTypes.Backup.Select(TimingSourceTypes.Label));
        foreach (var source in TimingSourceTypes.Primary) { Assert.Equal(source, TimingSourceTypes.Parse(TimingSourceTypes.Label(source))); }
        var simulatorB = Example().With(new(TimingRole.BackupStart, new(TimingSourceType.Simulator), 0))
            .With(new(TimingRole.BackupFinish, new(TimingSourceType.Simulator), 1));
        Assert.Throws<DomainValidationException>(simulatorB.Validate);
    }

    [Fact]
    public void SimulatorAndReplayRemainTrainingSources()
    {
        foreach (var connection in new[] { new TimingConnection(TimingSourceType.Simulator), new TimingConnection(TimingSourceType.ReplayFile) { ReplayPath = "x.txt" } })
        {
            var options = new TimingRoleConfiguration([new(TimingRole.Start, connection, 0), new(TimingRole.Finish, connection, 1)]).PrimaryCaptureOptions(Date);
            Assert.True(options.Simulation);
            Assert.Equal(connection.SourceLabel, options.Device);
        }
    }

    [Fact]
    public void SavedCaptureOptionsRoundTripIntoRoleAssignments()
    {
        var original = Example().PrimaryCaptureOptions(Date);
        var restored = TimingRoleConfiguration.FromCaptureOptions(original);
        restored.Validate();
        Assert.Equal(original, restored.PrimaryCaptureOptions(Date) with { IntermediateChannels = original.IntermediateChannels });
        Assert.Equal(original.IntermediateChannels, restored.PrimaryCaptureOptions(Date).IntermediateChannels);
    }

    [Fact]
    public void BClockOffsetIsRecordedOnBCaptureOnlyAndValidated()
    {
        var configuration = Example() with { BackupClockUtcOffsetMinutes = 120 };
        Assert.Equal(120, configuration.BackupCaptureOptions(Date).ComparisonUtcOffsetMinutes);
        Assert.Null(configuration.PrimaryCaptureOptions(Date).ComparisonUtcOffsetMinutes);
        Assert.Throws<DomainValidationException>((configuration with { BackupClockUtcOffsetMinutes = 900 }).Validate);
        Assert.Throws<DomainValidationException>((configuration with { BackupWarnings = new(0, 10, 5) }).Validate);
        Assert.Throws<DomainValidationException>((configuration with { BackupWarnings = new(1, 10, 0) }).Validate);
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
