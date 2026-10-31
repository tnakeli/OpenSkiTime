using System.Collections.ObjectModel;
using System.Globalization;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

// Choices offered by the competition editor, and the course values derived while typing.
public sealed partial class MainViewModel
{
    public IReadOnlyList<string> CompetitionCategoryChoices { get; } = FisReferenceData.CalendarCategories;
    public IReadOnlyList<FisNation> NationChoices { get; } = FisReferenceData.Nations;

    /// <summary>
    /// Seasons around the competition date. Updated in place, and the current season is always present, so the
    /// season combo box never loses its selection when the list changes.
    /// </summary>
    public ObservableCollection<string> CompetitionSeasonChoices { get; } = [];

    partial void OnCompetitionSeasonChanged(string value) => SyncCompetitionSeasonChoices();
    partial void OnCompetitionDateTextChanged(string value) => SyncCompetitionSeasonChoices();

    private void SyncCompetitionSeasonChoices()
    {
        DateOnly? date = DateOnly.TryParseExact(CompetitionDateText, s_dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : null;
        var wanted = FisReferenceData.SeasonChoices(date, CompetitionSeason);
        foreach (var stale in CompetitionSeasonChoices.Where(x => !wanted.Contains(x)).ToArray()) { CompetitionSeasonChoices.Remove(stale); }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= CompetitionSeasonChoices.Count || CompetitionSeasonChoices[i] != wanted[i]) { CompetitionSeasonChoices.Insert(i, wanted[i]); }
        }
    }

    // Vertical drop follows the altitudes while the operator types them. Loading a competition or applying a
    // homologation sets the altitudes first and the stored drop last, so a saved drop is never replaced on load.
    partial void OnCompetitionStartAltitudeChanged(string value) => UpdateCompetitionVerticalDrop();
    partial void OnCompetitionFinishAltitudeChanged(string value) => UpdateCompetitionVerticalDrop();

    private void UpdateCompetitionVerticalDrop()
    {
        if (CourseProfile.VerticalDrop(CompetitionStartAltitude, CompetitionFinishAltitude) is { } drop)
        { CompetitionVerticalDrop = drop.ToString(CultureInfo.InvariantCulture); }
    }
}
