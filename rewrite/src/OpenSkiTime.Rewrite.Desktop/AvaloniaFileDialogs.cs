using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace OpenSkiTime.Rewrite.Desktop;

public interface IFileDialogs
{
    Task<string?> ChooseNewAsync(string suggestedName);
    Task<string?> ChooseOpenAsync();
    Task<string?> ChooseBackupAsync(string suggestedName);
    Task<bool> ConfirmRemoveAsync(string competitionName);
    Task<bool> ConfirmDiscardChangesAsync(int changeCount);
    Task<string?> ChooseStartListExportAsync(string suggestedName, bool print);
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

    public Task<bool> ConfirmRemoveAsync(string competitionName)
        => ConfirmAsync("Remove competition", $"Remove competition '{competitionName}' from this series?",
            "Remove", "Cancel");

    public async Task<string?> ChooseStartListExportAsync(string suggestedName, bool print)
    {
        var extension = print ? "html" : "tsv";
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = print ? "Save printable start list (open in a browser to print)" : "Export start list",
            SuggestedFileName = suggestedName + "." + extension, DefaultExtension = extension,
            FileTypeChoices = [new FilePickerFileType(print ? "Printable HTML" : "Tab-separated values") { Patterns = ["*." + extension] }],
        });
        return file?.TryGetLocalPath();
    }

    public Task<bool> ConfirmDiscardChangesAsync(int changeCount)
        => ConfirmAsync("Discard all changes",
            $"Discard all {changeCount} unsaved changes? This includes edits and pasted data made since the last save.",
            "Discard all", "Keep changes");

    private async Task<bool> ConfirmAsync(string title, string message, string actionLabel, string cancelLabel)
    {
        var prompt = new Window
        {
            Title = title, Width = 440, Height = 170,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var action = new Button { Content = actionLabel, Classes = { "dangerAction" } };
        var cancel = new Button { Content = cancelLabel, Classes = { "secondaryAction" } };
        action.Click += (_, _) => prompt.Close(true);
        cancel.Click += (_, _) => prompt.Close(false);
        prompt.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(18), Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Children = { cancel, action } },
            },
        };
        return await prompt.ShowDialog<bool>(owner);
    }
}
