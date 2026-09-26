namespace OpenSkiTime.Import;

/// <summary>
/// The result of running the diff engine against a snapshot and a set of
/// parsed import rows. The importer UI presents this to the user before
/// calling <c>ImportApplyService</c>.
/// </summary>
public sealed class ImportDiff
{
    public ImportDiff(
        IReadOnlyList<NewCompetitorDiff> newCompetitors,
        IReadOnlyList<BibAssignmentDiff> bibAssignments,
        IReadOnlyList<ParticipationDiff> participations,
        IReadOnlyList<FieldChangeDiff> fieldChanges,
        IReadOnlyList<ImportRowWarning> warnings)
    {
        NewCompetitors = newCompetitors;
        BibAssignments = bibAssignments;
        Participations = participations;
        FieldChanges = fieldChanges;
        Warnings = warnings;
    }

    /// <summary>Competitors that do not exist yet and will be added.</summary>
    public IReadOnlyList<NewCompetitorDiff> NewCompetitors { get; }

    /// <summary>Bib-number assignments that will be applied or changed.</summary>
    public IReadOnlyList<BibAssignmentDiff> BibAssignments { get; }

    /// <summary>Participation flags that will be set (IsParticipating toggled).</summary>
    public IReadOnlyList<ParticipationDiff> Participations { get; }

    /// <summary>Field-level changes for existing competitors (e.g. NationCode FIN→NOR).</summary>
    public IReadOnlyList<FieldChangeDiff> FieldChanges { get; }

    /// <summary>Non-fatal row-level warnings (e.g. unresolved columns, bad year).</summary>
    public IReadOnlyList<ImportRowWarning> Warnings { get; }

    public bool HasChanges =>
        NewCompetitors.Count > 0 ||
        BibAssignments.Count > 0 ||
        Participations.Count > 0 ||
        FieldChanges.Count > 0;
}

public sealed record NewCompetitorDiff(
    string LastName,
    string FirstName,
    int YearOfBirth,
    string? FisCode,
    string? NationCode,
    string? ClubName,
    int? BibNumber,
    IReadOnlyList<string> ParticipatingIn);

public sealed record BibAssignmentDiff(
    Guid CompetitorId,
    string LastName,
    string FirstName,
    int? OldBib,
    int NewBib);

public sealed record ParticipationDiff(
    Guid CompetitorId,
    string LastName,
    string FirstName,
    Guid CompetitionId,
    string CompetitionShortLabel,
    bool IsParticipating);

/// <summary>A single field value change for an existing competitor.</summary>
public sealed record FieldChangeDiff(
    Guid CompetitorId,
    string FieldName,
    string OldValue,
    string NewValue);

public sealed record ImportRowWarning(int RowIndex, string Message);
