using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private string _reportAuxiliaryClockWarning = "";
}
