using Avalonia.Controls;
using Avalonia.Input;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

/// <summary>
/// Nation search for <see cref="AutoCompleteBox"/>: "fi" finds FIN, "swi" finds SUI (Switzerland). The box accepts a
/// whole country name; when focus leaves, a name that matches exactly one nation becomes its code. Three-letter text is
/// kept as typed (the FIS list is a suggestion, not a restriction) and the domain still validates the saved code.
/// </summary>
public static class NationSearch
{
    static NationSearch() => InputElement.LostFocusEvent.AddClassHandler<AutoCompleteBox>((box, _) =>
    {
        if (ReferenceEquals(box.ItemFilter, Filter) && ToCode(box.Text) is { } code && code != box.Text) { box.Text = code; }
    });

    public static AutoCompleteFilterPredicate<object?> Filter { get; } =
        (search, item) => item is FisNation nation && FisReferenceData.Matches(nation, search);

    /// <summary>The nation code for a typed country name that matches exactly one nation; null to keep the text.</summary>
    public static string? ToCode(string? text)
    {
        var value = text?.Trim() ?? "";
        if (value.Length <= 3) { return null; }
        var exact = FisReferenceData.Nations.Where(x => string.Equals(x.Name, value, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) { return exact[0].Code; }
        var matches = FisReferenceData.Nations.Where(x => FisReferenceData.Matches(x, value)).ToArray();
        return matches.Length == 1 ? matches[0].Code : null;
    }
}
