using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

public sealed record TimingPreferencesLoad(TimingRoleConfiguration Configuration, IReadOnlyList<string> MigrationNotes);

// Machine preferences only. Secrets stay in Credential Manager, timing data in the event file.
// Role assignments are stored in timing-roles.json. Former timing-settings.json / auxiliary-settings.json files are read
// once as a migration source when no role file exists and are left unchanged on disk.
public sealed class TimingPreferencesStore(string? directory = null)
{
    private const int FormatVersion = 1;
    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _directory = directory ?? LocalDataDirectory.Path;
    private string RolePath => Path.Combine(_directory, "timing-roles.json");
    private string LegacyTimingPath => Path.Combine(_directory, "timing-settings.json");
    private string LegacyAuxiliaryPath => Path.Combine(_directory, "auxiliary-settings.json");

    private sealed record Document(int Version, TimingRoleConfigurationDocument Configuration);
    private sealed record TimingRoleConfigurationDocument(TimingSourceAssignment[] Assignments, BackupClockWarnings BackupWarnings);

    // Legacy shapes, read only for migration.
    private sealed record LegacyTiming(string Source, string Port, string UsbId, int Baud,
        int StartChannel, int FinishChannel, string IntermediateChannels, string Firmware);
    private sealed record LegacyAuxiliary(string Source, string Port, string UsbId, int Baud, int StartChannel,
        int FinishChannel, string Firmware, string StartDevice, string FinishDevice, string Username,
        int BackupStartWarningMilliseconds = 1, int BackupFinishWarningMilliseconds = 10, int BackupMissingGraceSeconds = 5);

    public TimingRoleConfiguration? Load() => LoadWithNotes()?.Configuration;

    public TimingPreferencesLoad? LoadWithNotes()
    {
        try
        {
            if (File.Exists(RolePath))
            {
                var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(RolePath), s_json);
                if (document is not { Version: FormatVersion, Configuration: { } c }) { return null; }
                var configuration = new TimingRoleConfiguration(c.Assignments ?? [])
                { BackupWarnings = c.BackupWarnings ?? new() };
                return new(configuration, []);
            }
            return Migrate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    public void Save(TimingRoleConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.Validate();
        Directory.CreateDirectory(_directory);
        var document = new Document(FormatVersion, new(configuration.Assignments.ToArray(), configuration.BackupWarnings));
        var temporary = RolePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(document, s_json)); File.Move(temporary, RolePath, overwrite: true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    private TimingPreferencesLoad? Migrate()
    {
        var timing = ReadLegacy<LegacyTiming>(LegacyTimingPath);
        var auxiliary = ReadLegacy<LegacyAuxiliary>(LegacyAuxiliaryPath);
        if (timing is null && auxiliary is null) { return null; }
        var notes = new List<string>();
        var assignments = new List<TimingSourceAssignment>();
        var configuration = TimingRoleConfiguration.Empty;
        if (timing is not null && TimingSourceTypes.Parse(timing.Source) is { } source)
        {
            var connection = new TimingConnection(source)
            { Port = timing.Port ?? "", UsbId = timing.UsbId ?? "", BaudRate = timing.Baud is >= 1200 and <= 115200 ? timing.Baud : 38400, Firmware = timing.Firmware ?? "Not queried" };
            assignments.Add(new(TimingRole.Start, connection, timing.StartChannel));
            assignments.Add(new(TimingRole.Finish, connection, timing.FinishChannel));
            var parts = string.IsNullOrWhiteSpace(timing.IntermediateChannels) ? []
                : timing.IntermediateChannels.Split(',', StringSplitOptions.TrimEntries);
            var channels = parts.Select(x => int.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : -1).ToArray();
            if (channels.All(x => x is >= 0 and <= 8)) { assignments.AddRange(channels.Select((c, i) => new TimingSourceAssignment(TimingRole.Intermediate(i + 1), connection, c))); }
            else { notes.Add($"The former intermediate channel list \"{timing.IntermediateChannels}\" could not be read. Assign intermediates again."); }
            if (source == TimingSourceType.AlgeResults)
            { notes.Add("ALGE Results device IDs and username were not part of the former primary settings. Enter them for Start and Finish."); }
        }
        else if (timing is not null) { notes.Add($"The former primary source \"{timing.Source}\" is not recognized. Assign Start and Finish again."); }
        if (auxiliary is not null)
        {
            configuration = configuration with
            {
                BackupWarnings = new(Math.Clamp(auxiliary.BackupStartWarningMilliseconds, 1, 10000),
                    Math.Clamp(auxiliary.BackupFinishWarningMilliseconds, 1, 10000), Math.Clamp(auxiliary.BackupMissingGraceSeconds, 1, 300))
            };
            if (TimingSourceTypes.Parse(auxiliary.Source) is { } backupSource && TimingSourceTypes.DeviceRead.Contains(backupSource))
            {
                // The former connection served both live B and temporary B/hand imports. Its role was never saved, so
                // it is proposed as B Clock and flagged for the operator to confirm rather than silently trusted.
                var connection = new TimingConnection(backupSource)
                {
                    Port = auxiliary.Port ?? "", UsbId = auxiliary.UsbId ?? "", BaudRate = auxiliary.Baud is >= 1200 and <= 115200 ? auxiliary.Baud : 38400,
                    Firmware = auxiliary.Firmware ?? "Not queried", AlgeUsername = auxiliary.Username ?? ""
                };
                assignments.Add(new(TimingRole.BackupStart, connection with { AlgeDeviceId = auxiliary.StartDevice ?? "" }, auxiliary.StartChannel));
                assignments.Add(new(TimingRole.BackupFinish, connection with { AlgeDeviceId = auxiliary.FinishDevice ?? "" }, auxiliary.FinishChannel));
                notes.Add("B Clock Start/Finish were proposed from the former optional B / hand-clock connection. Check that this device is the permanent B Clock, or clear it, then save.");
            }
        }
        configuration = configuration with { Assignments = assignments };
        try { configuration.Validate(); }
        catch (OpenSkiTime.Domain.DomainValidationException ex)
        { notes.Add("The migrated settings need attention before they can be saved: " + ex.Message); }
        return new(configuration, notes);
    }

    private static T? ReadLegacy<T>(string path) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (JsonException) { return null; }
    }
}
