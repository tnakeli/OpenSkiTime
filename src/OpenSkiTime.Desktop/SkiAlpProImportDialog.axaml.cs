using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace OpenSkiTime.Desktop;

/// <summary>Statistics of an opened Ski-Alp-Pro file and the choices of its test import. Closes when the import is saved.</summary>
public sealed partial class SkiAlpProImportDialog : Window
{
    private SkiAlpProImportViewModel? _viewModel;

    public SkiAlpProImportDialog()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Escape && !e.Handled) { e.Handled = true; Close(); } });
        Closing += (_, e) => { if (_viewModel is { IsBusy: true }) { e.Cancel = true; } };
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelChanged; }
            _viewModel = DataContext as SkiAlpProImportViewModel;
            if (_viewModel is not null) { _viewModel.PropertyChanged += OnViewModelChanged; }
        };
    }

    // The import is done and no longer busy: the busy flag clears last.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SkiAlpProImportViewModel.IsBusy) && _viewModel is { IsDone: true, IsBusy: false }) { Close(); }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
