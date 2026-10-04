using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenSkiTime.Desktop;

// One ALGE Results account used by one or more timing roles (for example a club's account for an intermediate).
// The password stays in memory unless Remember password is selected, which uses this Windows user's Credential Manager.
public sealed partial class AlgeAccountEditor : ObservableObject
{
    public AlgeAccountEditor(string username) { Username = username; _rememberPassword = HasSavedPassword; }
    public string Username { get; }
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _rememberPassword;

    private WindowsCredentialStore Credential => new("OpenSkiTime.ALGE.Results.Password:" + Username);
    public bool HasSavedPassword => OperatingSystem.IsWindows() && Credential.Exists();
    public string PasswordWatermark => HasSavedPassword ? "Password (empty = saved password)" : "Password";

    // The password for connecting: the typed one, otherwise the remembered one. Applies the Remember choice.
    public string TakePassword()
    {
        var password = Password;
        if (password.Length == 0 && OperatingSystem.IsWindows()) { password = Credential.Read() ?? ""; }
        if (OperatingSystem.IsWindows()) { if (RememberPassword && password.Length != 0) { Credential.Save(password); } else if (!RememberPassword) { Credential.Remove(); } }
        Password = "";
        OnPropertyChanged(nameof(HasSavedPassword)); OnPropertyChanged(nameof(PasswordWatermark));
        return password;
    }
}
