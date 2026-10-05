using OpenSkiTime.Application;
using Xunit;

namespace OpenSkiTime.Desktop.Tests;

public sealed class TimingPreferencesStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ost-roles-" + Guid.NewGuid().ToString("N"));
    public TimingPreferencesStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void RoleMappingsIntermediatesBClockAndWarningsSurviveSaveAndLoad()
    {
        var timy = new TimingConnection(TimingSourceType.TimyUsb) { UsbId = "A1", Firmware = "1.9" };
        var mt1 = new TimingConnection(TimingSourceType.Mt1Serial) { Port = "COM4", BaudRate = 9600 };
        var saved = new TimingRoleConfiguration([
            new(TimingRole.Start, timy, 0), new(TimingRole.Finish, timy, 1),
            new(TimingRole.Intermediate(1), timy, 2), new(TimingRole.Intermediate(2), timy, 3), new(TimingRole.Intermediate(3), timy, 4),
            new(TimingRole.BackupStart, mt1, 0), new(TimingRole.BackupFinish, mt1, 1)])
        { BackupWarnings = new(2, 15, 7) };
        new TimingPreferencesStore(_root).Save(saved);
        var loaded = new TimingPreferencesStore(_root).LoadWithNotes()!;
        Assert.Empty(loaded.MigrationNotes);
        var c = loaded.Configuration;
        Assert.Equal(saved.Assignments.OrderBy(x => x.Role.Kind).ThenBy(x => x.Role.Index),
            c.Assignments.OrderBy(x => x.Role.Kind).ThenBy(x => x.Role.Index));
        Assert.Equal(new BackupClockWarnings(2, 15, 7), c.BackupWarnings);
        Assert.Equal([2, 3, 4], c.Intermediates.Select(x => x.Channel));
        Assert.Equal("COM4", c.BackupStart!.Connection.Port);
        Assert.DoesNotContain("Password", File.ReadAllText(Path.Combine(_root, "timing-roles.json")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidConfigurationIsNotSaved()
    {
        var timy = new TimingConnection(TimingSourceType.TimyUsb);
        Assert.Throws<OpenSkiTime.Domain.DomainValidationException>(() =>
            new TimingPreferencesStore(_root).Save(new([new(TimingRole.Start, timy, 0)])));
        Assert.False(File.Exists(Path.Combine(_root, "timing-roles.json")));
        Assert.Null(new TimingPreferencesStore(_root).Load());
    }

    [Fact]
    public void LegacyPrimaryAndBackupSettingsMigrateVisiblyWithoutTemporaryState()
    {
        File.WriteAllText(Path.Combine(_root, "timing-settings.json"),
            """{"Source":"Timy 2/3 · USB","Port":"","UsbId":"7","Baud":38400,"StartChannel":0,"FinishChannel":1,"IntermediateChannels":"2, 3","Firmware":"1.8"}""");
        File.WriteAllText(Path.Combine(_root, "auxiliary-settings.json"),
            """{"Source":"MT1 · USB / serial","Port":"COM9","UsbId":"","Baud":38400,"StartChannel":0,"FinishChannel":1,"Firmware":"Not queried","StartDevice":"","FinishDevice":"","Username":"","BackupStartWarningMilliseconds":1,"BackupFinishWarningMilliseconds":10,"BackupMissingGraceSeconds":5,"AuxiliaryClockOffsetMinutes":null}""");
        var before = File.ReadAllText(Path.Combine(_root, "timing-settings.json"));
        var result = new TimingPreferencesStore(_root).LoadWithNotes()!;
        var c = result.Configuration;
        c.Validate();
        Assert.Equal(("7", 0, 1), (c.Start!.Connection.UsbId, c.Start.Channel, c.Finish!.Channel));
        Assert.Equal([2, 3], c.Intermediates.Select(x => x.Channel));
        Assert.Equal(("COM9", 0, 1), (c.BackupStart!.Connection.Port, c.BackupStart.Channel, c.BackupFinish!.Channel));
        Assert.Equal(new BackupClockWarnings(1, 10, 5), c.BackupWarnings);
        Assert.Contains(result.MigrationNotes, x => x.Contains("B Clock", StringComparison.Ordinal));
        // Legacy files stay unchanged; nothing is written until the operator saves.
        Assert.Equal(before, File.ReadAllText(Path.Combine(_root, "timing-settings.json")));
        Assert.False(File.Exists(Path.Combine(_root, "timing-roles.json")));
        new TimingPreferencesStore(_root).Save(c);
        Assert.Empty(new TimingPreferencesStore(_root).LoadWithNotes()!.MigrationNotes);
    }

    [Fact]
    public void LegacyWarningThresholdsMigrateWithoutAPrimarySource()
    {
        File.WriteAllText(Path.Combine(_root, "auxiliary-settings.json"),
            """{"Source":"Unknown","Port":"","UsbId":"","Baud":38400,"StartChannel":0,"FinishChannel":1,"Firmware":"","StartDevice":"","FinishDevice":"","Username":"","BackupStartWarningMilliseconds":3,"BackupFinishWarningMilliseconds":12,"BackupMissingGraceSeconds":9,"AuxiliaryClockOffsetMinutes":120}""");
        var c = new TimingPreferencesStore(_root).Load()!;
        Assert.Equal(new BackupClockWarnings(3, 12, 9), c.BackupWarnings);
        Assert.False(c.HasBackupClock);
        Assert.False(c.HasPrimary);
    }

    [Fact]
    public void UnreadableLegacyIntermediatesAreReportedInsteadOfGuessed()
    {
        File.WriteAllText(Path.Combine(_root, "timing-settings.json"),
            """{"Source":"MT1 · USB / serial","Port":"COM3","UsbId":"","Baud":38400,"StartChannel":0,"FinishChannel":1,"IntermediateChannels":"2;x","Firmware":"Not queried"}""");
        var result = new TimingPreferencesStore(_root).LoadWithNotes()!;
        Assert.Empty(result.Configuration.Intermediates);
        Assert.Contains(result.MigrationNotes, x => x.Contains("2;x", StringComparison.Ordinal));
        Assert.Equal("COM3", result.Configuration.Start!.Connection.Port);
    }

    [Fact]
    public void CorruptRoleFileLoadsAsAbsent()
    {
        File.WriteAllText(Path.Combine(_root, "timing-roles.json"), "{ not json");
        Assert.Null(new TimingPreferencesStore(_root).Load());
    }
}
