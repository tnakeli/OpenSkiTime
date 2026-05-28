namespace OpenSkiTime.Desktop.Models;

public sealed class ImportPasteSession
{
    public ImportPasteSession(long snapshotVersion)
    {
        SnapshotVersion = snapshotVersion;
        IsActive = true;
    }

    public long SnapshotVersion { get; }
    public bool IsActive { get; private set; }

    public void Complete() => IsActive = false;
}
