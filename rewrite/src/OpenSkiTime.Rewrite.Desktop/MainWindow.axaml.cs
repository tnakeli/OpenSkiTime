using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Rewrite.Desktop;

public partial class MainWindow : Window
{
    public MainWindow() => AvaloniaXamlLoader.Load(this);

    private void Navigation_Click(object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<ScrollViewer>("WorkspaceScroll") is { } scroll)
        {
            scroll.Offset = Vector.Zero;
        }
    }

    private async void CompetitorGrid_RowEditEnded(object? sender, DataGridRowEditEndedEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Commit && e.Row.DataContext is CompetitorGridRow row
            && DataContext is MainViewModel viewModel)
        {
            await viewModel.SaveCompetitorRowAsync(row);
        }
    }

    private async void Participation_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox { DataContext: CompetitionEntryChoice choice }
            && DataContext is MainViewModel viewModel)
        {
            await viewModel.SaveParticipationChoiceAsync(choice);
        }
    }

    private void DatePickerButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
        {
            return;
        }

        var inputName = button.Name switch
        {
            "SeriesStartDatePickerButton" => "SeriesStartDateInput",
            "SeriesEndDatePickerButton" => "SeriesEndDateInput",
            "CompetitionDatePickerButton" => "CompetitionDateInput",
            _ => null
        };
        if (inputName is null || this.FindControl<TextBox>(inputName) is not { } input)
        {
            return;
        }

        var picker = new DatePicker
        {
            DayFormat = "dd",
            MonthFormat = "MM MMMM",
            YearFormat = "yyyy",
            MinWidth = 310
        };
        if (DateTime.TryParseExact(input.Text, ["dd.MM.yyyy", "d.M.yyyy"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            picker.SelectedDate = new DateTimeOffset(date);
        }

        var flyout = new Flyout { Content = picker };
        button.Flyout = flyout;
        picker.SelectedDateChanged += (_, _) =>
        {
            if (picker.SelectedDate is { } selected)
            {
                input.Text = selected.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
                flyout.Hide();
            }
        };
        flyout.ShowAt(button);
    }
}
