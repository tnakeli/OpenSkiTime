using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Desktop.Shell;
using OpenSkiTime.Desktop.ViewModels;
using OpenSkiTime.Desktop.Views;

[assembly: AvaloniaTestApplication(typeof(OpenSkiTime.Desktop.Tests.HeadlessAppBuilder))]

namespace OpenSkiTime.Desktop.Tests;

public sealed class HeadlessAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public class HeadlessGridReferenceTests
{
    [AvaloniaFact]
    public async Task Shell_forms_navigate_and_save_series_and_competition()
    {
        await using var fixture = new LegacyGridFixture();
        await fixture.InitializeAsync();
        var shell = fixture.Services.GetRequiredService<ShellViewModel>();
        var window = new ShellWindow { DataContext = shell };
        window.Show();
        await shell.LoadAsync();
        Assert.IsType<EventSeriesOverviewViewModel>(shell.CurrentContent);

        Click(window, "+ New Event Series");
        var overview = shell.Overview;
        Assert.True(overview.IsCreatingNew);
        overview.Name = "UI Series";
        overview.Location = "Levi";
        overview.Organizer = "Test Club";
        overview.StartDate = new DateOnly(2026, 1, 10);
        overview.EndDate = new DateOnly(2026, 1, 11);
        overview.Nation = "FIN";
        overview.Season = "2025/26";
        Click(window, "Save Event Series");
        await overview.SaveSeriesCommand.ExecutionTask!;
        var repo = fixture.Services.GetRequiredService<IEventSeriesRepository>();
        var newSeries = (await repo.ListAsync()).Single(s => s.Name == "UI Series");
        Assert.Equal("UI Series", (await fixture.ReopenSeriesAsync(newSeries.Id)).Name);

        Click(window, "+ New Competition");
        var editor = Assert.IsType<CompetitionEditorViewModel>(shell.CurrentContent);
        Assert.Single(window.GetVisualDescendants().OfType<CompetitionEditorView>());
        editor.Name = "Giant Slalom";
        editor.ShortLabel = "3.2 GS";
        editor.Date = new DateOnly(2026, 1, 11);
        Click(window, "Save");
        await editor.SaveCommand.ExecutionTask!;
        var savedCompetition = Assert.Single((await fixture.ReopenSeriesAsync(newSeries.Id)).Competitions);
        Assert.Equal("3.2 GS", savedCompetition.ShortLabel);

        overview.Name = "UI Series Edited";
        Click(window, "Save Event Series");
        await overview.SaveSeriesCommand.ExecutionTask!;
        Assert.Equal("UI Series Edited", (await fixture.ReopenSeriesAsync(newSeries.Id)).Name);

        overview.EditCompetitionCommand.Execute(savedCompetition);
        editor = Assert.IsType<CompetitionEditorViewModel>(shell.CurrentContent);
        editor.Name = "Edited Giant Slalom";
        window.UpdateLayout();
        Click(window, "Save");
        await editor.SaveCommand.ExecutionTask!;
        Assert.Equal("Edited Giant Slalom", Assert.Single((await fixture.ReopenSeriesAsync(newSeries.Id)).Competitions).Name);
        Assert.Empty(fixture.Dialogs.Errors);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Competitor_tab_renders_and_paste_buttons_commit_a_simple_row()
    {
        await using var fixture = new LegacyGridFixture();
        await fixture.InitializeAsync();
        var view = new CompetitorGridView { DataContext = fixture.Grid };
        var window = new Window { Content = view, Width = 1280, Height = 800 };
        window.Show();

        var grid = view.FindControl<DataGrid>("CompetitorDataGrid");
        Assert.NotNull(grid);
        Assert.Contains(grid.Columns, column => Equals(column.Header, "3.1 SL"));

        fixture.Clipboard.Text = "Last Name\tFirst Name\tYear\nMüller\tHannes\t2007";
        Click(window, "📋 Paste from clipboard");
        await fixture.Grid.PasteCommand.ExecutionTask!;
        Assert.True(fixture.Grid.HasActivePasteSession);
        Assert.Single(fixture.Grid.Competitors);

        Click(window, "Apply import");
        await fixture.Grid.ApplyImportCommand.ExecutionTask!;
        Assert.False(fixture.Grid.HasActivePasteSession);
        Assert.Equal("MÜLLER", Assert.Single((await fixture.ReopenAsync()).Competitors).LastName.Value);

        window.UpdateLayout();
        grid.SelectedItem = Assert.Single(fixture.Grid.Competitors);
        Click(window, "📄 Copy selected");
        await fixture.Grid.CopySelectedRowsCommand.ExecutionTask!;
        Assert.Contains("MÜLLER\tHannes\t2007", fixture.Clipboard.Text);
        Assert.Empty(fixture.Dialogs.Errors);
        window.Close();
    }

    private static void Click(Window window, string content)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .Single(b => Equals(b.Content, content));
        Assert.True(button.Focus());
        window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
        window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
    }
}
