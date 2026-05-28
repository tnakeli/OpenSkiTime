using OpenSkiTime.Domain.Common;

namespace OpenSkiTime.Domain.Competitors;

/// <summary>
/// A single athlete registered within an Event Series.
/// Competitors are owned by an Event Series (via EventSeries.AddCompetitor).
/// The bib uniqueness invariant lives on EventSeries.
/// </summary>
public class Competitor
{
    private readonly List<Participation.Participation> _participations = [];

    /// <summary>EF Core constructor. Do not call from production code.</summary>
    private Competitor() { }

    private Competitor(
        Guid id,
        Guid eventSeriesId,
        UpperCaseName lastName,
        string firstName,
        int yearOfBirth,
        string? fisCode,
        string? nationCode,
        string? clubName,
        Gender? gender,
        int? bibNumber)
    {
        Id = id;
        EventSeriesId = eventSeriesId;
        LastName = lastName;
        FirstName = firstName.Trim();
        YearOfBirth = yearOfBirth;
        FisCode = fisCode;
        NationCode = nationCode;
        ClubName = clubName;
        Gender = gender;
        BibNumber = bibNumber;
    }

    public Guid Id { get; private set; }

    public Guid EventSeriesId { get; private set; }

    /// <summary>Upper-cased last name per constitution §VII and FR-023.</summary>
    public UpperCaseName LastName { get; private set; }

    public string FirstName { get; private set; } = null!;

    public int YearOfBirth { get; private set; }

    /// <summary>Optional FIS athlete code (up to 9 chars). Null when not registered with FIS.</summary>
    public string? FisCode { get; private set; }

    /// <summary>ISO 3-letter nation code. Null when unknown.</summary>
    public string? NationCode { get; private set; }

    public string? ClubName { get; private set; }

    public Gender? Gender { get; private set; }

    /// <summary>
    /// Race bib. Null until assigned. Uniqueness within the EventSeries is
    /// enforced by <c>EventSeries.AssignBib</c>.
    /// </summary>
    public int? BibNumber { get; private set; }

    public IReadOnlyList<Participation.Participation> Participations => _participations;

    /// <summary>
    /// Factory: validates required fields and constructs the entity.
    /// </summary>
    public static Competitor Create(
        Guid id,
        Guid eventSeriesId,
        string lastName,
        string firstName,
        int yearOfBirth,
        string? fisCode = null,
        string? nationCode = null,
        string? clubName = null,
        Gender? gender = null,
        int? bibNumber = null)
    {
        if (eventSeriesId == Guid.Empty)
        {
            throw new ArgumentException("EventSeriesId is required.", nameof(eventSeriesId));
        }

        ValidateFirstName(firstName);
        ValidateYearOfBirth(yearOfBirth);

        var lastNameVo = new UpperCaseName(lastName);

        string? normalizedFis = string.IsNullOrWhiteSpace(fisCode)
            ? null
            : new FisCode(fisCode).Value;
        string? normalizedNation = string.IsNullOrWhiteSpace(nationCode)
            ? null
            : new NationCode(nationCode).Value;
        string? normalizedClub = string.IsNullOrWhiteSpace(clubName)
            ? null
            : new ClubName(clubName).Value;

        if (bibNumber.HasValue && bibNumber.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bibNumber), "Bib number must be ≥ 1.");
        }

        return new Competitor(
            id == Guid.Empty ? Guid.NewGuid() : id,
            eventSeriesId,
            lastNameVo,
            firstName,
            yearOfBirth,
            normalizedFis,
            normalizedNation,
            normalizedClub,
            gender,
            bibNumber);
    }

    /// <summary>Updates mutable personal data. Bib is managed separately via EventSeries.</summary>
    public void UpdatePersonalData(
        string lastName,
        string firstName,
        int yearOfBirth,
        string? fisCode,
        string? nationCode,
        string? clubName,
        Gender? gender)
    {
        ValidateFirstName(firstName);
        ValidateYearOfBirth(yearOfBirth);

        LastName = new UpperCaseName(lastName);
        FirstName = firstName.Trim();
        YearOfBirth = yearOfBirth;
        FisCode = string.IsNullOrWhiteSpace(fisCode) ? null : new FisCode(fisCode).Value;
        NationCode = string.IsNullOrWhiteSpace(nationCode) ? null : new NationCode(nationCode).Value;
        ClubName = string.IsNullOrWhiteSpace(clubName) ? null : new ClubName(clubName).Value;
        Gender = gender;
    }

    /// <summary>
    /// Assigns (or clears) the bib number. Uniqueness across the series
    /// is enforced by the caller (<c>EventSeries.AssignBib</c>).
    /// </summary>
    internal void SetBib(int? bib)
    {
        if (bib.HasValue && bib.Value < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(bib), "Bib number must be ≥ 1.");
        }

        BibNumber = bib;
    }

    private static void ValidateFirstName(string firstName)
    {
        if (string.IsNullOrWhiteSpace(firstName))
        {
            throw new ArgumentException("First name is required.", nameof(firstName));
        }
    }

    private static void ValidateYearOfBirth(int yearOfBirth)
    {
        if (yearOfBirth < 1900 || yearOfBirth > DateTime.UtcNow.Year)
        {
            throw new ArgumentOutOfRangeException(
                nameof(yearOfBirth),
                $"Year of birth must be between 1900 and {DateTime.UtcNow.Year}.");
        }
    }
}
