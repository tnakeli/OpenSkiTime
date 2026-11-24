using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OpenSkiTime.Application;
using OpenSkiTime.Desktop;
using OpenSkiTime.Domain;
using OpenSkiTime.Persistence;
using Xunit;

namespace OpenSkiTime.Tests;

public partial class DesktopWorkflowTests
{
    private static readonly DateOnly s_fissiDate = new(2026, 11, 28);

    private static CompetitionValues FissiRace(string label, string? codex, string gender)
        => new("Synthetic " + label, label, s_fissiDate, Discipline.Slalom, RaceType.Fis, 2, 0, codex, Calendar: new(2027, "Test place", "FIN", "FIS", gender));

    private static FissiPages.Site FissiSite()
    {
        var site = new FissiPages.Site { Upcoming = FissiPages.Races(new(51, "0507"), new(52, "5489", "SL", "Women")) };
        site.Pages[51] = FissiPages.RacePage("0507", "28.11.2026", new FissiPages.Athlete("Testi ESIMERKKI", "100001"), new FissiPages.Athlete("Uusi TULOKAS", "100002"));
        site.Pages[52] = FissiPages.RacePage("5489", "28.11.2026", new FissiPages.Athlete("Tiina ESIMERKKI", "200001"));
        return site;
    }

    // Two men entered in the men's race: one registered with another club, one no longer registered.
    private static async Task<SeriesDetails> CreateFissiSeriesAsync(SeriesWorkspace workspace, string path)
    {
        var series = await workspace.CreateAsync(path, new("Synthetic series", "Test place", "Test club", s_fissiDate, s_fissiDate, "FIN", "2026/27"),
            [FissiRace("SL M", "0507", "M"), FissiRace("SL W", "5489", "W"), FissiRace("SL X", null, "M")]);
        var men = series.Competitions.Single(x => x.Values.ShortLabel == "SL M").Id;
        var saved = await workspace.SaveDeskRowAsync(null, new("ESIMERKKI", "Testi", 2008, "100001", "FIN", "Old Club", Gender.Male), men, true, null, series.Revision);
        await workspace.SaveDeskRowAsync(null, new("POISTUVA", "Testi", 2008, "100009", "FIN", "Synthetic Club", Gender.Male), men, true, null, saved.Revision);
        return await workspace.ReadAsync();
    }

    private static MainViewModel FissiViewModel(SeriesWorkspace workspace, string folder, string path, HttpClient http)
        => new(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
            fisStore: new FisLocalStore(folder), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);

    private static async Task InFissiFolderAsync(Func<string, string, Task> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), "openskitime-fissi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { await test(folder, Path.Combine(folder, "test.ost")); }
        finally { Directory.Delete(folder, true); }
    }

    private static bool IsEntered(CompetitorDeskDetails desk, string code, Guid competition)
        => desk.Participations.Any(x => x.Participates && x.CompetitionId == competition
            && x.CompetitorId == desk.Competitors.Single(y => y.Values.FederationCode == code).Id);

    [AvaloniaFact]
    public Task RegistrationsAreStagedReviewedAndSaved() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var series = await CreateFissiSeriesAsync(workspace, path);
        var men = series.Competitions.Single(x => x.Values.ShortLabel == "SL M").Id;
        var women = series.Competitions.Single(x => x.Values.ShortLabel == "SL W").Id;
        using var vm = FissiViewModel(workspace, folder, path, http);
        var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 900 };
        window.Show();
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        window.FindControl<Expander>("FissiSection")!.IsExpanded = true;
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsFissiPanelOpen);
        Assert.False(window.FindControl<DataGrid>("FissiRacesGrid")!.IsVisible);
        // The section header has the same text, so the button is found by name.
        var button = window.FindControl<Button>("UpdateFromFissiButton")!;
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        button.Command!.Execute(null);
        await vm.UpdateFromFissiCommand.ExecutionTask!;
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsError, vm.StatusMessage);

        // Every competition has its outcome.
        Assert.Equal(["SL M", "SL W", "SL X"], vm.FissiRaces.Select(x => x.Race));
        Assert.Equal(("0507", "28.11.2026", "2", "1", "1", "1", "Review and save"), (vm.FissiRaces[0].Codex, vm.FissiRaces[0].Date,
            vm.FissiRaces[0].Registered, vm.FissiRaces[0].New, vm.FissiRaces[0].Added, vm.FissiRaces[0].Removed, vm.FissiRaces[0].Status));
        Assert.Equal(("—", "—", "—", "No codex"), (vm.FissiRaces[2].Codex, vm.FissiRaces[2].Registered, vm.FissiRaces[2].Added, vm.FissiRaces[2].Status));
        Assert.True(window.FindControl<DataGrid>("FissiRacesGrid")!.IsVisible);

        // Staged only: the file still has the earlier entries.
        var before = await workspace.ReadCompetitorDeskAsync();
        Assert.Equal(2, before.Competitors.Count); Assert.True(IsEntered(before, "100009", men));
        Assert.Equal(4, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));
        Assert.True(vm.VisibleCompetitors.Single(x => x.FederationCode == "100001").IsClubChanged);
        Assert.Equal("Women", vm.VisibleCompetitors.Single(x => x.FederationCode == "200001").GenderText);
        // Pending edits are saved or discarded before another update.
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Contains("Save or discard", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal(4, vm.VisibleCompetitors.Count(x => !x.IsPlaceholder));

        // The operator keeps one removed entry (a late registration at the race office).
        vm.RestoreDeskChangeCommand.Execute(vm.DeskChangeLog.Single(x => x.Kind == DeskChangeKind.Entry && x.DisplayLabel.Contains("POISTUVA", StringComparison.Ordinal)));
        await vm.CommitDeskChangesCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        await workspace.CloseAsync();
        await workspace.OpenAsync(path);
        var saved = await workspace.ReadCompetitorDeskAsync();
        Assert.Equal(["100001", "100002", "100009", "200001"], saved.Competitors.Select(x => x.Values.FederationCode!).Order());
        Assert.Equal("Synthetic Club", saved.Competitors.Single(x => x.Values.FederationCode == "100001").Values.Club);
        Assert.True(IsEntered(saved, "100002", men)); Assert.True(IsEntered(saved, "200001", women)); Assert.True(IsEntered(saved, "100009", men));
        window.Close();
    });

    [AvaloniaFact]
    public Task UpdateAfterSaveProposesNothingAndRemovedEntriesAreSaved() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var series = await CreateFissiSeriesAsync(workspace, path);
        using var vm = FissiViewModel(workspace, folder, path, http);
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        await vm.CommitDeskChangesCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        var desk = await workspace.ReadCompetitorDeskAsync();
        Assert.False(IsEntered(desk, "100009", series.Competitions.Single(x => x.Values.ShortLabel == "SL M").Id));
        Assert.Equal(4, desk.Competitors.Count);
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Empty(vm.DeskChangeLog);
        Assert.Contains("Nothing to save", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Equal("Up to date", vm.FissiRaces[0].Status);
    });

    [AvaloniaFact]
    public Task AFailureOnTheLastPageStagesNothing() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        site.Pages[52] = "Race not found";
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        await CreateFissiSeriesAsync(workspace, path);
        using var vm = FissiViewModel(workspace, folder, path, http);
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.False(vm.IsFissiBusy);
        Assert.Empty(vm.DeskChangeLog); Assert.Empty(vm.FissiRaces);
        site.Intercept = _ => throw new HttpRequestException("offline");
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Contains("cannot be reached", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(vm.DeskChangeLog);
    });

    [AvaloniaTheory]
    [InlineData("edit")]
    [InlineData("competition")]
    [InlineData("close")]
    [InlineData("section")]
    public Task ChangesDuringTheDownloadStageNothing(string change) => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Intercept = async _ => { await release.Task; return null; };
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        await CreateFissiSeriesAsync(workspace, path);
        using var vm = FissiViewModel(workspace, folder, path, http);
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        var update = vm.UpdateFromFissiCommand.ExecuteAsync(null);
        Assert.True(vm.IsFissiBusy);
        if (change == "edit") { vm.VisibleCompetitors.First(x => !x.IsPlaceholder).Club = "Typed Club"; }
        else if (change == "competition")
        {
            vm.SelectedCompetition = vm.Competitions[0];
            vm.CompetitionName = "Renamed";
            await vm.SaveCompetitionCommand.ExecuteAsync(null);
            Assert.False(vm.IsError, vm.StatusMessage);
        }
        else if (change == "section") { vm.ShowCompetitionsCommand.Execute(null); Assert.True(vm.IsCompetitionsSection); }
        else { await vm.CloseSeriesCommand.ExecuteAsync(null); }
        release.SetResult();
        await update;
        Assert.False(vm.IsFissiBusy);
        Assert.Empty(vm.FissiRaces);
        Assert.Equal(change == "edit" ? 1 : 0, vm.DeskChangeLog.Count);
        if (change != "close") { Assert.True(vm.IsError); }
    });

    // A later run needs the entries and details of Run 1: the update leaves a drawn competition alone.
    [AvaloniaFact]
    public Task ADrawnCompetitionIsNotChangedAndItsSecondRunCanStillBePrepared() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var series = await CreateFissiSeriesAsync(workspace, path);
        var men = series.Competitions.Single(x => x.Values.ShortLabel == "SL M");
        var desk = await workspace.ReadCompetitorDeskAsync();
        var plan = FisStartOrder.FirstRun(men.Id, men.Values, Gender.Male, desk.Competitors.Select((x, i) => new DrawEntrant(x.Id, x.Values, 10 + i)).ToArray(),
            new("1327", s_fissiDate, s_fissiDate), new(), "fissi-draw");
        var lists = await workspace.SaveStartListAsync(new(plan, desk.Revision, "Operator", "Synthetic draw", DateTimeOffset.UnixEpoch));
        using var vm = FissiViewModel(workspace, folder, path, http);
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal("Start list saved, not updated", vm.FissiRaces[0].Status);
        Assert.DoesNotContain("/public/registered_athletes.php?race_id=51&lang=en", site.Requests);
        Assert.Equal(DeskChangeKind.Add, Assert.Single(vm.DeskChangeLog).Kind);
        await vm.CommitDeskChangesCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        var runOne = Assert.Single(lists.Revisions);
        lists = await workspace.MarkRunStartedAsync(runOne.Id, (await workspace.ReadAsync()).Revision, "Operator", DateTimeOffset.UnixEpoch);
        runOne = Assert.Single(lists.Revisions);
        var second = FisStartOrder.SecondRun(runOne, runOne.Plan.Entries.Select((x, i) => new RunFinish(x.Entrant.CompetitorId, FinishStatus.Finished, 6000 + i)).ToArray());
        lists = await workspace.SaveStartListAsync(new(second, lists.SeriesRevision, "Operator", "Synthetic second run", DateTimeOffset.UnixEpoch));
        Assert.Contains(lists.Revisions, x => x.Plan.RunNumber == 2);
    });

    [AvaloniaFact]
    public Task AStartListSavedDuringTheDownloadLocksItsCompetition() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        site.Intercept = async _ => { await release.Task; return null; };
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var series = await CreateFissiSeriesAsync(workspace, path);
        var men = series.Competitions.Single(x => x.Values.ShortLabel == "SL M");
        using var vm = FissiViewModel(workspace, folder, path, http);
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        var update = vm.UpdateFromFissiCommand.ExecuteAsync(null);
        while (site.Requests.Count == 0) { await Task.Delay(10); }
        var desk = await workspace.ReadCompetitorDeskAsync();
        var plan = FisStartOrder.FirstRun(men.Id, men.Values, Gender.Male, desk.Competitors.Select((x, i) => new DrawEntrant(x.Id, x.Values, 10 + i)).ToArray(),
            new("1327", s_fissiDate, s_fissiDate), new(), "fissi-draw");
        await workspace.SaveStartListAsync(new(plan, desk.Revision, "Operator", "Synthetic draw", DateTimeOffset.UnixEpoch));
        release.SetResult();
        await update;
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal("Start list saved, not updated", vm.FissiRaces[0].Status);
        Assert.Equal(DeskChangeKind.Add, Assert.Single(vm.DeskChangeLog).Kind);
        await vm.CommitDeskChangesCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal(3, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
    });

    [AvaloniaFact]
    public Task TheApiUpdateUsesTheSavedKeyAndIsStagedReviewedAndSaved() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = new FissiPages.ApiSite("fissi_synthetic"); using var http = new HttpClient(site);
        site.Races["0507", s_fissiDate] = FissiPages.ApiRace("0507", "2026-11-28", "SL", "Men", new FissiPages.Athlete("Testi ESIMERKKI", "100001"), new FissiPages.Athlete("Uusi TULOKAS", "100002"));
        site.Races["5489", s_fissiDate] = FissiPages.ApiRace("5489", "2026-11-28", "SL", "Women", new FissiPages.Athlete("Tiina ESIMERKKI", "200001"));
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        var series = await CreateFissiSeriesAsync(workspace, path);
        var men = series.Competitions.Single(x => x.Values.ShortLabel == "SL M").Id;
        var fisKey = new ReportSettingsCredential(); var fissiKey = new ReportSettingsCredential();
        using var vm = new MainViewModel(workspace, new FileDialogsStub { NewPath = path, OpenPath = path, BackupPath = path + ".bak" },
            fisStore: new FisLocalStore(folder, fisKey, fissiCredential: fissiKey), recentSeriesStore: new RecentSeriesStore(folder), informationHttp: http);
        var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 1000 };
        window.Show();
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        // Without a key nothing is sent.
        await vm.UpdateFromFissiApiCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Contains("Settings → API keys", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(site.Requests);

        // The key has its own field, last in Settings > API keys, and its own credential.
        vm.SelectedSettingsTab = 0; vm.ShowSettingsCommand.Execute(null);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var settings = window.GetVisualDescendants().OfType<SettingsView>().Single();
        Assert.Equal("API keys", ((TabItem)settings.FindControl<TabControl>("SettingsTabs")!.SelectedItem!).Header);
        TextBox Box(string name) => settings.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == name);
        double Y(string name) => Box(name).TranslatePoint(default, window)!.Value.Y;
        Assert.True(Y("FisApiKeyInputBox") < Y("FisMemberTokenInputBox") && Y("FisMemberTokenInputBox") < Y("FissiApiKeyInputBox"));
        Assert.Equal('•', Box("FissiApiKeyInputBox").PasswordChar);
        Box("FissiApiKeyInputBox").Text = " fissi_synthetic ";
        Click(window, "Save regs.fissi.fi key");
        Assert.Equal("fissi_synthetic", fissiKey.Read()); Assert.Null(fisKey.Read());
        Assert.Empty(vm.FissiApiKeyInput); Assert.Equal("Saved for this Windows user", vm.FissiApiKeyStatus);

        vm.ShowCompetitorsCommand.Execute(null);
        vm.IsFissiApiPanelOpen = true;
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        var button = window.FindControl<Button>("UpdateFromFissiApiButton")!;
        Assert.True(button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        button.Command!.Execute(null);
        await vm.UpdateFromFissiApiCommand.ExecutionTask!;
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsError, vm.StatusMessage);
        Assert.Equal(["SL M", "SL W", "SL X"], vm.FissiApiRaces.Select(x => x.Race));
        Assert.Equal(("2", "1", "1", "1", "Review and save"), (vm.FissiApiRaces[0].Registered, vm.FissiApiRaces[0].New, vm.FissiApiRaces[0].Added, vm.FissiApiRaces[0].Removed, vm.FissiApiRaces[0].Status));
        Assert.Empty(vm.FissiRaces);
        Assert.True(window.FindControl<DataGrid>("FissiApiRacesGrid")!.IsVisible);
        Assert.Equal(2, (await workspace.ReadCompetitorDeskAsync()).Competitors.Count);
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            window.FindControl<Expander>("FissiApiSection")!.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            CaptureFissi(window, Path.Combine(output, "fissi-api.png"));
        }
        await vm.CommitDeskChangesCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage);
        var saved = await workspace.ReadCompetitorDeskAsync();
        Assert.Equal(["100001", "100002", "100009", "200001"], saved.Competitors.Select(x => x.Values.FederationCode!).Order());
        // The API has no club: the saved one stays.
        Assert.Equal("Old Club", saved.Competitors.Single(x => x.Values.FederationCode == "100001").Values.Club);
        Assert.True(IsEntered(saved, "100002", men)); Assert.False(IsEntered(saved, "100009", men));
        await vm.UpdateFromFissiApiCommand.ExecuteAsync(null);
        Assert.False(vm.IsError, vm.StatusMessage); Assert.Empty(vm.DeskChangeLog);

        // A rejected key stages nothing and is never shown.
        fissiKey.Save("fissi_wrong");
        await vm.UpdateFromFissiApiCommand.ExecuteAsync(null);
        Assert.True(vm.IsError); Assert.Contains("rejected the API key", vm.StatusMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("fissi_wrong", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Empty(vm.DeskChangeLog); Assert.Empty(vm.FissiApiRaces);
        vm.RemoveFissiApiKeyCommand.Execute(null);
        Assert.Null(fissiKey.Read()); Assert.Equal("Not configured", vm.FissiApiKeyStatus);
        window.Close();
    });

    private static void CaptureFissi(MainWindow window, string file)
    {
        using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize(1366, 1000), new Avalonia.Vector(96, 96));
        bitmap.Render(window); bitmap.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public Task TheUpdateSectionSharesThePageEdges() => InFissiFolderAsync(async (folder, path) =>
    {
        using var site = FissiSite(); using var http = new HttpClient(site);
        await using var workspace = new SeriesWorkspace(new SqliteSeriesFileStore());
        await CreateFissiSeriesAsync(workspace, path);
        using var vm = FissiViewModel(workspace, folder, path, http);
        var window = new MainWindow { DataContext = vm, WindowState = WindowState.Normal, Width = 1366, Height = 1000 };
        window.Show();
        await vm.OpenSeriesCommand.ExecuteAsync(null);
        vm.ShowCompetitorsCommand.Execute(null);
        vm.IsFissiPanelOpen = true;
        await vm.UpdateFromFissiCommand.ExecuteAsync(null);
        window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        var section = window.FindControl<Expander>("FissiSection")!;
        var fis = window.GetVisualDescendants().OfType<Expander>().Single(x => Equals(x.Header, "Update from FIS"));
        Assert.Equal(fis.Bounds.X, section.Bounds.X); Assert.Equal(fis.Bounds.Width, section.Bounds.Width);
        // The API update is above the deprecated one.
        var api = window.FindControl<Expander>("FissiApiSection")!;
        Assert.Equal(("Update from regs.fissi.fi", "Update from regs.fissi.fi (deprecated Web Crawl)"), (api.Header, section.Header));
        Assert.Equal(fis.Bounds.X, api.Bounds.X); Assert.Equal(fis.Bounds.Width, api.Bounds.Width);
        Assert.True(fis.Bounds.Y < api.Bounds.Y && api.Bounds.Y < section.Bounds.Y);
        var output = Environment.GetEnvironmentVariable("OPENSKITIME_M7_VISUAL_DIR");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            section.BringIntoView(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            CaptureFissi(window, Path.Combine(output, "fissi-update.png"));
        }
        window.Close();
    });
}
