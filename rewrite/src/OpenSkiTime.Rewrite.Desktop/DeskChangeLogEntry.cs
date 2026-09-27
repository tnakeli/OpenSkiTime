namespace OpenSkiTime.Rewrite.Desktop;

public enum DeskChangeKind { Add, Edit, Entry, Delete }

public sealed record DeskChangeLogEntry(Guid LocalRowId, DeskChangeKind Kind,
    string? Field, Guid? CompetitionId, string DisplayLabel);
