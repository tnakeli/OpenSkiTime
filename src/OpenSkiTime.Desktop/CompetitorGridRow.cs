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
    // FIS points saved with the competitor and the staged points (from Update from FIS list or a FIS search); a
    // difference is a pending change like any edited cell.
    public CompetitorFisPoints? SavedFisPoints { get; private set; }
    private CompetitorFisPoints? _fisPoints;
    public CompetitorFisPoints? FisPoints
    {
        get => _fisPoints;
        set => SetFisPoints(value, FederationCode.Trim());
    }

    // The FIS code the current points belong to (points are an athlete's, not a row's). Recorded even when the value is
    // unchanged: another athlete can have equal points.
    public string FisPointsCode { get; private set; } = string.Empty;

    public void SetFisPoints(CompetitorFisPoints? value, string code)
    {
        FisPointsCode = code;
        if (Equals(_fisPoints, value)) { return; }
        _fisPoints = value;
        foreach (var name in new[] { nameof(FisPoints), nameof(FisDh), nameof(FisSg), nameof(FisSl), nameof(FisGs), nameof(FisAc),
                     nameof(FisDhText), nameof(FisSgText), nameof(FisSlText), nameof(FisGsText), nameof(FisAcText) })
        { OnPropertyChanged(name); }
        NotifyDraftChanged();
    }

    // True when the Code differs from the saved Code: the saved points then cannot stay as they are.
    public bool IsFederationCodeChanged => SavedValues is { } saved
        && !string.Equals(FederationCode.Trim(), saved.FederationCode ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    public decimal? FisDh => FisPoints?.Dh;
    public decimal? FisSg => FisPoints?.Sg;
    public decimal? FisSl => FisPoints?.Sl;
    public decimal? FisGs => FisPoints?.Gs;
    public decimal? FisAc => FisPoints?.Ac;
    public string FisDhText => FormatPoint(FisDh);
    public string FisSgText => FormatPoint(FisSg);
    public string FisSlText => FormatPoint(FisSl);
    public string FisGsText => FormatPoint(FisGs);
    public string FisAcText => FormatPoint(FisAc);
    public bool IsFisPointsChanged => !IsPlaceholder && !Equals(FisPoints, SavedFisPoints);
    public bool IsDhChanged => IsFisPointsChanged && FisDh != SavedFisPoints?.Dh;
    public bool IsSgChanged => IsFisPointsChanged && FisSg != SavedFisPoints?.Sg;
    public bool IsSlChanged => IsFisPointsChanged && FisSl != SavedFisPoints?.Sl;
    public bool IsGsChanged => IsFisPointsChanged && FisGs != SavedFisPoints?.Gs;
    public bool IsAcChanged => IsFisPointsChanged && FisAc != SavedFisPoints?.Ac;

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
          || IsNationChanged || IsClubChanged || IsCodeChanged || IsFisPointsChanged || GridEntries.Any(x => x.IsChanged)));
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

    public static CompetitorGridRow From(CompetitorDetails details, CompetitorFisPoints? fisPoints = null)
    {
        var row = new CompetitorGridRow();
        row.MarkSaved(details, fisPoints);
        return row;
    }

    public CompetitorValues Draft() => new(Surname, FirstName,
        OptionalNumber(BirthYearText), FederationCode, Nation, Club, ParseGender(GenderText));

    public void MarkSaved(CompetitorDetails details, CompetitorFisPoints? fisPoints = null)
    {
        ArgumentNullException.ThrowIfNull(details);
        Id = details.Id;
        SavedValues = details.Values;
        SavedFisPoints = fisPoints;
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
        if (SavedValues is not null) { FisPoints = SavedFisPoints; }
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
            case nameof(FisPoints): SetFisPoints(SavedFisPoints, saved.FederationCode ?? string.Empty); break;
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
        OnPropertyChanged(nameof(IsFisPointsChanged));
        OnPropertyChanged(nameof(IsDhChanged));
        OnPropertyChanged(nameof(IsSgChanged));
        OnPropertyChanged(nameof(IsSlChanged));
        OnPropertyChanged(nameof(IsGsChanged));
        OnPropertyChanged(nameof(IsAcChanged));
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

    // The gender typed in the row, or null while it is empty or not yet valid.
    public Gender? EnteredGender
    {
        get { try { return ParseGender(GenderText); } catch (DomainValidationException) { return null; } }
    }

    private static Gender? ParseGender(string text) => text.Trim().ToUpperInvariant() switch
    {
        "" => null,
        "F" or "FEMALE" or "WOMAN" or "WOMEN" => Gender.Female,
        "M" or "MALE" or "MAN" or "MEN" => Gender.Male,
        _ => throw new DomainValidationException("Gender must be Women or Men."),
    };
}
