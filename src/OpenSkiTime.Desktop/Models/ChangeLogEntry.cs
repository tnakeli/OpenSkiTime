namespace OpenSkiTime.Desktop.Models;

public enum ChangeOp { Add, Edit, Delete }

public sealed record ChangeLogEntry(
    Guid Id,
    DateTime Timestamp,
    ChangeOp OperationType,
    Guid? CompetitorId,
    string? FieldName,
    string? BeforeValue,
    string? AfterValue,
    string DisplayLabel);
