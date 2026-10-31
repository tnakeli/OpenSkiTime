using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    public ObservableCollection<FisCourseHomologation> CompetitionHomologations { get; } = [];
    public bool HasCompetitionHomologations => CompetitionHomologations.Count > 0;
    [ObservableProperty] private FisCourseHomologation? _selectedCompetitionHomologation;
    [ObservableProperty] private bool _isHomologationBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompetitionHomologationStatus))]
    private string _competitionHomologationStatus = "";
    public bool HasCompetitionHomologationStatus => !string.IsNullOrWhiteSpace(CompetitionHomologationStatus);
    private (string? Path, Guid? Id, string Nation, Discipline Discipline, string Gender, string Date)? _homologationContext;
    // The FIS lookup takes the calendar gender code (M, W, A), not the displayed Men / Women / Mixed.
    // What decides the list: nation, discipline, gender and race date. The location only chooses the preselected course.
    private (string?, Guid?, string, Discipline, string, string) CourseContext()
        => (workspace.FilePath, _editingCompetitionId, CompetitionNation.Trim().ToUpperInvariant(), CompetitionDiscipline, CompetitionGenderLabels.ToCode(CompetitionGender), CompetitionDateText);

    [RelayCommand]
    private async Task BrowseCompetitionHomologationsAsync()
    {
        if (IsHomologationBusy || !IsCompetitionEditing) { return; }
        var context = CourseContext(); IsHomologationBusy = true;
        CompetitionHomologationStatus = "Fetching FIS homologations…";
        try
        {
            var courses = await new FisCourseClient(_informationHttp).FindAsync(context.Item3, context.Item4,
                context.Item5, AsDate(context.Item6, "Competition date"), _fisStore.ReadApiKey());
            if (context != CourseContext()) { return; }
            _homologationContext = context;
            SelectedCompetitionHomologation = null; CompetitionHomologations.Clear();
            foreach (var course in courses) { CompetitionHomologations.Add(course); }
            OnPropertyChanged(nameof(HasCompetitionHomologations));
            // Nothing is applied automatically: the best match is preselected and Apply homologation copies it.
            var match = FisCourseClient.BestMatch(courses, CompetitionLocation, CompetitionHomologation);
            SelectedCompetitionHomologation = match;
            CompetitionHomologationStatus = courses.Count == 0
                ? $"FIS has no homologations for this discipline and gender in {context.Item3} valid on the race date. Enter course details manually."
                : match is not null
                    ? $"{courses.Count} homologations in {context.Item3}. Selected {match.Place} · {match.CourseName}; check it, or choose another course, then Apply homologation."
                    : string.IsNullOrWhiteSpace(CompetitionLocation) ? $"{courses.Count} homologations in {context.Item3}. Choose the course, then Apply homologation."
                    : $"{courses.Count} homologations in {context.Item3}; none at \"{CompetitionLocation.Trim()}\". Choose the course, then Apply homologation.";
        }
        catch (Exception ex) when (ex is DomainValidationException or HttpRequestException or IOException or OperationCanceledException or PlatformNotSupportedException)
        {
            if (context == CourseContext()) { CompetitionHomologationStatus = ex is HttpRequestException or OperationCanceledException
                ? "FIS homologation lookup failed or timed out. Retry or enter course details manually." : ex.Message; }
        }
        finally
        {
            IsHomologationBusy = false;
            if (context != CourseContext()) { CompetitionHomologationStatus = "Competition details changed during the lookup. Browse again for the current competition."; }
        }
    }

    [RelayCommand]
    private void ApplyCompetitionHomologation()
    {
        if (SelectedCompetitionHomologation is not { } course || _homologationContext != CourseContext())
        { CompetitionHomologationStatus = "Browse again after changing the nation, discipline, gender or date."; return; }
        CompetitionCourseName = course.CourseName; CompetitionHomologation = course.HomologationCode;
        CompetitionStartAltitude = course.StartAltitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionFinishAltitude = course.FinishAltitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionVerticalDrop = course.VerticalDrop?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionCourseLength = course.CourseLength?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionHomologationStatus = "Homologation applied to the editor. Save competition to keep it; course fields remain editable.";
    }
}
