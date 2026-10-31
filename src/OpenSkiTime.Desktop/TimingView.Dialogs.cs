using Avalonia;
using Avalonia.Controls;

namespace OpenSkiTime.Desktop;

// Forerunner entry and the classification editor open as dialogs over the Timing view. The view model's
// ShowForerunnerEditor / ShowTimingClassificationEditor flags stay the source of truth: the view opens a dialog while its
// flag is set and the view is shown, and closing the dialog by the operator clears the flag (forerunners: cancel).
public sealed partial class TimingView
{
    private TimingClassificationDialog? _classificationDialog;
    private TimingForerunnerDialog? _forerunnerDialog;
    internal Window? ClassificationDialog => _classificationDialog;
    internal Window? ForerunnerDialog => _forerunnerDialog;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) { SyncDialogs(); }
    }

    private void SyncDialogs()
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        var shown = _viewModel is not null && owner is not null && IsVisible;
        _classificationDialog = SyncDialog(_classificationDialog, owner, shown && _viewModel!.ShowTimingClassificationEditor,
            () => new TimingClassificationDialog(), vm => vm.ShowTimingClassificationEditor = false);
        _forerunnerDialog = SyncDialog(_forerunnerDialog, owner, shown && _viewModel!.ShowForerunnerEditor,
            () => new TimingForerunnerDialog(), vm => { if (vm.ShowForerunnerEditor) { vm.CancelTimingForerunnersCommand.Execute(null); } });
    }

    private T? SyncDialog<T>(T? dialog, Window? owner, bool open, Func<T> create, Action<MainViewModel> closedByOperator) where T : Window
    {
        if (open && dialog is null && owner is not null)
        {
            var created = create();
            created.DataContext = _viewModel;
            created.Closed += (_, _) =>
            {
                if (ReferenceEquals(_classificationDialog, created)) { _classificationDialog = null; }
                if (ReferenceEquals(_forerunnerDialog, created)) { _forerunnerDialog = null; }
                // A dialog the view closes is detached first; only the operator's close changes the editor state.
                var vm = created.DataContext as MainViewModel;
                // Detach so a closed dialog's two-way bindings can never write into the next dialog's state.
                created.DataContext = null;
                if (vm is not null) { closedByOperator(vm); }
            };
            _ = created.ShowDialog(owner);
            return created;
        }
        if (!open && dialog is not null)
        {
            dialog.DataContext = null;
            dialog.Close();
            return null;
        }
        return dialog;
    }

    private void CloseDialogs()
    {
        foreach (Window? dialog in new Window?[] { _classificationDialog, _forerunnerDialog })
        {
            if (dialog is null) { continue; }
            dialog.DataContext = null;
            dialog.Close();
        }
        _classificationDialog = null;
        _forerunnerDialog = null;
    }
}
