using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace OpenSkiTime.Desktop;

/// <summary>
/// Edit result: classification, a start/finish time of day or an EET for one competitor. The Timing view opens it while
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
        Opened += (_, _) =>
        {
            FitToScreen();
            Dispatcher.UIThread.Post(() => FocusCompetitor(selectAll: true), DispatcherPriority.Input);
        };
        Closed += (_, _) => Watch(null);
    }

    // Caps the height to the working area of the screen, so the action bar stays visible on a short display and the tab
    // content scrolls instead; a dialog that would reach below the screen moves up.
    private void FitToScreen()
    {
        if (Screens.ScreenFromWindow(this) is not { } screen) { return; }
        var area = screen.WorkingArea;
        MaxHeight = Math.Max(MinHeight, area.Height / screen.Scaling - 16);
        Dispatcher.UIThread.Post(() =>
        {
            var bottom = Position.Y + (int)Math.Ceiling(Bounds.Height * screen.Scaling);
            if (bottom > area.Bottom || Position.Y < area.Y)
            { Position = new Avalonia.PixelPoint(Position.X, Math.Max(area.Y, area.Bottom - (int)Math.Ceiling(Bounds.Height * screen.Scaling))); }
        }, DispatcherPriority.Loaded);
    }

    private void Watch(MainViewModel? vm)
    {
        if (_viewModel is not null) { _viewModel.PropertyChanged -= ShowStatus; _viewModel.EetRows.CollectionChanged -= ShowEetTarget; }
        _viewModel = vm;
        if (vm is not null) { vm.PropertyChanged += ShowStatus; vm.EetRows.CollectionChanged += ShowEetTarget; }
    }

    // The EET table lists the whole start order; keep the competitor with the missing time in view.
    private void ShowEetTarget(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems?.OfType<EetEntryRow>().FirstOrDefault(x => x.IsTarget) is not { } target) { return; }
        Dispatcher.UIThread.Post(() => this.FindControl<DataGrid>("EetReferenceGrid")?.ScrollIntoView(target, null), DispatcherPriority.Background);
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
            FocusTab(vm.TimingResultTab);
        }
    }

    // The first field of each tab, focused after choosing a competitor or switching tab with Alt+1/2/3.
    private void FocusTab(int tab)
    {
        Control? target = tab switch
        {
            1 => this.FindControl<TextBox>("TimeEntryTime"),
            2 => this.FindControl<TextBox>("EetBackupTime"),
            _ => this.FindControl<ComboBox>("TimingClassificationChoice")
        };
        Dispatcher.UIThread.Post(() => target?.Focus(), DispatcherPriority.Input);
    }

    private async void OnDialogKey(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.D1 or Key.D2 or Key.D3 && _viewModel is { } tabs)
        {
            e.Handled = true;
            tabs.TimingResultTab = e.Key - Key.D1;
            FocusTab(tabs.TimingResultTab);
            return;
        }
        // Enter in a text field saves the current tab; the competitor box handles its own Enter (choose the
        // competitor) and Enter in the EET table only commits the typed backup time.
        if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.None || _viewModel is not { } vm
            || e.Source is not TextBox box || box.GetVisualAncestors().OfType<AutoCompleteBox>().Any()
            || box.GetVisualAncestors().OfType<DataGrid>().Any()) { return; }
        e.Handled = true;
        switch (vm.TimingResultTab)
        {
            case 1:
                await vm.SaveTimeEntryCommand.ExecuteAsync(null);
                break;
            case 2:
                if (!vm.SaveEetCommand.CanExecute(null)) { return; }
                await vm.SaveEetCommand.ExecuteAsync(null);
                break;
            default:
                await vm.SaveTimingClassificationCommand.ExecuteAsync(null);
                break;
        }
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
