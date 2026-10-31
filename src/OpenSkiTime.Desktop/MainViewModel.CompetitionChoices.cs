using System.Collections.ObjectModel;
using System.Globalization;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

// Choices offered by the series and competition editors, and the course values derived while typing.
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
        SyncInPlace(CompetitionSeasonChoices, FisReferenceData.SeasonChoices(date, CompetitionSeason));
    }

    /// <summary>
    /// Season labels around the series start date for the editable series season field. The typed season is always
    /// one of them: removing the selected item would make the combo box clear the operator's text.
    /// </summary>
    public ObservableCollection<string> SeriesSeasonChoices { get; } = new(FisReferenceData.SeriesSeasonChoices(DateOnly.FromDateTime(DateTime.Today)));

    partial void OnStartDateTextChanged(string value) => SyncSeriesSeasonChoices();
    partial void OnSeasonChanged(string value) => SyncSeriesSeasonChoices();

    private void SyncSeriesSeasonChoices()
    {
        DateOnly? date = DateOnly.TryParseExact(StartDateText, s_dateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed : null;
        var wanted = FisReferenceData.SeriesSeasonChoices(date).ToList();
        if (!string.IsNullOrWhiteSpace(Season) && !wanted.Contains(Season)) { wanted.Add(Season); }
        wanted.Sort(StringComparer.Ordinal);
        SyncInPlace(SeriesSeasonChoices, wanted);
    }

    /// <summary>
    /// FIS calendar seasons around today, plus the season being browsed, so the calendar season choice never loses
    /// its selection.
    /// </summary>
    public ObservableCollection<string> SeriesCalendarSeasonChoices { get; } = new(FisReferenceData.SeasonChoices(DateOnly.FromDateTime(DateTime.Today), null));

    partial void OnSeriesCalendarSeasonChanged(string value)
        => SyncInPlace(SeriesCalendarSeasonChoices, FisReferenceData.SeasonChoices(DateOnly.FromDateTime(DateTime.Today), value));

    // Brings a choice list to the wanted items without clearing it, so an item that stays is never deselected.
    private static void SyncInPlace(ObservableCollection<string> items, IReadOnlyList<string> wanted)
    {
        foreach (var stale in items.Where(x => !wanted.Contains(x)).ToArray()) { items.Remove(stale); }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i >= items.Count || items[i] != wanted[i]) { items.Insert(i, wanted[i]); }
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
