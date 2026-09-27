using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.Rewrite.Domain;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class CompetitionEntryChoice(CompetitionDetails competition, bool participates, int? importedBib)
    : ObservableObject
{
    public Guid CompetitionId => competition.Id;
    public string Label => competition.Values.ShortLabel;
    public int? ImportedBib => importedBib;
    public bool SavedParticipation { get; private set; } = participates;
    public bool IsChanged => IsParticipating != SavedParticipation;
    [ObservableProperty] private bool _isParticipating = participates;
    [ObservableProperty] private bool _isSaving;

    public void MarkSaved()
    {
        SavedParticipation = IsParticipating;
        OnPropertyChanged(nameof(IsChanged));
    }
    public void Restore() => IsParticipating = SavedParticipation;
    partial void OnIsParticipatingChanged(bool value) => OnPropertyChanged(nameof(IsChanged));
}
