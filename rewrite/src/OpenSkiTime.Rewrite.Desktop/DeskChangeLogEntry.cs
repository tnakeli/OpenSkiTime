using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public enum DeskChangeKind { Add, Edit, Delete }

public sealed record DeskChangeLogEntry(Guid Id, DateTime Timestamp, DeskChangeKind Kind,
    Guid CompetitorId, CompetitorValues? PreviousValues, Guid? CompetitionId,
    bool PreviousParticipation, int? PreviousImportedBib, string DisplayLabel);
