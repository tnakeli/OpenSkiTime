using OpenSkiTime.Application;
using OpenSkiTime.Domain;

namespace OpenSkiTime.Desktop;

// Update from Ski-Alp-Pro (Settings > About > Advanced): a test tool, available in advanced mode only.
public sealed partial class MainViewModel
{
    public bool CanUpdateFromSkiAlpPro => AdvancedMode && IsEditingSeries;

    /// <summary>Asks for a Ski-Alp-Pro file and reads it. Null when there is nothing to show; the status bar says why.</summary>
    public async Task<SkiAlpProImportViewModel?> OpenSkiAlpProAsync()
    {
        SkiAlpProImportViewModel? result = null;
        await GuardAsync(async () =>
        {
            if (!AdvancedMode) { throw new DomainValidationException("Enable advanced mode to update from Ski-Alp-Pro."); }
            if (!IsEditingSeries || _current is null) { throw new DomainValidationException("Open an event series before updating from Ski-Alp-Pro."); }
            await EnsureSkiAlpProReadyAsync();
            if (await dialogs.ChooseSkiAlpProFileAsync() is not { } path) { return; }
            SetStatus("Reading the Ski-Alp-Pro file…");
            var race = await Persistence.SkiAlpProFileReader.ReadAsync(path);
            var series = await workspace.ReadAsync();
            result = new(race, series.Competitions, id => workspace.ReadSkiAlpProReplacementAsync(id), (scope, target, codex) => ImportSkiAlpProAsync(race, scope, target, codex));
            await result.LoadReplacementAsync();
            SetStatus($"{race.FileName} read. Nothing is imported yet.");
        });
        return result;
    }

    private async Task EnsureSkiAlpProReadyAsync()
    {
        if (workspace.Timing?.IsActive == true || workspace.Auxiliary?.IsActive == true)
        { throw new DomainValidationException("Leave the Timing view before updating from Ski-Alp-Pro."); }
        EnsureDeskClean();
        if (!await FlushRaceInformationAsync() || !await FlushTimingReportAsync())
        { throw new DomainValidationException("Unsaved race information or timing report could not be saved. Correct it before updating from Ski-Alp-Pro."); }
    }

    private async Task<string> ImportSkiAlpProAsync(SkiAlpProRace race, SkiAlpProScope scope, Guid? target, string codex)
    {
        if (!AdvancedMode) { throw new DomainValidationException("Enable advanced mode to update from Ski-Alp-Pro."); }
        await EnsureSkiAlpProReadyAsync();
        EnsureFisListLoaded();
        IReadOnlyList<FisPointsListInfo> known = _fisList is { } list ? [new(list.ListCode, list.Name, list.ValidFrom, list.ValidTo, list.PenaltyRules)] : [];
        var request = new SkiAlpProImportRequest(race, scope, await workspace.ReadAsync(), await workspace.ReadCompetitorDeskAsync(), target, codex, known,
            string.IsNullOrWhiteSpace(TimingOperator) ? Environment.UserName : TimingOperator, DateTimeOffset.Now);
        var commit = await Task.Run(() => SkiAlpProImport.Build(request, new Devices.AlgeDecoderFactory(), Guid.NewGuid));
        var result = await workspace.ApplySkiAlpProImportAsync(commit);
        var label = result.Series.Competitions.First(x => x.Id == commit.CompetitionId).Values.ShortLabel;
        var message = $"Test import saved to {label}: {SkiAlpProImport.ScopeLabel(scope)}, {commit.Athletes.Count} competitors."
            + (result.BackupPath is null ? "" : $" Copy of the previous series: {Path.GetFileName(result.BackupPath)}.")
            + string.Concat(commit.Notices.Except(race.Notices).Select(x => " " + x));
        // The import is saved. Every view must forget the replaced lists and timing: the series is opened again.
        try { await ReloadSeriesAsync(commit.CompetitionId); }
        catch (Exception ex) when (ex is SeriesFileException or IOException or UnauthorizedAccessException)
        { throw new SeriesFileException("The import is saved, but the event series could not be read again. Close and open it. " + ex.Message, ex); }
        SetStatus(message);
        return message;
    }

    private async Task ReloadSeriesAsync(Guid competitionId)
    {
        var details = await workspace.OpenAsync(workspace.FilePath!);
        StopInformationTracking(); _informationKey = null;
        _current = null;
        ResultsCompetition = null;
        SelectedCompetition = null;
        IsCompetitionEditing = false;
        Apply(details);
        InvalidateCompetitionInformation(details.Competitions.Select(x => x.Id));
        DeskCompetition = Competitions.FirstOrDefault(x => x.Id == competitionId) ?? DeskCompetition;
        await LoadCompetitorDeskAsync();
    }
}
