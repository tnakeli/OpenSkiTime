using System.Text.Json;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

/// <summary>Reusable user preferences; each event stores its own independent report snapshot.</summary>
public sealed class TimingReportDefaultsStore(string? directory = null)
{
    private readonly string _path = Path.Combine(directory ?? LocalDataDirectory.Path, "timing-report-defaults.json");

    public TimingReportDefaults Load() => File.Exists(_path)
        ? Normalize(JsonSerializer.Deserialize<TimingReportDefaults>(File.ReadAllText(_path))
            ?? throw new IOException("Timing report defaults are unreadable.")) : new();

    public TimingReportDefaults Save(TimingReportDefaults defaults)
    {
        var valid = Normalize(defaults);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(valid)); File.Move(temporary, _path, overwrite: true); }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        return valid;
    }

    private static TimingReportDefaults Normalize(TimingReportDefaults d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d with { TimerA = Device(d.TimerA), TimerB = Device(d.TimerB), StartDevice = Device(d.StartDevice),
            StartClock = Device(d.StartClock), FinishCellsA = Device(d.FinishCellsA), FinishCellsB = Device(d.FinishCellsB),
            ChiefOfTiming = Person(d.ChiefOfTiming), Timekeeper = Person(d.Timekeeper),
            TimerStartA = d.TimerStartA is null ? null : Device(d.TimerStartA),
            TimerStartB = d.TimerStartB is null ? null : Device(d.TimerStartB),
            ConnectionA = Text(d.ConnectionA), ConnectionB = Text(d.ConnectionB), Voice = Text(d.Voice) };
    }

    private static TimingReportDevice Device(TimingReportDevice? d)
    {
        if (d is null) { throw new DomainValidationException("Timing report equipment defaults are incomplete."); }
        if (d.ValidUntilSeason is < 1900 or > 2200)
        { throw new DomainValidationException("Equipment homologation expiry season is invalid."); }
        return d with { Brand = Text(d.Brand), Model = Text(d.Model), Serial = Text(d.Serial), Homologation = Text(d.Homologation) };
    }

    private static TimingReportPerson Person(TimingReportPerson? p)
    {
        if (p is null) { throw new DomainValidationException("Timing report contact defaults are incomplete."); }
        var nation = Text(p.Nation).ToUpperInvariant();
        if (nation.Length > 0 && (nation.Length != 3 || !nation.All(char.IsAsciiLetter)))
        { throw new DomainValidationException("Contact nation must be a three-letter FIS code."); }
        return new(Text(p.FirstName), Text(p.LastName).ToUpperInvariant(), nation, Text(p.Email), Text(p.Phone), Text(p.Number), Text(p.Company));
    }

    private static string Text(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length > 250 || value.Any(char.IsControl))
        { throw new DomainValidationException("Timing report defaults must be at most 250 characters per field and contain no control characters."); }
        return value;
    }
}
