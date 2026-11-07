using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenSkiTime.Tests;

// The FIS "Alpine Result XML Description" v2.13, sections 2 and 3, as an executable checklist for a generated result file:
// the elements allowed at each level in the order of the element tables, the required children, attribute domains, value
// formats (times as MM:SS:ZZ) and the general rule of section 1 that a used optional element holds its required children.
//
// Reading of the specification (it is not consistent with itself): the element tables are normative and the examples only
// show the structure (section 1). Where a table contradicts itself, the reading used by the majority of the document and
// by its example wins: Startelev (2.3.4 and the example, not the table's Startlelev) and the technical delegate's Number (the example).
internal static class FisResultXmlSpec
{
    private sealed record Child(string Name, bool Required = false, bool Many = false, Func<string, bool>? Value = null);
    private sealed record Attribute(string Name, bool Required, string[]? Values = null, Func<string, bool>? Check = null);
    private sealed record Rule(Child[] Children, Attribute[] Attributes);

    private static readonly string[] s_functions = ["ChiefRace", "Referee", "Assistantreferee", "ChiefCourse", "Startreferee", "Finishreferee", "ChiefTiming"];
    private static readonly string[] s_statuses = ["DNS", "DNS1", "DNS2", "DSQ", "DSQ1", "DSQ2", "DNF", "DNF1", "DNF2", "DNQ", "DNQ1", "DPO", "NPS", "DQB", "DQO"];

    private static bool Int(string v) => int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static bool Decimal(string v) => Regex.IsMatch(v, @"^-?\d+(\.\d+)?$");
    // Tables: MM:SS:ZZ (minutes, seconds, hundredths).
    private static bool Time(string v) => Regex.IsMatch(v, @"^\d{2,}:[0-5]\d:\d{2}$");
    private static bool Clock(string v) => Regex.IsMatch(v, @"^([01]\d|2[0-3]):[0-5]\d$");
    private static bool Text(string v) => v.Trim().Length > 0;
    private static bool Nation(string v) => Regex.IsMatch(v, "^[A-Z]{3}$");

    private static Child C(string name, bool required = false, bool many = false, Func<string, bool>? value = null) => new(name, required, many, value);
    private static Attribute A(string name, bool required, string[]? values = null) => new(name, required, values);

    private static readonly Dictionary<string, Rule> s_rules = new()
    {
        ["Fisresults"] = new([C("Raceheader", true), C("AL_race", true)], []),
        ["Fisresults/Raceheader"] = new([C("Season", true, value: Int), C("Codex", true, value: Int), C("Nation", true, value: Nation),
            C("Discipline", true, value: v => new[] { "DH", "SL", "GS", "SG", "AC", "TE", "KOS", "KOG", "PGS", "PSL", "CE", "IND", "P", "CAR" }.Contains(v)),
            C("Category", true, value: Text), C("Type", true, value: v => new[] { "Startlist", "Partial", "Unofficial", "Official" }.Contains(v)),
            C("Training", value: Int), C("Speedcodex", value: Int), C("Eventname", value: Text), C("Place", value: Text), C("Racedate"),
            C("Tempunit", value: v => new[] { "C", "F", "K" }.Contains(v)), C("Longunit", value: v => new[] { "m", "km", "mi", "in", "yd" }.Contains(v)),
            C("Speedunit", value: Text), C("Windunit", value: Text)],
            [A("Sector", true, ["AL"]), A("Gender", true, ["M", "W", "A"])]),
        ["Fisresults/Raceheader/Racedate"] = new([C("Day", true, value: Int), C("Month", true, value: Int), C("Year", true, value: v => Regex.IsMatch(v, @"^\d{4}$"))], []),
        ["Fisresults/AL_race"] = new([C("AL_raceinfo", true), C("AL_classified", true), C("AL_notclassified", true)], []),
        ["Fisresults/AL_race/AL_raceinfo"] = new([C("Usedfislist", value: Text), C("Appliedpenalty", value: Decimal), C("Calculatedpenalty", value: Decimal),
            C("Fvalue", value: Int), C("Jury", true, true), C("Member", true, true), C("Runinfo", true, true), C("Timingby", value: Text),
            C("Dataprocessingby", value: Text), C("Softwarecompany", value: Text), C("Softwarename", value: Text), C("Softwareversion", value: Text)], []),
        ["Fisresults/AL_race/AL_raceinfo/Jury"] = new([C("Number", value: Text), C("Lastname", true, value: Text), C("Firstname", true, value: Text),
            C("Nation", true, value: Nation), C("Email", value: Text), C("Phonenbr", value: Text)],
            [new("Function", true, [.. s_functions, "TechnicalDelegate"])]),
        ["Fisresults/AL_race/AL_raceinfo/Member"] = new([C("Lastname", true, value: Text), C("Firstname", true, value: Text), C("Nation", true, value: Nation)],
            [new("Function", true, s_functions)]),
        ["Fisresults/AL_race/AL_raceinfo/Runinfo"] = new([C("Course", true), C("Weather", many: true), C("Starttime", true, value: Clock), C("Endtime", value: Clock)],
            [new("No", true, Check: Int)]),
        ["Fisresults/AL_race/AL_raceinfo/Runinfo/Course"] = new([C("Name", value: Text), C("Homologation", true, value: Text), C("Length", value: Int),
            C("Gates", true, value: Int), C("Turninggates", true, value: Int), C("Startelev", true, value: Int), C("Finishelev", true, value: Int),
            C("Coursesetter", true), C("Forerunner", many: true), C("Intermediate", many: true)], []),
        ["Fisresults/AL_race/AL_raceinfo/Runinfo/Course/Coursesetter"] = new([C("Lastname", true, value: Text), C("Firstname", true, value: Text), C("Nation", true, value: Nation)], []),
        ["Fisresults/AL_race/AL_raceinfo/Runinfo/Course/Forerunner"] = new([C("Lastname", true, value: Text), C("Firstname", true, value: Text), C("Nation", true, value: Nation)],
            [new("Order", true, Check: Int)]),
        ["Fisresults/AL_race/AL_raceinfo/Runinfo/Weather"] = new([C("Time", value: Text), C("Place", value: Text), C("Weather", value: Text), C("Snow", value: Text),
            C("Temperatureair", value: Decimal), C("Temperaturesnow", value: Decimal), C("Humiditystart", value: Int), C("Windspeed", value: Decimal)], []),
        ["Fisresults/AL_race/AL_classified"] = new([C("AL_ranked", many: true)], []),
        ["Fisresults/AL_race/AL_classified/AL_ranked"] = new([C("Rank", true, value: Int), C("Order", true, value: Int), C("Bib", true, value: Text),
            C("Competitor", true), C("AL_result", true), C("AL_resultdetail")], [A("Status", true, ["QLF"])]),
        ["Fisresults/AL_race/AL_classified/AL_ranked/Competitor"] = Competitor(),
        ["Fisresults/AL_race/AL_classified/AL_ranked/AL_result"] = new([C("Timerun1", true, value: Time), C("Timerun2", value: Time), C("Timerun3", value: Time),
            C("Totaltime", true, value: Time), C("Diff", value: Time), C("Racepoints", true, value: Decimal), C("Level", value: Text)], []),
        ["Fisresults/AL_race/AL_notclassified"] = new([C("AL_notranked", many: true)], []),
        ["Fisresults/AL_race/AL_notclassified/AL_notranked"] = new([C("Run", true, value: Int), C("Bib", true, value: Text), C("Competitor", true),
            C("Gate", value: Int), C("AL_result"), C("AL_resultdetail"), C("Reason", value: Text)], [A("Status", true, s_statuses)]),
        ["Fisresults/AL_race/AL_notclassified/AL_notranked/Competitor"] = Competitor(),
        // 3.11: Racepoints is required whenever the AL_result of a non-finisher is present.
        ["Fisresults/AL_race/AL_notclassified/AL_notranked/AL_result"] = new([C("Timerun1", true, value: Time), C("Timerun2", value: Time), C("Timerun3", value: Time),
            C("Totaltime", value: Time), C("Diff", value: Time), C("Racepoints", true, value: Decimal), C("Level", value: Text)], []),
    };

    private static Rule Competitor() => new([C("Fiscode", true, value: Text), C("Lastname", true, value: Text), C("Firstname", true, value: Text),
        C("Gender", value: v => v is "M" or "F"), C("Nation", true, value: Nation), C("Yearofbirth", value: Int), C("Clubname", value: Text)], []);

    /// <summary>Every deviation from the specification found in the document; empty when it conforms.</summary>
    public static IReadOnlyList<string> Validate(XDocument document, string? fileName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var problems = new List<string>();
        if (document.Declaration is not { Encoding: { } encoding } || !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
        { problems.Add("The XML declaration must state UTF-8 (section 1)."); }
        if (fileName is not null && !Regex.IsMatch(fileName, @"^[A-Z]{3}\d{4}\.xml$")) { problems.Add($"File name {fileName} is not <NSA code><codex>.xml (section 1)."); }
        if (document.Root is null) { problems.Add("No root element."); return problems; }
        Check(document.Root, document.Root.Name.LocalName, problems);
        var ranked = document.Descendants("AL_ranked").Select(x => x.Element("Bib")?.Value).ToHashSet();
        var notRanked = document.Descendants("AL_notranked").Select(x => x.Element("Bib")?.Value).ToHashSet();
        if (ranked.Overlaps(notRanked)) { problems.Add("A bib is both classified and not classified."); }
        return problems;
    }

    private static void Check(XElement element, string path, List<string> problems)
    {
        if (!s_rules.TryGetValue(path, out var rule)) { return; }
        foreach (var attribute in element.Attributes())
        {
            var allowed = rule.Attributes.FirstOrDefault(x => x.Name == attribute.Name.LocalName);
            if (allowed is null) { problems.Add($"{path}: attribute {attribute.Name.LocalName} is not defined."); }
            else if (allowed.Values is { } values && !values.Contains(attribute.Value)) { problems.Add($"{path}/@{allowed.Name}: '{attribute.Value}' is not one of {string.Join(", ", values)}."); }
            else if (allowed.Check is { } check && !check(attribute.Value)) { problems.Add($"{path}/@{allowed.Name}: '{attribute.Value}' is not valid."); }
        }
        foreach (var attribute in rule.Attributes.Where(x => x.Required && element.Attribute(x.Name) is null)) { problems.Add($"{path}: required attribute {attribute.Name} is missing."); }

        var children = element.Elements().ToArray();
        var lastIndex = -1;
        foreach (var child in children)
        {
            var index = Array.FindIndex(rule.Children, x => x.Name == child.Name.LocalName);
            if (index < 0) { problems.Add($"{path}: element {child.Name.LocalName} is not defined here."); continue; }
            if (index < lastIndex) { problems.Add($"{path}: element {child.Name.LocalName} is out of order (table order: {string.Join(", ", rule.Children.Select(x => x.Name))})."); }
            lastIndex = Math.Max(lastIndex, index);
            var definition = rule.Children[index];
            if (!definition.Many && children.Count(x => x.Name == child.Name) > 1) { problems.Add($"{path}: element {child.Name.LocalName} must occur once."); }
            var childPath = path + "/" + child.Name.LocalName;
            // A container such as AL_notclassified is required but may hold nothing; a value element is never empty (section 1).
            if (!child.HasElements && !s_rules.ContainsKey(childPath))
            {
                // Section 1: used optional elements are not empty; every leaf here carries a value.
                if (string.IsNullOrWhiteSpace(child.Value)) { problems.Add($"{childPath}: is empty."); }
                else if (definition.Value is { } valid && !valid(child.Value)) { problems.Add($"{childPath}: value '{child.Value}' is not valid."); }
                else if (child.Value != child.Value.Trim()) { problems.Add($"{childPath}: value has surrounding spaces."); }
            }
            Check(child, childPath, problems);
        }
        foreach (var required in rule.Children.Where(x => x.Required && !children.Any(c => c.Name.LocalName == x.Name))) { problems.Add($"{path}: required element {required.Name} is missing."); }
    }
}
