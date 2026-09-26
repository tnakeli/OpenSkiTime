using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace OpenSkiTime.Rewrite.Desktop;

public interface IFileDialogs
{
    Task<string?> ChooseNewAsync(string suggestedName);
    Task<string?> ChooseOpenAsync();
    Task<string?> ChooseBackupAsync(string suggestedName);
    Task<string?> ChooseLegacyDatabaseAsync();
    Task<bool> ConfirmRemoveAsync(string competitionName);
    Task<bool> ConfirmRemoveCompetitorAsync(string surname);
}

public sealed class AvaloniaFileDialogs(Window owner) : IFileDialogs
{
    private static readonly FilePickerFileType s_seriesType = new("OpenSkiTime event series")
    {
        Patterns = ["*.ost"],
    };

    public async Task<string?> ChooseNewAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Create event series file", SuggestedFileName = suggestedName + ".ost",
            DefaultExtension = "ost", FileTypeChoices = [s_seriesType],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> ChooseOpenAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open event series file", AllowMultiple = false,
            FileTypeFilter = [s_seriesType],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> ChooseBackupAsync(string suggestedName)
    {
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save portable backup", SuggestedFileName = suggestedName + "-backup.ost",
            DefaultExtension = "ost", FileTypeChoices = [s_seriesType],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<string?> ChooseLegacyDatabaseAsync()
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Preview legacy OpenSkiTime database", AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SQLite database")
            {
                Patterns = ["*.db", "*.sqlite", "*.sqlite3", "*.ost"],
            }],
        });
        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public Task<bool> ConfirmRemoveAsync(string competitionName)
        => ConfirmAsync("competition", competitionName);

    public Task<bool> ConfirmRemoveCompetitorAsync(string surname)
        => ConfirmAsync("competitor", surname);

    private async Task<bool> ConfirmAsync(string kind, string name)
    {
        var prompt = new Window
        {
            Title = $"Remove {kind}", Width = 360, Height = 150,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var remove = new Button { Content = "Remove", Classes = { "dangerAction" } };
        var cancel = new Button { Content = "Cancel", Classes = { "secondaryAction" } };
        remove.Click += (_, _) => prompt.Close(true);
        cancel.Click += (_, _) => prompt.Close(false);
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18), Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"Remove {kind} '{name}' from this series?", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, remove } },
            },
        };
        return await prompt.ShowDialog<bool>(owner);
    }
}
