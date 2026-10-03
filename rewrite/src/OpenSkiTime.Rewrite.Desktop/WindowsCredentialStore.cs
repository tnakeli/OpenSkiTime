namespace OpenSkiTime.Rewrite.Desktop;

public interface ICredentialStore
{
    bool Exists();
    string? Read();
    void Save(string key);
    void Remove();
}

public sealed class WindowsCredentialStore(string target) : ICredentialStore
{
    private readonly OpenSkiTime.LiveTiming.Client.WindowsCredentialStore _store = new(target);
    public bool Exists() => _store.Exists();
    public string? Read() => _store.Read();
    public void Save(string key) => _store.Save(key);
    public void Remove() => _store.Remove();
}
