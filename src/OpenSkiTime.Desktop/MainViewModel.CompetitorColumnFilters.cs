namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, string> _competitorColumnFilters = new(StringComparer.Ordinal);
    public string CompetitorColumnFilter(string key) => _competitorColumnFilters.GetValueOrDefault(key, "");
    public void SetCompetitorColumnFilter(string key, string text)
    {
        ArgumentNullException.ThrowIfNull(key); ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) { _competitorColumnFilters.Remove(key); }
        else { _competitorColumnFilters[key] = text; }
        RefreshVisibleCompetitors();
    }
    private bool MatchesCompetitorColumnFilters(CompetitorGridRow row)
        => _competitorColumnFilters.All(filter => filter.Value.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).All(term =>
        {
            var value = CompetitorColumnText(row, filter.Key);
            // "Men" must not also match "Women" just because it is a substring.
            return filter.Key == nameof(CompetitorGridRow.GenderText) && (term.Equals("Men", StringComparison.OrdinalIgnoreCase)
                || term.Equals("Women", StringComparison.OrdinalIgnoreCase))
                ? value.Equals(term, StringComparison.OrdinalIgnoreCase) : value.Contains(term, StringComparison.OrdinalIgnoreCase);
        }));
    private static string CompetitorColumnText(CompetitorGridRow row, string key) => key switch
    {
        nameof(CompetitorGridRow.FederationCode) => row.FederationCode,
        nameof(CompetitorGridRow.Surname) => row.Surname, nameof(CompetitorGridRow.FirstName) => row.FirstName,
        nameof(CompetitorGridRow.BirthYearText) => row.BirthYearText, nameof(CompetitorGridRow.GenderText) => row.GenderText,
        nameof(CompetitorGridRow.Nation) => row.Nation, nameof(CompetitorGridRow.Club) => row.Club,
        nameof(CompetitorGridRow.Category) => row.Category,
        nameof(CompetitorGridRow.FisDh) => row.FisDhText, nameof(CompetitorGridRow.FisSg) => row.FisSgText,
        nameof(CompetitorGridRow.FisSl) => row.FisSlText, nameof(CompetitorGridRow.FisGs) => row.FisGsText,
        nameof(CompetitorGridRow.FisAc) => row.FisAcText,
        _ when key.StartsWith("entry:", StringComparison.Ordinal) && Guid.TryParse(key[6..], out var id)
            => row.GridEntries.FirstOrDefault(x => x.CompetitionId == id)?.IsParticipating == true ? "Yes" : "No",
        _ => ""
    };
}
