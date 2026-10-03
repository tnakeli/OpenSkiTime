using System.Globalization;
using Avalonia.Collections;
using Avalonia.Controls;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private DataGridCollectionView? _competitionView;
    public DataGridCollectionView CompetitionView => _competitionView ??= new(Competitions);
    internal bool IsChangingCompetitionPresentation { get; set; }
}

public partial class MainWindow
{
    private ColumnGridController<CompetitionDetails>? _competitionColumnControls;
    internal Flyout? CompetitionFilterMenu => _competitionColumnControls?.FilterMenu;

    private void InstallCompetitionColumnControls()
    {
        _competitionColumnControls?.Detach();
        if (_gridViewModel is not { } vm || this.FindControl<DataGrid>("CompetitionsGrid") is not { } grid) { return; }
        _competitionColumnControls = new(grid, vm.CompetitionView, "Competition", CompetitionColumnValue,
            changing: value => vm.IsChangingCompetitionPresentation = value);
    }

    private static object? CompetitionColumnValue(CompetitionDetails row, string key) => key switch
    {
        "Values.Date" => row.Values.Date, "Values.ShortLabel" => row.Values.ShortLabel,
        "Values.Name" => row.Values.Name, "Values.Discipline" => row.Values.Discipline.ToString(),
        "Values.FisCode" => row.Values.FisCode, "Values.Calendar.Category" => row.Values.Calendar?.Category,
        "Values.Calendar.Gender" => row.Values.Calendar?.Gender, "Values.Calendar.Location" => row.Values.Calendar?.Location,
        "Values.CourseName" => row.Values.CourseName,
        "Values" => new HomologationDisplayConverter().Convert(row.Values, typeof(string), null, CultureInfo.InvariantCulture),
        "Values.Calendar.TechnicalDelegate.LastName" => row.Values.Calendar?.TechnicalDelegate?.LastName,
        "Values.Calendar.TechnicalDelegate.Number" => row.Values.Calendar?.TechnicalDelegate?.Number,
        "Values.RunCount" => row.Values.RunCount, _ => null
    };
}
