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
    public bool HasCompetitionHomologations => IsCompetitionFis && CompetitionHomologations.Count > 0;
    [ObservableProperty] private FisCourseHomologation? _selectedCompetitionHomologation;
    [ObservableProperty] private bool _isHomologationBusy;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCompetitionHomologationStatus))]
    private string _competitionHomologationStatus = "";
    public bool HasCompetitionHomologationStatus => !string.IsNullOrWhiteSpace(CompetitionHomologationStatus);
    private (string? Path, Guid? Id, string Place, string Nation, Discipline Discipline, string Gender, string Date)? _homologationContext;
    private (string?, Guid?, string, string, Discipline, string, string) CourseContext()
        => (workspace.FilePath, _editingCompetitionId, CompetitionLocation.Trim(), CompetitionNation.Trim().ToUpperInvariant(), CompetitionDiscipline, CompetitionGender, CompetitionDateText);

    [RelayCommand]
    private async Task BrowseCompetitionHomologationsAsync()
    {
        if (IsHomologationBusy || !IsCompetitionEditing || !IsCompetitionFis) { return; }
        var context = CourseContext(); IsHomologationBusy = true;
        CompetitionHomologationStatus = "Fetching FIS homologations…";
        try
        {
            var courses = await new FisCourseClient(_informationHttp).FindAsync(context.Item3, context.Item4,
                context.Item5, context.Item6, AsDate(context.Item7, "Competition date"), _fisStore.ReadApiKey());
            if (context != CourseContext()) { return; }
            _homologationContext = context;
            SelectedCompetitionHomologation = null; CompetitionHomologations.Clear();
            foreach (var course in courses) { CompetitionHomologations.Add(course); }
            OnPropertyChanged(nameof(HasCompetitionHomologations));
            CompetitionHomologationStatus = courses.Count == 0 ? "No homologations match this location, discipline, gender and race date. Enter course details manually."
                : "Choose a homologation and apply it. Course fields and per-run Results details remain editable.";
            if (courses.Count == 1) { SelectedCompetitionHomologation = courses[0]; ApplyCompetitionHomologation(); }
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
        { CompetitionHomologationStatus = "Browse again after changing the location, date or event."; return; }
        CompetitionCourseName = course.CourseName; CompetitionHomologation = course.HomologationCode;
        CompetitionStartAltitude = course.StartAltitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionFinishAltitude = course.FinishAltitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionVerticalDrop = course.VerticalDrop?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionCourseLength = course.CourseLength?.ToString(CultureInfo.InvariantCulture) ?? "";
        CompetitionHomologationStatus = "Homologation applied to the editor. Save competition to keep it; course fields remain editable.";
    }
}
