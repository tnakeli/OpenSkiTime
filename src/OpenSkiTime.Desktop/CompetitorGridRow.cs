using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

public sealed partial class CompetitorGridRow : ObservableObject
{
    public static IReadOnlyList<string> GenderOptions { get; } = ["Women", "Men"];
    public IReadOnlyList<string> AvailableGenders { get; } = GenderOptions;
    public Guid LocalId { get; } = Guid.NewGuid();
    public Guid? Id { get; private set; }
    public CompetitorValues? SavedValues { get; private set; }
    public ObservableCollection<CompetitionEntryChoice> GridEntries { get; } = [];
    private IReadOnlyDictionary<string, decimal>? _fisPoints;
    public decimal? FisDh => FisPoint("DH");
    public decimal? FisSg => FisPoint("SG");
    public decimal? FisSl => FisPoint("SL");
    public decimal? FisGs => FisPoint("GS");
    public decimal? FisAc => FisPoint("AC");
    public string FisDhText => FormatPoint(FisDh);
    public string FisSgText => FormatPoint(FisSg);
    public string FisSlText => FormatPoint(FisSl);
    public string FisGsText => FormatPoint(FisGs);
    public string FisAcText => FormatPoint(FisAc);

    public void SetFisPoints(IReadOnlyDictionary<string, decimal>? points)
    {
        _fisPoints = points;
        OnPropertyChanged(nameof(FisDh));
        OnPropertyChanged(nameof(FisSg));
        OnPropertyChanged(nameof(FisSl));
        OnPropertyChanged(nameof(FisGs));
        OnPropertyChanged(nameof(FisAc));
        OnPropertyChanged(nameof(FisDhText));
        OnPropertyChanged(nameof(FisSgText));
        OnPropertyChanged(nameof(FisSlText));
        OnPropertyChanged(nameof(FisGsText));
        OnPropertyChanged(nameof(FisAcText));
    }

    private decimal? FisPoint(string discipline) => _fisPoints is not null
        && _fisPoints.TryGetValue(discipline, out var value) ? value : null;

    private static string FormatPoint(decimal? value) => value?.ToString("0.00", CultureInfo.InvariantCulture) ?? string.Empty;

    [ObservableProperty] private string _surname = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _birthYearText = string.Empty;
    [ObservableProperty] private string _genderText = string.Empty;
    [ObservableProperty] private string _federationCode = string.Empty;
    [ObservableProperty] private string _nation = string.Empty;
    [ObservableProperty] private string _club = string.Empty;
    [ObservableProperty] private string _category = "Unclassified";
    [ObservableProperty] private bool _isPlaceholder;
    [ObservableProperty] private bool _isPendingDelete;
    [ObservableProperty] private string _warningText = string.Empty;
    [ObservableProperty] private bool _warningApproved;

    public bool HasTypedContent => Surname.Length > 0 || FirstName.Length > 0 || BirthYearText.Length > 0
        || GenderText.Length > 0 || FederationCode.Length > 0 || Nation.Length > 0 || Club.Length > 0
        || GridEntries.Any(x => x.IsChanged);
    public bool HasPendingChanges => !IsPlaceholder && (IsPendingDelete || (SavedValues is null
        ? HasTypedContent
        : IsSurnameChanged || IsFirstNameChanged || IsYearChanged || IsGenderChanged
          || IsNationChanged || IsClubChanged || IsCodeChanged || GridEntries.Any(x => x.IsChanged)));
    public bool NeedsApproval => WarningText.Length > 0 && HasPendingChanges;
    public string ChangeMarker => IsPendingDelete ? "DEL" : IsPlaceholder ? "+" : SavedValues is null ? "NEW" : HasPendingChanges ? "●" : string.Empty;
    public bool IsSurnameChanged => IsFieldChanged(Surname, SavedValues?.Surname);
    public bool IsFirstNameChanged => IsFieldChanged(FirstName, SavedValues?.FirstName);
    public bool IsYearChanged => IsFieldChanged(BirthYearText, SavedValues?.BirthYear?.ToString(CultureInfo.InvariantCulture));
    public bool IsGenderChanged => IsFieldChanged(GenderText, GenderLabels.Format(SavedValues?.Gender));
    public bool IsNationChanged => IsFieldChanged(Nation, SavedValues?.Nation);
    public bool IsClubChanged => IsFieldChanged(Club, SavedValues?.Club);
    public bool IsCodeChanged => IsFieldChanged(FederationCode, SavedValues?.FederationCode);

    private bool IsFieldChanged(string current, string? saved)
        => !IsPlaceholder && (SavedValues is null ? current.Length > 0 : current != (saved ?? string.Empty));

    public static CompetitorGridRow From(CompetitorDetails details)
    {
        var row = new CompetitorGridRow();
        row.MarkSaved(details);
        return row;
    }

    public CompetitorValues Draft() => new(Surname, FirstName,
        OptionalNumber(BirthYearText), FederationCode, Nation, Club, ParseGender(GenderText));

    public void MarkSaved(CompetitorDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        Id = details.Id;
        SavedValues = details.Values;
        RestoreAll();
    }

    public void RestoreAll()
    {
        if (SavedValues is { } values)
        {
            Surname = values.Surname;
            FirstName = values.FirstName;
            BirthYearText = values.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            GenderText = GenderLabels.Format(values.Gender);
            FederationCode = values.FederationCode ?? string.Empty;
            Nation = values.Nation ?? string.Empty;
            Club = values.Club ?? string.Empty;
        }
        foreach (var choice in GridEntries) { choice.Restore(); }
        IsPendingDelete = false;
        WarningText = string.Empty;
        WarningApproved = false;
        NotifyDraftChanged();
    }

    public void RestoreField(string field)
    {
        var saved = SavedValues;
        if (saved is null) { return; }
        switch (field)
        {
            case nameof(Surname): Surname = saved.Surname; break;
            case nameof(FirstName): FirstName = saved.FirstName; break;
            case nameof(BirthYearText): BirthYearText = saved.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty; break;
            case nameof(GenderText): GenderText = GenderLabels.Format(saved.Gender); break;
            case nameof(Nation): Nation = saved.Nation ?? string.Empty; break;
            case nameof(Club): Club = saved.Club ?? string.Empty; break;
            case nameof(FederationCode): FederationCode = saved.FederationCode ?? string.Empty; break;
        }
    }

    public void NotifyDraftChanged()
    {
        OnPropertyChanged(nameof(IsSurnameChanged));
        OnPropertyChanged(nameof(IsFirstNameChanged));
        OnPropertyChanged(nameof(IsYearChanged));
        OnPropertyChanged(nameof(IsGenderChanged));
        OnPropertyChanged(nameof(IsNationChanged));
        OnPropertyChanged(nameof(IsClubChanged));
        OnPropertyChanged(nameof(IsCodeChanged));
        OnPropertyChanged(nameof(HasTypedContent));
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(ChangeMarker));
        OnPropertyChanged(nameof(NeedsApproval));
    }

    partial void OnSurnameChanged(string value) => NotifyDraftChanged();
    partial void OnFirstNameChanged(string value) => NotifyDraftChanged();
    partial void OnBirthYearTextChanged(string value) => NotifyDraftChanged();
    partial void OnGenderTextChanged(string value) => NotifyDraftChanged();
    partial void OnNationChanged(string value) => NotifyDraftChanged();
    partial void OnClubChanged(string value) => NotifyDraftChanged();
    partial void OnFederationCodeChanged(string value) => NotifyDraftChanged();
    partial void OnIsPlaceholderChanged(bool value) => NotifyDraftChanged();
    partial void OnIsPendingDeleteChanged(bool value) => NotifyDraftChanged();
    partial void OnWarningTextChanged(string value) => OnPropertyChanged(nameof(NeedsApproval));

    private static int? OptionalNumber(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var year))
        {
            throw new DomainValidationException("Birth year must be a whole number.");
        }
        return year;
    }

    private static Gender? ParseGender(string text) => text.Trim().ToUpperInvariant() switch
    {
        "" => null,
        "F" or "FEMALE" or "WOMAN" or "WOMEN" => Gender.Female,
        "M" or "MALE" or "MAN" or "MEN" => Gender.Male,
        _ => throw new DomainValidationException("Gender must be Women or Men."),
    };
}
