using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class RacePersonEditor : ObservableObject
{
    public string Function { get; }
    public string Label => RaceInformation.JuryFunctionLabel(Function);
    [ObservableProperty] private string _firstName = "";
    [ObservableProperty] private string _lastName = "";
    [ObservableProperty] private string _nation = "";
    public RacePersonEditor(string function, FisPerson person)
    { ArgumentNullException.ThrowIfNull(person); Function = function; FirstName = person.FirstName; LastName = person.LastName; Nation = person.Nation; }
    public FisPerson Values => new(FirstName.Trim(), LastName.Trim().ToUpperInvariant(), Nation.Trim().ToUpperInvariant());
}

public sealed partial class ForerunnerEditor : ObservableObject
{
    [ObservableProperty] private string _letter = "";
    [ObservableProperty] private string _firstName = "";
    [ObservableProperty] private string _lastName = "";
    [ObservableProperty] private string _nation = "";
    public RaceForerunner Values => new(Letter.Trim().ToUpperInvariant(),
        new(FirstName.Trim(), LastName.Trim().ToUpperInvariant(), Nation.Trim().ToUpperInvariant()));
}

public sealed partial class RaceRunEditor : ObservableObject
{
    public int Number { get; }
    public string Label => $"Run {Number}";
    public RacePersonEditor Setter { get; }
    public ObservableCollection<ForerunnerEditor> Forerunners { get; } = [];
    [ObservableProperty] private ForerunnerEditor? _selectedForerunner;
    [ObservableProperty] private string _gates = "";
    [ObservableProperty] private string _turningGates = "";
    [ObservableProperty] private string _startTime = "";
    [ObservableProperty] private string _course = "";
    [ObservableProperty] private string _startAltitude = "";
    [ObservableProperty] private string _finishAltitude = "";
    [ObservableProperty] private string _drop = "";
    [ObservableProperty] private string _length = "";
    [ObservableProperty] private string _homologation = "";
    [ObservableProperty] private string _conditions = "";
    [ObservableProperty] private string _snow = "";
    [ObservableProperty] private string _startTemperature = "";
    [ObservableProperty] private string _finishTemperature = "";
    [ObservableProperty] private string _unlocatedTemperature = "";
    [ObservableProperty] private string _weatherSource = "";
    [ObservableProperty] private string _forecastTemperature = "";

    public RaceRunEditor(RaceRunInformation run)
    {
        ArgumentNullException.ThrowIfNull(run);
        Number = run.Number; Setter = new("Coursesetter", run.CourseSetter);
        Gates = Format(run.Gates); TurningGates = Format(run.TurningGates); StartTime = run.StartTime;
        Course = run.Course; StartAltitude = Format(run.StartAltitude); FinishAltitude = Format(run.FinishAltitude);
        Drop = Format(run.Drop); Length = Format(run.Length); Homologation = run.Homologation;
        Conditions = run.Weather?.Conditions ?? ""; Snow = run.Weather?.Snow ?? "";
        StartTemperature = Format(run.Weather?.StartTemperature); FinishTemperature = Format(run.Weather?.FinishTemperature);
        UnlocatedTemperature = Format(run.Weather?.UnlocatedTemperature);
        WeatherSource = run.Weather?.Source ?? "";
        // The provenance records the applied forecast's temperature and whether the forecast filled the place-unspecified
        // field. The field stays forecast-owned after reopening only while it still holds that value.
        ForecastTemperature = ForecastIn(WeatherSource);
        _forecastOwnsUnlocated = WeatherSource.EndsWith(FilledMarker, StringComparison.Ordinal) && SameNumber(UnlocatedTemperature, ForecastTemperature);
        foreach (var runner in run.Forerunners ?? [])
        { Forerunners.Add(new() { Letter = runner.Letter, FirstName = runner.Person.FirstName, LastName = runner.Person.LastName, Nation = runner.Person.Nation }); }
    }

    private static string Format<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture) ?? "";
    private static int? Integer(string value, string label) => value.Trim().Length == 0 ? null
        : int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
        : throw new DomainValidationException($"{label}: enter a whole number.");
    private static decimal? Temperature(string value) => value.Trim().Length == 0 ? null
        : decimal.TryParse(value.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) ? n
        : throw new DomainValidationException("Enter temperature as a decimal number in °C.");
    public RaceRunInformation Values => new(Number, Setter.Values, Integer(Gates, "Gates"), Integer(TurningGates, "Turns"),
        StartTime.Trim(), Course.Trim(), Integer(StartAltitude, "Start altitude"), Integer(FinishAltitude, "Finish altitude"),
        Integer(Drop, "Drop"), Integer(Length, "Length"), Homologation.Trim(), Forerunners.Select(x => x.Values).ToArray(),
        new(Conditions.Trim(), Snow.Trim(), Temperature(StartTemperature), Temperature(FinishTemperature), WeatherSource, Temperature(UnlocatedTemperature)));

    [RelayCommand]
    private void AddForerunner()
    {
        var letter = Enumerable.Range('A', 26).Select(x => ((char)x).ToString())
            .FirstOrDefault(x => Forerunners.All(r => r.Letter != x));
        if (letter is null)
        {
            var number = 27;
            while (Forerunners.Any(x => x.Letter == "F" + number.ToString(CultureInfo.InvariantCulture))) { number++; }
            letter = "F" + number.ToString(CultureInfo.InvariantCulture);
        }
        var row = new ForerunnerEditor { Letter = letter }; Forerunners.Add(row); SelectedForerunner = row;
    }
    [RelayCommand] private void RemoveForerunner()
    { if (SelectedForerunner is { } row) { Forerunners.Remove(row); } }

    private const string ForecastPrefix = "Open-Meteo forecast · ";
    private const string FilledMarker = " · filled air place unspecified";

    // True only while the place-unspecified temperature holds a value the forecast put there. Typing in the field ends
    // that ownership, so a later forecast never replaces a value the operator entered, even an equal one.
    private bool _forecastOwnsUnlocated;
    private bool _fillingFromForecast;
    partial void OnUnlocatedTemperatureChanged(string value)
    {
        if (_fillingFromForecast) { return; }
        _forecastOwnsUnlocated = false;
        // The saved provenance must not claim the field either, or reopening would hand it back to the forecast.
        if (WeatherSource.EndsWith(FilledMarker, StringComparison.Ordinal)) { WeatherSource = WeatherSource[..^FilledMarker.Length]; }
    }

    private void FillUnlocated(string value)
    {
        _fillingFromForecast = true;
        try { UnlocatedTemperature = value; } finally { _fillingFromForecast = false; }
    }

    // "4" and "4.0" are the same forecast value (a saved decimal may come back without its trailing zero).
    private static bool SameNumber(string a, string b)
        => decimal.TryParse(a.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(b, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var y) && x == y;

    private static string ForecastIn(string source) => source.StartsWith(ForecastPrefix, StringComparison.Ordinal)
        ? source.Split(" · ").FirstOrDefault(x => x.EndsWith(" °C", StringComparison.Ordinal))?[..^3] ?? "" : "";

    public void ApplyForecast(WeatherHour hour)
    {
        ArgumentNullException.ThrowIfNull(hour);
        if (string.IsNullOrWhiteSpace(Conditions)) { Conditions = hour.Conditions; }
        // A model grid-cell temperature is not a measured start/finish temperature, so it goes to the
        // place-unspecified field: into an empty field, or replacing a value an earlier forecast put there. A value the
        // operator typed is never replaced. An hour without a temperature removes the earlier forecast's value.
        ForecastTemperature = hour.Temperature?.ToString("0.0", CultureInfo.InvariantCulture) ?? "unavailable";
        var mayFill = string.IsNullOrWhiteSpace(UnlocatedTemperature) || _forecastOwnsUnlocated;
        if (mayFill) { FillUnlocated(hour.Temperature is null ? "" : ForecastTemperature); }
        _forecastOwnsUnlocated = mayFill && hour.Temperature is not null;
        WeatherSource = $"{ForecastPrefix}{hour.LocalTime:yyyy-MM-dd HH:mm} {hour.Timezone} · {ForecastTemperature} °C · retrieved {hour.RetrievedAt.ToUniversalTime():yyyy-MM-dd HH:mm} UTC"
            + (_forecastOwnsUnlocated ? FilledMarker : "");
    }

    public void FillEmpty(string field, string value)
    {
        ArgumentNullException.ThrowIfNull(field); ArgumentNullException.ThrowIfNull(value);
        var clean = value.Trim();
        var number = clean.EndsWith(" m", StringComparison.Ordinal) ? clean[..^2] : clean;
        switch (field.Trim().ToLowerInvariant())
        {
            case "course": case "course name": if (string.IsNullOrWhiteSpace(Course)) { Course = clean; } break;
            case "start altitude": if (string.IsNullOrWhiteSpace(StartAltitude) && int.TryParse(number, out _)) { StartAltitude = number; } break;
            case "finish altitude": if (string.IsNullOrWhiteSpace(FinishAltitude) && int.TryParse(number, out _)) { FinishAltitude = number; } break;
            case "vertical drop": case "drop": if (string.IsNullOrWhiteSpace(Drop) && int.TryParse(number, out _)) { Drop = number; } break;
            case "length": if (string.IsNullOrWhiteSpace(Length) && int.TryParse(number, out _)) { Length = number; } break;
            case "homologation": case "homologation number": if (string.IsNullOrWhiteSpace(Homologation)) { Homologation = clean; } break;
            case "gates": if (string.IsNullOrWhiteSpace(Gates) && int.TryParse(number, out _)) { Gates = number; } break;
            case "turning gates": case "directions": if (string.IsNullOrWhiteSpace(TurningGates) && int.TryParse(number, out _)) { TurningGates = number; } break;
            case "starting time": case "start time": if (string.IsNullOrWhiteSpace(StartTime) && TimeOnly.TryParseExact(clean, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) { StartTime = clean; } break;
            case "conditions": if (string.IsNullOrWhiteSpace(Conditions)) { Conditions = clean; } break;
            case "snow": if (string.IsNullOrWhiteSpace(Snow)) { Snow = clean; } break;
        }
    }
}
