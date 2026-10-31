using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

/// <summary>
/// Classification of one competitor after the run. The Timing view opens it while
/// <see cref="MainViewModel.ShowTimingClassificationEditor"/> is set and clears the flag when the operator closes it.
/// </summary>
public sealed partial class TimingClassificationDialog : Window
{
    private MainViewModel? _viewModel;

    public static AutoCompleteFilterPredicate<object?> CompetitorFilter { get; } =
        (search, item) => item is TimingGridRow row && MainViewModel.MatchesTimingCompetitor(row, search);

    public TimingClassificationDialog()
    {
        AvaloniaXamlLoader.Load(this);
        var competitor = this.FindControl<AutoCompleteBox>("TimingClassificationCompetitor")!;
        competitor.AddHandler(KeyDownEvent, ChooseCompetitor, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnDialogKey);
        DataContextChanged += (_, _) => Watch(DataContext as MainViewModel);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => FocusCompetitor(selectAll: true), DispatcherPriority.Input);
        Closed += (_, _) => Watch(null);
    }

    private void Watch(MainViewModel? vm)
    {
        if (_viewModel is not null) { _viewModel.PropertyChanged -= ShowStatus; }
        _viewModel = vm;
        if (vm is not null) { vm.PropertyChanged += ShowStatus; }
    }

    // Only messages raised while the dialog is open are shown here (a save, a lookup that found nobody).
    private void ShowStatus(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.StatusMessage) or nameof(MainViewModel.IsError)) || _viewModel is not { } vm) { return; }
        var message = this.FindControl<TextBlock>("TimingClassificationMessage")!;
        message.Text = vm.StatusMessage;
        message.IsVisible = vm.StatusMessage.Length > 0;
        message.Foreground = (Avalonia.Media.IBrush?)this.FindResource(vm.IsError ? "ErrorBrush" : "MutedTextBrush");
    }

    private void ChooseCompetitor(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _viewModel is not { } vm || sender is not AutoCompleteBox box) { return; }
        e.Handled = true;
        box.IsDropDownOpen = false;
        if (vm.ChooseTimingClassificationCompetitor(box.Text))
        {
            vm.TimingReason = "";
            this.FindControl<ComboBox>("TimingClassificationChoice")!.Focus();
        }
    }

    private async void OnDialogKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        // Enter in a text field saves; the competitor box handles its own Enter (choose the competitor).
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _viewModel is not { } vm
            || e.Source is not TextBox box || box.GetVisualAncestors().OfType<AutoCompleteBox>().Any()) { return; }
        e.Handled = true;
        await vm.SaveTimingClassificationCommand.ExecuteAsync(null);
        if (!vm.IsError) { FocusCompetitor(selectAll: true); }
    }

    private void FocusCompetitor(bool selectAll)
    {
        var box = this.FindControl<AutoCompleteBox>("TimingClassificationCompetitor")!;
        box.Focus();
        if (selectAll && box.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } text) { text.SelectAll(); }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();
}
