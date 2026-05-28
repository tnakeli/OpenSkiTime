using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Desktop.Models;

namespace OpenSkiTime.Desktop.ViewModels;

/// <summary>
/// Observable participation cell for a single competition column.
/// </summary>
public sealed partial class ParticipationCellViewModel : ObservableObject
{
    public Guid CompetitionId { get; }
    public string ShortLabel { get; }
    [ObservableProperty] private bool _isParticipating;

    public ParticipationCellViewModel(Guid competitionId, string shortLabel, bool isParticipating)
    {
        CompetitionId = competitionId;
        ShortLabel = shortLabel;
        _isParticipating = isParticipating;
    }
}

/// <summary>
/// Observable row VM for a single competitor in the competitor grid.
/// Supports both read-only display and inline editing via RowState.
/// </summary>
public sealed partial class CompetitorRowViewModel : ObservableObject
{
    private string _originalLastName = string.Empty;
    private string _originalFirstName = string.Empty;
    private int _originalYearOfBirth;
    private string _originalGender = string.Empty;
    private string _originalNationCode = string.Empty;
    private string _originalClubName = string.Empty;
    private string _originalFisCode = string.Empty;

    public CompetitorRowViewModel(OpenSkiTime.Domain.Competitors.Competitor c)
    {
        ArgumentNullException.ThrowIfNull(c);
        Id = c.Id;
        _lastName = c.LastName.Value;
        _firstName = c.FirstName;
        _yearOfBirth = c.YearOfBirth;
        _gender = c.Gender?.ToString() ?? string.Empty;
        _nationCode = c.NationCode ?? string.Empty;
        _clubName = c.ClubName ?? string.Empty;
        _fisCode = c.FisCode ?? string.Empty;
        BibNumber = c.BibNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _rowState = RowState.Unchanged;
        SnapshotOriginals();
    }

    public CompetitorRowViewModel()
    {
        Id = Guid.NewGuid();
        _rowState = RowState.Added;
    }

    public static IReadOnlyList<string> GenderOptions { get; } = ["Men", "Women"];

    public Guid Id { get; }

    public string BibNumber { get; } = string.Empty;

    [ObservableProperty] private string _lastName = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private int _yearOfBirth;
    [ObservableProperty] private string _gender = string.Empty;
    [ObservableProperty] private string _nationCode = string.Empty;
    [ObservableProperty] private string _clubName = string.Empty;
    [ObservableProperty] private string _fisCode = string.Empty;

    [ObservableProperty] private RowState _rowState;
    [ObservableProperty] private bool _isEditing;

    public List<ChangeLogEntry> PendingChanges { get; } = [];

    /// <summary>Fields changed by a paste operation (highlights individual cells).</summary>
    public HashSet<string> ModifiedFields { get; } = [];

    public bool IsFieldModified(string fieldName) => ModifiedFields.Contains(fieldName);

    /// <summary>One cell per competition in the event series, ordered by date.</summary>
    public ObservableCollection<ParticipationCellViewModel> Participations { get; } = [];

    internal void SnapshotOriginals()
    {
        _originalLastName = LastName;
        _originalFirstName = FirstName;
        _originalYearOfBirth = YearOfBirth;
        _originalGender = Gender;
        _originalNationCode = NationCode;
        _originalClubName = ClubName;
        _originalFisCode = FisCode;
        ModifiedFields.Clear();
    }

    internal void RestoreOriginals()
    {
        LastName = _originalLastName;
        FirstName = _originalFirstName;
        YearOfBirth = _originalYearOfBirth;
        Gender = _originalGender;
        NationCode = _originalNationCode;
        ClubName = _originalClubName;
        FisCode = _originalFisCode;
        RowState = RowState.Unchanged;
        PendingChanges.Clear();
    }

    internal string GetFieldValue(string field) => field switch
    {
        nameof(LastName) => LastName,
        nameof(FirstName) => FirstName,
        nameof(YearOfBirth) => YearOfBirth.ToString(CultureInfo.InvariantCulture),
        nameof(Gender) => Gender,
        nameof(NationCode) => NationCode,
        nameof(ClubName) => ClubName,
        nameof(FisCode) => FisCode,
        _ => string.Empty,
    };

    internal string GetOriginalFieldValue(string field) => field switch
    {
        nameof(LastName) => _originalLastName,
        nameof(FirstName) => _originalFirstName,
        nameof(YearOfBirth) => _originalYearOfBirth.ToString(CultureInfo.InvariantCulture),
        nameof(Gender) => _originalGender,
        nameof(NationCode) => _originalNationCode,
        nameof(ClubName) => _originalClubName,
        nameof(FisCode) => _originalFisCode,
        _ => string.Empty,
    };
}
