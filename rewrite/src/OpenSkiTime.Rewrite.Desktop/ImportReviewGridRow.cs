using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class ImportReviewGridRow : ObservableObject
{
    private readonly CompetitorValues? _before;
    public int SourceRow { get; }
    public Guid? CompetitorId { get; }
    public bool IsNew { get; }
    public string MatchNote => IsNew ? "NEW" : "UPDATE";
    public string Changes { get; }
    public string WarningText { get; }
    public bool NeedsApproval => WarningText.Length > 0;
    [ObservableProperty] private bool _approved;
    [ObservableProperty] private string _surname = string.Empty;
    [ObservableProperty] private string _firstName = string.Empty;
    [ObservableProperty] private string _birthYearText = string.Empty;
    [ObservableProperty] private string _genderText = string.Empty;
    [ObservableProperty] private string _nation = string.Empty;
    [ObservableProperty] private string _club = string.Empty;
    [ObservableProperty] private string _federationCode = string.Empty;
    public ObservableCollection<ImportReviewEntryRow> Entries { get; } = [];

    public bool IsSurnameChanged => IsNew || !string.Equals(Surname, _before?.Surname, StringComparison.Ordinal);
    public bool IsFirstNameChanged => IsNew || !string.Equals(FirstName, _before?.FirstName, StringComparison.Ordinal);
    public bool IsYearChanged => IsNew || BirthYearText != (_before?.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
    public bool IsGenderChanged => IsNew || GenderText != GenderLabels.Format(_before?.Gender);
    public bool IsNationChanged => IsNew || Nation != (_before?.Nation ?? string.Empty);
    public bool IsClubChanged => IsNew || Club != (_before?.Club ?? string.Empty);
    public bool IsCodeChanged => IsNew || FederationCode != (_before?.FederationCode ?? string.Empty);

    public ImportReviewGridRow(ImportReviewItem item, CompetitorValues? before,
        IReadOnlyList<CompetitionDetails> competitions, IReadOnlyList<ParticipationDetails> oldEntries)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(competitions);
        ArgumentNullException.ThrowIfNull(oldEntries);
        SourceRow = item.SourceRow;
        CompetitorId = item.CompetitorId;
        IsNew = item.IsNew;
        Changes = string.Join(", ", item.ChangedFields);
        WarningText = string.Join(" ", item.Warnings);
        _before = before;
        Surname = item.Values.Surname;
        FirstName = item.Values.FirstName;
        BirthYearText = item.Values.BirthYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        GenderText = GenderLabels.Format(item.Values.Gender);
        Nation = item.Values.Nation ?? string.Empty;
        Club = item.Values.Club ?? string.Empty;
        FederationCode = item.Values.FederationCode ?? string.Empty;
        foreach (var competition in competitions)
        {
            var patch = item.Entries.FirstOrDefault(x => x.CompetitionId == competition.Id);
            var old = oldEntries.FirstOrDefault(x => x.CompetitorId == item.CompetitorId
                && x.CompetitionId == competition.Id);
            Entries.Add(new ImportReviewEntryRow(competition, patch, old));
        }
    }

    partial void OnSurnameChanged(string value) => OnPropertyChanged(nameof(IsSurnameChanged));
    partial void OnFirstNameChanged(string value) => OnPropertyChanged(nameof(IsFirstNameChanged));
    partial void OnBirthYearTextChanged(string value) => OnPropertyChanged(nameof(IsYearChanged));
    partial void OnGenderTextChanged(string value) => OnPropertyChanged(nameof(IsGenderChanged));
    partial void OnNationChanged(string value) => OnPropertyChanged(nameof(IsNationChanged));
    partial void OnClubChanged(string value) => OnPropertyChanged(nameof(IsClubChanged));
    partial void OnFederationCodeChanged(string value) => OnPropertyChanged(nameof(IsCodeChanged));

    public ImportCommitRow ToCommitRow(int eventYear)
    {
        if (!string.IsNullOrWhiteSpace(BirthYearText)
            && !int.TryParse(BirthYearText.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new DomainValidationException($"Review row {SourceRow}: birth year must be a whole number.");
        }
        var year = string.IsNullOrWhiteSpace(BirthYearText) ? (int?)null
            : int.Parse(BirthYearText.Trim(), CultureInfo.InvariantCulture);
        var gender = GenderText.Trim().ToUpperInvariant() switch
        {
            "" => (Gender?)null,
            "F" or "FEMALE" or "WOMAN" or "WOMEN" => Gender.Female,
            "M" or "MALE" or "MAN" or "MEN" => Gender.Male,
            "O" or "OTHER" => Gender.Other,
            _ => throw new DomainValidationException($"Review row {SourceRow}: gender must be Women, Men or Other."),
        };
        var values = new CompetitorValues(Surname, FirstName, year, FederationCode,
            Nation, Club, gender).Validated(eventYear);
        return new ImportCommitRow(CompetitorId, values,
            Entries.Select(x => x.ToPatch(SourceRow)).Where(x => x is not null).Cast<ImportEntryPatch>().ToArray());
    }
}

public sealed partial class ImportReviewEntryRow : ObservableObject
{
    public Guid CompetitionId { get; }
    public string Label { get; }
    public string Name { get; }
    public string Current { get; }
    [ObservableProperty] private string _participationText = string.Empty;
    [ObservableProperty] private string _bibText = string.Empty;

    public ImportReviewEntryRow(CompetitionDetails competition, ImportEntryPatch? patch,
        ParticipationDetails? before)
    {
        ArgumentNullException.ThrowIfNull(competition);
        CompetitionId = competition.Id;
        Label = competition.Values.ShortLabel;
        Name = competition.Values.Name;
        Current = $"Saved: {(before?.Participates == true ? "X" : "–")}, bib {before?.ImportedBib?.ToString(CultureInfo.InvariantCulture) ?? "–"}";
        ParticipationText = patch?.Participates switch { true => "X", false => "0", _ => string.Empty };
        BibText = patch?.BibSpecified == true ? patch.ImportedBib?.ToString(CultureInfo.InvariantCulture) ?? "~" : string.Empty;
    }

    public ImportEntryPatch? ToPatch(int sourceRow)
    {
        bool? state = ParticipationText.Trim().ToUpperInvariant() switch
        {
            "" => null,
            "X" or "1" or "YES" or "TRUE" or "KYLLÄ" or "KYLLA" or "JOO" or "K" => true,
            "0" or "NO" or "FALSE" or "EI" or "-" => false,
            _ => throw new DomainValidationException($"Review row {sourceRow}: participation for {Label} must be X, 0 or blank."),
        };
        var bibText = BibText.Trim();
        var bibSpecified = bibText.Length > 0;
        int? bib = null;
        if (bibSpecified && bibText != "~")
        {
            if (!int.TryParse(bibText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed is <= 0 or > 99999)
            {
                throw new DomainValidationException($"Review row {sourceRow}: bib for {Label} must be 1–99999 or ~ to clear.");
            }
            bib = parsed;
        }
        return state is null && !bibSpecified ? null : new ImportEntryPatch(CompetitionId, state, bibSpecified, bib);
    }
}
