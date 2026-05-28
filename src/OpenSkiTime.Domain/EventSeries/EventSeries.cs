using OpenSkiTime.Domain.CategoryRules;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Competitors;

namespace OpenSkiTime.Domain.Series;

/// <summary>
/// A race weekend or week. Aggregate root that owns its <see cref="Competitions"/>
/// (and, in later phases, Competitors, Participations, CategoryRules).
/// </summary>
public class EventSeries
{
    private readonly List<Competition> _competitions = [];
    private readonly List<Competitor> _competitors = [];
    private readonly List<CategoryRule> _categoryRules = [];

    /// <summary>EF Core constructor. Do not call from production code.</summary>
    private EventSeries() { }

    private EventSeries(
        Guid id,
        string name,
        string location,
        string organizer,
        DateOnly startDate,
        DateOnly endDate,
        string nation,
        string season,
        DateTime createdAt)
    {
        Id = id;
        Name = name;
        Location = location;
        Organizer = organizer;
        StartDate = startDate;
        EndDate = endDate;
        Nation = nation;
        Season = season;
        CreatedAt = createdAt;
        RowVersion = 1;
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = null!;

    public string Location { get; private set; } = null!;

    public string Organizer { get; private set; } = null!;

    public DateOnly StartDate { get; private set; }

    public DateOnly EndDate { get; private set; }

    /// <summary>ISO 3-letter nation code (e.g., FIN, ITA).</summary>
    public string Nation { get; private set; } = null!;

    /// <summary>Season label (e.g., 2025/26).</summary>
    public string Season { get; private set; } = null!;

    public DateTime CreatedAt { get; private set; }

    /// <summary>
    /// Monotonic counter incremented by <c>OpenSkiTimeDbContext.SaveChangesAsync</c>
    /// whenever any entity within this series is added/updated/removed in the
    /// current change set. Used as the import-preview snapshot version.
    /// </summary>
    public long RowVersion { get; private set; }

    /// <summary>
    /// Persistence hook: bump the snapshot version. Called from
    /// <c>OpenSkiTimeDbContext.SaveChangesAsync</c>; not for production
    /// domain code.
    /// </summary>
    public void IncrementRowVersion() => RowVersion += 1;

    public IReadOnlyList<Competition> Competitions => _competitions;

    public IReadOnlyList<Competitor> Competitors => _competitors;

    public IReadOnlyList<CategoryRule> CategoryRules => _categoryRules;

    /// <summary>
    /// Factory enforcing the spec's required-field rules for Event Series
    /// basic data (FR-002) and the EndDate &gt;= StartDate invariant.
    /// </summary>
    public static EventSeries Create(
        Guid id,
        string name,
        string location,
        string organizer,
        DateOnly startDate,
        DateOnly endDate,
        string nation,
        string season,
        DateTime createdAtUtc)
    {
        ValidateName(name);
        ValidateLocation(location);
        ValidateOrganizer(organizer);
        ValidateDates(startDate, endDate);
        ValidateNation(nation);
        ValidateSeason(season);

        return new EventSeries(
            id == Guid.Empty ? Guid.NewGuid() : id,
            name.Trim(),
            location.Trim(),
            organizer.Trim(),
            startDate,
            endDate,
            nation.Trim().ToUpperInvariant(),
            season.Trim(),
            createdAtUtc);
    }

    public void Rename(string newName)
    {
        ValidateName(newName);
        Name = newName.Trim();
    }

    public void UpdateBasicData(
        string name,
        string location,
        string organizer,
        DateOnly startDate,
        DateOnly endDate,
        string nation,
        string season)
    {
        ValidateName(name);
        ValidateLocation(location);
        ValidateOrganizer(organizer);
        ValidateDates(startDate, endDate);
        ValidateNation(nation);
        ValidateSeason(season);

        Name = name.Trim();
        Location = location.Trim();
        Organizer = organizer.Trim();
        StartDate = startDate;
        EndDate = endDate;
        Nation = nation.Trim().ToUpperInvariant();
        Season = season.Trim();
    }

    public void AddCompetition(Competition competition)
    {
        ArgumentNullException.ThrowIfNull(competition);

        if (competition.EventSeriesId != Id)
        {
            throw new InvalidOperationException(
                "Competition's EventSeriesId does not match this Event Series.");
        }

        if (_competitions.Any(c => string.Equals(
                c.ShortLabel, competition.ShortLabel, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"A competition with short label '{competition.ShortLabel}' already exists in this Event Series.");
        }

        _competitions.Add(competition);
    }

    public bool RemoveCompetition(Guid competitionId)
    {
        var c = _competitions.FirstOrDefault(x => x.Id == competitionId);
        if (c is null)
        {
            return false;
        }

        return _competitions.Remove(c);
    }

    // ── Competitor management ─────────────────────────────────────────────────

    /// <summary>
    /// Adds a competitor to this series. Enforces that the competitor belongs
    /// to this series and that no duplicate (same last name + first name +
    /// year-of-birth) already exists.
    /// </summary>
    public void AddCompetitor(Competitor competitor)
    {
        ArgumentNullException.ThrowIfNull(competitor);

        if (competitor.EventSeriesId != Id)
        {
            throw new InvalidOperationException(
                "Competitor's EventSeriesId does not match this Event Series.");
        }

        var duplicate = _competitors.FirstOrDefault(c =>
            c.LastName.Value == competitor.LastName.Value &&
            string.Equals(c.FirstName, competitor.FirstName, StringComparison.OrdinalIgnoreCase) &&
            c.YearOfBirth == competitor.YearOfBirth);

        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"A competitor '{competitor.LastName} {competitor.FirstName}' " +
                $"born {competitor.YearOfBirth} already exists in this Event Series.");
        }

        _competitors.Add(competitor);
    }

    /// <summary>
    /// Removes a competitor and returns <c>true</c> if found.
    /// The caller (use case) is responsible for removing orphan Participations.
    /// </summary>
    public bool RemoveCompetitor(Guid competitorId)
    {
        var c = _competitors.FirstOrDefault(x => x.Id == competitorId);
        if (c is null)
        {
            return false;
        }

        return _competitors.Remove(c);
    }

    /// <summary>
    /// Assigns a bib to a competitor. Enforces uniqueness within the series.
    /// Pass <c>null</c> to clear the bib.
    /// </summary>
    public void AssignBib(Guid competitorId, int? bib)
    {
        var competitor = _competitors.FirstOrDefault(c => c.Id == competitorId)
            ?? throw new InvalidOperationException(
                $"Competitor {competitorId} not found in this Event Series.");

        if (bib.HasValue)
        {
            var conflict = _competitors.FirstOrDefault(c =>
                c.Id != competitorId && c.BibNumber == bib.Value);

            if (conflict is not null)
            {
                throw new InvalidOperationException(
                    $"Bib {bib.Value} is already assigned to " +
                    $"'{conflict.LastName} {conflict.FirstName}'.");
            }
        }

        competitor.SetBib(bib);
    }

    // ── Category rule management ──────────────────────────────────────────────

    public void AddCategoryRule(CategoryRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.EventSeriesId != Id)
        {
            throw new InvalidOperationException(
                "CategoryRule's EventSeriesId does not match this Event Series.");
        }

        _categoryRules.Add(rule);
    }

    public bool RemoveCategoryRule(Guid ruleId)
    {
        var r = _categoryRules.FirstOrDefault(x => x.Id == ruleId);
        if (r is null)
        {
            return false;
        }

        return _categoryRules.Remove(r);
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Event Series name is required.", nameof(name));
        }
    }

    private static void ValidateLocation(string location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            throw new ArgumentException("Location/resort is required.", nameof(location));
        }
    }

    private static void ValidateOrganizer(string organizer)
    {
        if (string.IsNullOrWhiteSpace(organizer))
        {
            throw new ArgumentException("Organizer is required.", nameof(organizer));
        }
    }

    private static void ValidateDates(DateOnly startDate, DateOnly endDate)
    {
        if (endDate < startDate)
        {
            throw new ArgumentException("EndDate must be on or after StartDate.", nameof(endDate));
        }
    }

    private static void ValidateNation(string nation)
    {
        if (string.IsNullOrWhiteSpace(nation) || nation.Trim().Length != 3)
        {
            throw new ArgumentException(
                "Nation must be a 3-letter ISO code (e.g., FIN, ITA).",
                nameof(nation));
        }
    }

    private static void ValidateSeason(string season)
    {
        if (string.IsNullOrWhiteSpace(season))
        {
            throw new ArgumentException("Season is required (e.g., 2025/26).", nameof(season));
        }
    }
}
