using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Client;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    // Publisher keys stay in this Windows user's Credential Manager, never in the series file or preferences.
    internal Func<string, ICredentialStore> LiveCredentialStore { get; set; } = target => new WindowsCredentialStore(target);
    [ObservableProperty] private string _livePublisherKeyInput = "";
    [ObservableProperty] private string _livePublisherKeyStatus = "";

    partial void OnLiveCloudEndpointChanged(string value) => RefreshLivePublisherKeyStatus();

    internal void RefreshLivePublisherKeyStatus()
    {
        try
        {
            LivePublisherKeyStatus = LiveCredentialStore(PublisherKeyCredential.Target(LiveCloudEndpoint)).Exists()
                ? "Publisher key saved for this server and Windows user." : "No publisher key saved for this server.";
        }
        catch (Exception ex) when (ex is LiveValidationException or IOException or PlatformNotSupportedException)
        { LivePublisherKeyStatus = ex is LiveValidationException ? ex.Message : "Credential store unavailable."; }
    }

    [RelayCommand]
    private void SaveLivePublisherKey()
    {
        try
        {
            var key = LivePublisherKeyInput.Trim();
            PublisherKeyCredential.Validate(key);
            LiveCredentialStore(PublisherKeyCredential.Target(LiveCloudEndpoint)).Save(key);
            LivePublisherKeyInput = "";
            RefreshLivePublisherKeyStatus();
            SetStatus("Live timing publisher key saved in Windows Credential Manager. Restart Cloud publishing to use it.");
        }
        catch (Exception ex) when (ex is LiveValidationException or ArgumentException or IOException or PlatformNotSupportedException)
        { SetStatus(ex.Message, error: true); }
    }

    [RelayCommand]
    private void RemoveLivePublisherKey()
    {
        try
        {
            LiveCredentialStore(PublisherKeyCredential.Target(LiveCloudEndpoint)).Remove();
            LivePublisherKeyInput = "";
            RefreshLivePublisherKeyStatus();
            SetStatus("Live timing publisher key removed from Windows Credential Manager.");
        }
        catch (Exception ex) when (ex is LiveValidationException or IOException or PlatformNotSupportedException)
        { SetStatus(ex.Message, error: true); }
    }
}
