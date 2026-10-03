using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    [ObservableProperty] private string _reportAuxiliaryClockWarning = "";
}
