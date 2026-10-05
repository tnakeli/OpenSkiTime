namespace OpenSkiTime.Timing;

public enum BackupClockHealth { NotConfigured, Ok, WaitingForB, MissingSignal, TimeDifferenceWarning, DeviceUnavailable }

public sealed record BackupClockStatus(BackupClockHealth Health, string Text)
{
    public bool IsProblem => Health is BackupClockHealth.MissingSignal or BackupClockHealth.TimeDifferenceWarning
        or BackupClockHealth.DeviceUnavailable;

    // Answers one operator question: is B behaving approximately as expected? It reads a comparison snapshot only
    // and has no path back to A timing.
    public static BackupClockStatus Evaluate(bool configured, bool connected, string? fault, BackupMonitorSnapshot? comparison)
    {
        if (!configured) { return new(BackupClockHealth.NotConfigured, "B Clock · not configured"); }
        if (!connected || !string.IsNullOrWhiteSpace(fault))
        { return new(BackupClockHealth.DeviceUnavailable, "B Clock · device unavailable" + (string.IsNullOrWhiteSpace(fault) ? "" : " · " + fault)); }
        var rows = comparison?.Rows ?? [];
        var missing = rows.Count(x => x.Warning.Contains("missing", StringComparison.OrdinalIgnoreCase));
        if (missing > 0) { return new(BackupClockHealth.MissingSignal, $"B Clock · missing signal ({missing})"); }
        var differences = rows.Count(x => x.Warning.Length != 0);
        if (differences > 0) { return new(BackupClockHealth.TimeDifferenceWarning, $"B Clock · time difference warning ({differences})"); }
        if (rows.Any(x => !x.IsElapsed && x.Monitored && x.ATicks is not null && x.BTicks is null))
        { return new(BackupClockHealth.WaitingForB, "B Clock · waiting for B"); }
        return new(BackupClockHealth.Ok, "B Clock · OK");
    }
}
