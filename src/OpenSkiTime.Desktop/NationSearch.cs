using Avalonia.Controls;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

/// <summary>Nation search for <see cref="AutoCompleteBox"/>: "fi" finds FIN, "swi" finds SUI (Switzerland).</summary>
public static class NationSearch
{
    public static AutoCompleteFilterPredicate<object?> Filter { get; } =
        (search, item) => item is FisNation nation && FisReferenceData.Matches(nation, search);
}
