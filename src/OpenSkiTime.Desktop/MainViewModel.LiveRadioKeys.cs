using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.LiveTiming.Client;

namespace OpenSkiTime.Desktop;

public sealed record LiveRadioKeyChoice(string Provider, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel
{
    // Race radio provider keys stay in this Windows user's Credential Manager, never in the series file, preferences or logs.
    public LiveRadioKeyChoice[] LiveRadioKeyChoices { get; } = RadioKeyCredential.Providers.Select(x => new LiveRadioKeyChoice(x, RadioKeyCredential.Label(x))).ToArray();
    [ObservableProperty] private LiveRadioKeyChoice? _liveRadioKeyProvider;
    [ObservableProperty] private string _liveRadioKeyInput = "";
    [ObservableProperty] private string _liveRadioKeyStatus = "";

    partial void OnLiveRadioKeyProviderChanged(LiveRadioKeyChoice? value) => RefreshLiveRadioKeyStatus();

    internal void RefreshLiveRadioKeyStatus()
    {
        if (LiveRadioKeyProvider is not { } provider) { LiveRadioKeyStatus = ""; return; }
        try
        {
            LiveRadioKeyStatus = LiveCredentialStore(RadioKeyCredential.Target(provider.Provider)).Exists()
                ? $"{provider.Label} key saved for this Windows user." : $"No {provider.Label} key saved.";
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { LiveRadioKeyStatus = "Credential store unavailable."; }
    }

    [RelayCommand]
    private void SaveLiveRadioKey()
    {
        if (LiveRadioKeyProvider is not { } provider) { SetStatus("Choose a radio provider.", error: true); return; }
        try
        {
            var key = LiveRadioKeyInput.Trim();
            RadioKeyCredential.Validate(key);
            LiveCredentialStore(RadioKeyCredential.Target(provider.Provider)).Save(key);
            LiveRadioKeyInput = "";
            RefreshLiveRadioKeyStatus();
            SetStatus($"{provider.Label} key saved in Windows Credential Manager. Save the radio settings in the live timing panel to use it.");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException) { SetStatus(ex.Message, error: true); }
    }

    [RelayCommand]
    private void RemoveLiveRadioKey()
    {
        if (LiveRadioKeyProvider is not { } provider) { return; }
        try
        {
            LiveCredentialStore(RadioKeyCredential.Target(provider.Provider)).Remove();
            LiveRadioKeyInput = "";
            RefreshLiveRadioKeyStatus();
            SetStatus($"{provider.Label} key removed from Windows Credential Manager.");
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException) { SetStatus(ex.Message, error: true); }
    }
}
