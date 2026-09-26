using System.Globalization;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class CompetitorGridRow : ObservableObject
{
    public Guid? Id { get; private set; }
    public CompetitorValues? SavedValues { get; private set; }
    public bool SavedParticipation { get; private set; }
    public int? SavedImportedBib { get; private set; }
    public ImportReviewGridRow? PendingImport { get; private set; }
    public bool IsImportHighlighted => PendingImport is not null;
    [ObservableProperty] private bool _isPendingDelete;
    public string ImportMarker => IsPendingDelete ? "DEL" : PendingImport is null ? string.Empty : PendingImport.IsNew ? "NEW" : "Δ";
    public bool IsSurnameChanged => PendingImport?.IsSurnameChanged == true;
    public bool IsFirstNameChanged => PendingImport?.IsFirstNameChanged == true;
    public bool IsYearChanged => PendingImport?.IsYearChanged == true;
    public bool IsGenderChanged => PendingImport?.IsGenderChanged == true;
    public bool IsNationChanged => PendingImport?.IsNationChanged == true;
    public bool IsClubChanged => PendingImport?.IsClubChanged == true;
    public bool IsCodeChanged => PendingImport?.IsCodeChanged == true;
    public ObservableCollection<CompetitionEntryChoice> GridEntries { get; } = [];
    public bool HasDraftChanges => SavedValues is { } saved
        ? Surname != saved.Surname || FirstName != saved.FirstName
          || BirthYearText != (saved.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
          || GenderText != (saved.Gender?.ToString() ?? string.Empty)
          || FederationCode != (saved.FederationCode ?? string.Empty)
          || Nation != (saved.Nation ?? string.Empty) || Club != (saved.Club ?? string.Empty)
          || IsParticipating != SavedParticipation
          || ImportedBibText != (SavedImportedBib?.ToString(CultureInfo.InvariantCulture) ?? string.Empty)
        : Surname.Length > 0 || FirstName.Length > 0 || BirthYearText.Length > 0
          || GenderText.Length > 0 || FederationCode.Length > 0 || Nation.Length > 0
          || Club.Length > 0 || IsParticipating || ImportedBibText.Length > 0;

    [ObservableProperty] private string _surname = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _birthYearText = string.Empty;
    [ObservableProperty] private string _genderText = string.Empty;
    [ObservableProperty] private string _federationCode = string.Empty;
    [ObservableProperty] private string _nation = string.Empty;
    [ObservableProperty] private string _club = string.Empty;
    [ObservableProperty] private string _importedBibText = string.Empty;
    [ObservableProperty] private bool _isParticipating;
    [ObservableProperty] private string _category = "Unclassified";
    [ObservableProperty] private string _readiness = "Not entered";

    public static CompetitorGridRow From(CompetitorDetails details, ParticipationDetails? entry)
    {
        var row = new CompetitorGridRow();
        row.MarkSaved(details, entry);
        return row;
    }

    public CompetitorValues Draft() => new(Surname, FirstName,
        OptionalNumber(BirthYearText, "Birth year"), FederationCode, Nation, Club, ParseGender(GenderText));

    public int? DraftImportedBib() => OptionalNumber(ImportedBibText, "Imported bib");

    public void MarkSaved(CompetitorDetails details, ParticipationDetails? entry)
    {
        ArgumentNullException.ThrowIfNull(details);
        Id = details.Id;
        SavedValues = details.Values;
        SavedParticipation = entry?.Participates ?? false;
        SavedImportedBib = entry?.ImportedBib;
        RestoreDraft();
    }

    public void SetEntry(ParticipationDetails? entry)
    {
        SavedParticipation = entry?.Participates ?? false;
        SavedImportedBib = entry?.ImportedBib;
        IsParticipating = SavedParticipation;
        ImportedBibText = SavedImportedBib?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public void RestoreDraft()
    {
        if (SavedValues is not { } values) { return; }
        Surname = values.Surname;
        FirstName = values.FirstName;
        BirthYearText = values.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        GenderText = values.Gender?.ToString() ?? string.Empty;
        FederationCode = values.FederationCode ?? string.Empty;
        Nation = values.Nation ?? string.Empty;
        Club = values.Club ?? string.Empty;
        IsParticipating = SavedParticipation;
        ImportedBibText = SavedImportedBib?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public void StageImport(ImportReviewGridRow review)
    {
        ArgumentNullException.ThrowIfNull(review);
        PendingImport = review;
        Surname = review.Surname;
        FirstName = review.FirstName;
        BirthYearText = review.BirthYearText;
        GenderText = review.GenderText;
        Nation = review.Nation;
        Club = review.Club;
        FederationCode = review.FederationCode;
        OnPropertyChanged(nameof(IsImportHighlighted));
        OnPropertyChanged(nameof(ImportMarker));
        OnPropertyChanged(nameof(IsSurnameChanged));
        OnPropertyChanged(nameof(IsFirstNameChanged));
        OnPropertyChanged(nameof(IsYearChanged));
        OnPropertyChanged(nameof(IsGenderChanged));
        OnPropertyChanged(nameof(IsNationChanged));
        OnPropertyChanged(nameof(IsClubChanged));
        OnPropertyChanged(nameof(IsCodeChanged));
    }

    public void ClearImport()
    {
        PendingImport = null;
        if (SavedValues is not null) { RestoreDraft(); }
        OnPropertyChanged(nameof(IsImportHighlighted));
        OnPropertyChanged(nameof(ImportMarker));
        OnPropertyChanged(nameof(IsSurnameChanged));
        OnPropertyChanged(nameof(IsFirstNameChanged));
        OnPropertyChanged(nameof(IsYearChanged));
        OnPropertyChanged(nameof(IsGenderChanged));
        OnPropertyChanged(nameof(IsNationChanged));
        OnPropertyChanged(nameof(IsClubChanged));
        OnPropertyChanged(nameof(IsCodeChanged));
    }

    partial void OnSurnameChanged(string value) => OnPropertyChanged(nameof(IsSurnameChanged));
    partial void OnFirstNameChanged(string value) => OnPropertyChanged(nameof(IsFirstNameChanged));
    partial void OnBirthYearTextChanged(string value) => OnPropertyChanged(nameof(IsYearChanged));
    partial void OnGenderTextChanged(string value) => OnPropertyChanged(nameof(IsGenderChanged));
    partial void OnNationChanged(string value) => OnPropertyChanged(nameof(IsNationChanged));
    partial void OnClubChanged(string value) => OnPropertyChanged(nameof(IsClubChanged));
    partial void OnFederationCodeChanged(string value) => OnPropertyChanged(nameof(IsCodeChanged));
    partial void OnIsPendingDeleteChanged(bool value) => OnPropertyChanged(nameof(ImportMarker));

    private static int? OptionalNumber(string text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw new DomainValidationException($"{label} must be a whole number.");
        }
        return value;
    }

    private static Gender? ParseGender(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        return text.Trim().ToUpperInvariant() switch
        {
            "F" or "FEMALE" => Gender.Female,
            "M" or "MALE" => Gender.Male,
            "O" or "OTHER" => Gender.Other,
            _ => throw new DomainValidationException("Gender must be Female, Male or Other."),
        };
    }
}
