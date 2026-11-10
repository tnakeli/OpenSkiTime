using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using OpenSkiTime.Application;

namespace OpenSkiTime.Desktop;

public sealed partial class AuditTrailView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _showingBundle;
    public AuditTrailView()
    {
        AvaloniaXamlLoader.Load(this);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            { this.FindControl<TextBox>("AuditSearchBox")?.Focus(); e.Handled = true; }
        });
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) { _viewModel.PropertyChanged -= OnViewModelChanged; }
            _viewModel = DataContext as MainViewModel;
            if (_viewModel is not null) { _viewModel.PropertyChanged += OnViewModelChanged; }
        };
    }
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsDebugBundleReviewOpen) && _viewModel is { IsDebugBundleReviewOpen: true } vm && !_showingBundle)
        { OpenBundleDialog(vm); }
    }
    private async void OpenBundleDialog(MainViewModel vm)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) { return; }
        _showingBundle = true;
        var dialog = new DebugBundleDialog { DataContext = vm };
        try { await dialog.ShowDialog(owner); }
        finally { _showingBundle = false; vm.CloseDebugBundleReviewCommand.Execute(null); }
    }
    private void Search_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MainViewModel vm) { vm.RefreshAuditCommand.Execute(null); e.Handled = true; }
    }
    private async void CopyDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        { await clipboard.SetTextAsync(vm.IsAuditDiagnostics ? vm.AuditEnvironment : vm.AuditDetails); }
    }
    private void SourceLink_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AuditReference reference } && DataContext is MainViewModel vm)
        { vm.LoadAuditDetailCommand.Execute(reference); }
    }
}
