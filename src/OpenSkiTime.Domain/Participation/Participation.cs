namespace OpenSkiTime.Domain.Participation;

/// <summary>
/// Links a <see cref="Competitors.Competitor"/> to a <see cref="Competitions.Competition"/>
/// and records whether the athlete is entered (IsParticipating) and the
/// optional start order within that race.
/// Created by the importer or manually by the race office.
/// </summary>
public class Participation
{
    /// <summary>EF Core constructor. Do not call from production code.</summary>
    private Participation() { }

    private Participation(
        Guid id,
        Guid competitorId,
        Guid competitionId,
        bool isParticipating,
        int? startOrder)
    {
        Id = id;
        CompetitorId = competitorId;
        CompetitionId = competitionId;
        IsParticipating = isParticipating;
        StartOrder = startOrder;
    }

    public Guid Id { get; private set; }

    public Guid CompetitorId { get; private set; }

    public Guid CompetitionId { get; private set; }

    /// <summary>
    /// True when the competitor is entered in this race.
    /// False means "present in series but not in this race" — still persisted
    /// so the grid can render a ✗ rather than a blank.
    /// </summary>
    public bool IsParticipating { get; private set; }

    /// <summary>Optional start number within this competition (1-based). Null = not yet drawn.</summary>
    public int? StartOrder { get; private set; }

    public static Participation Create(
        Guid id,
        Guid competitorId,
        Guid competitionId,
        bool isParticipating,
        int? startOrder = null)
    {
        if (competitorId == Guid.Empty)
        {
            throw new ArgumentException("CompetitorId is required.", nameof(competitorId));
        }

        if (competitionId == Guid.Empty)
        {
            throw new ArgumentException("CompetitionId is required.", nameof(competitionId));
        }

        if (startOrder.HasValue && startOrder.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(startOrder), "Start order must be ≥ 1.");
        }

        return new Participation(
            id == Guid.Empty ? Guid.NewGuid() : id,
            competitorId,
            competitionId,
            isParticipating,
            startOrder);
    }

    public void SetParticipating(bool participating) => IsParticipating = participating;

    public void SetStartOrder(int? order)
    {
        if (order.HasValue && order.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(order), "Start order must be ≥ 1.");
        }

        StartOrder = order;
    }
}
