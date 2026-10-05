using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.LiveTiming.Client;
using OpenSkiTime.Application;
using OpenSkiTime.Devices;
using OpenSkiTime.Domain;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

public sealed partial class MainViewModel
{
    private ObservableCollection<LiveTimingChannel>? _liveChannels;
    public ObservableCollection<LiveTimingChannel> LiveChannels => _liveChannels ??= [
        new(this, "Local", PublisherKind.Local), new(this, "Cloud", PublisherKind.Cloud), new(this, "FIS", PublisherKind.FisHttps)];
    [ObservableProperty] private string _liveCloudEndpoint = "https://live.openskiti.me";
    [ObservableProperty] private string _liveLocalEndpoint = "http://localhost:5078";
    [ObservableProperty] private string _liveFisHttpsEndpoint = "https://livedata.fis-ski.com/al/";
    [ObservableProperty] private string _liveFisTcpHost = "live.fis-ski.com";
    [ObservableProperty] private int _liveFisTcpPort = 1550;
    [ObservableProperty] private string _liveFisPassword = "";
    [ObservableProperty] private bool _liveFisUseTcp;
    [ObservableProperty] private string _liveTimeZone = TimeZoneInfo.Local.Id;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasLiveTimingError))] private string _liveTimingError = "";
    public bool HasLiveTimingError => !string.IsNullOrEmpty(LiveTimingError);
    private readonly SemaphoreSlim _liveControlGate = new(1,1);
    private DispatcherTimer? _liveTimer;
    private Guid? _liveCompetitionId;
    private LiveSnapshot? _liveSnapshot;
    private TimingSnapshot? _liveObservedTiming;
    private StartListRevision[] _liveLists = [];
    private long _liveVersion;
    private bool _liveDisposed;
    private ControlPanelProcess? _livePanel;
    public int? LiveControlPanelProcessId => _livePanel?.ProcessId;
    private LiveConnectionSettings LiveSettings() => new(LiveLocalEndpoint, LiveCloudEndpoint, LiveFisHttpsEndpoint,
        LiveFisTcpHost, LiveFisTcpPort, LiveFisPassword, LiveFisUseTcp, LiveTimeZone);

    [RelayCommand]
    private async Task OpenLiveTimingAsync()
    {
        await RunLiveControlAsync(async () =>
        {
            if (TimingCompetition is not null && _timingList is not null && workspace.Timing?.Snapshot is not null)
            { await BuildLiveSnapshotAsync(); }
            var existing = _livePanel?.IsConnected == true;
            await EnsureLivePanelAsync();
            if (existing) { await _livePanel!.RequestAsync(new(Guid.NewGuid(), ControlPanelAction.Activate)); }
        });
    }
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Read-only live projection requests cannot crash timing capture.")]
    private async Task EnsureLivePanelAsync()
    {
        if (_livePanel?.IsConnected == true) { return; }
        if (_livePanel is not null) { await _livePanel.DisposeAsync(); }
        foreach (var channel in LiveChannels) { channel.Update(new(channel.Kind, new(PublisherState.Stopped), null)); }
        _livePanel = new ControlPanelProcess();
        _livePanel.PrepareSnapshot = settings =>
        {
            var completion = new TaskCompletionSource<ControlPanelInput>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await _liveControlGate.WaitAsync();
                    try
                    {
                        _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
                        LiveTimeZone = settings.TimeZone;
                        await BuildLiveSnapshotAsync();
                        LiveLocalEndpoint = settings.LocalEndpoint; LiveCloudEndpoint = settings.CloudEndpoint;
                        LiveFisHttpsEndpoint = settings.FisHttpsEndpoint; LiveFisTcpHost = settings.FisTcpHost;
                        LiveFisTcpPort = settings.FisTcpPort; LiveFisUseTcp = settings.FisUseTcp; LiveFisPassword = settings.FisPassword;
                        completion.TrySetResult(new(Guid.Empty, ControlPanelAction.State, _liveCompetitionId, _liveSnapshot));
                    }
                    finally { _liveControlGate.Release(); }
                }
                catch (Exception)
                { completion.TrySetResult(new(Guid.Empty, ControlPanelAction.State, PreparationError: "Select a saved timing run and check the race time zone.")); }
            });
            return completion.Task;
        };
        var suffix = OperatingSystem.IsWindows() ? ".exe" : "";
        var path = Path.Combine(AppContext.BaseDirectory, "LiveTiming", "ControlPanel", "OpenSkiTime.LiveTiming.ControlPanel" + suffix);
        if (!File.Exists(path)) { throw new IOException("Live timing control panel executable missing."); }
        await _livePanel.StartAsync(path, LiveSettings());
        if (_liveCompetitionId is { } id && _liveSnapshot is { } snapshot) { _livePanel.Offer(id, snapshot); }
        _liveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => PollLiveTiming());
        _liveTimer.Start();
    }
    internal Task StartLiveChannelAsync(LiveTimingChannel channel) => RunLiveControlAsync(async () =>
    {
        await BuildLiveSnapshotAsync();
        await EnsureLivePanelAsync();
        var result = await _livePanel!.RequestAsync(new(Guid.NewGuid(), ControlPanelAction.Start, _liveCompetitionId, _liveSnapshot, channel.Kind, LiveSettings()));
        ApplyLivePanelState(result);
    });
    internal Task ControlLiveChannelAsync(LiveTimingChannel channel, string command) => RunLiveControlAsync(async () =>
    {
        if (_livePanel?.IsConnected != true) { throw new IOException("Live timing panel is closed."); }
        if (command == "refresh") { await BuildLiveSnapshotAsync(); }
        var action = command switch
        {
            "stop" => ControlPanelAction.Stop, "refresh" => ControlPanelAction.Refresh,
            "delete" => ControlPanelAction.Delete, "delete-all" => ControlPanelAction.DeleteAll,
            _ => throw new LiveValidationException("Unknown live command.")
        };
        ApplyLivePanelState(await _livePanel.RequestAsync(new(Guid.NewGuid(), action, _liveCompetitionId, _liveSnapshot, channel.Kind)));
    });
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Live timing cannot interrupt authoritative timing capture.")]
    private async Task RunLiveControlAsync(Func<Task> action)
    {
        await _liveControlGate.WaitAsync();
        try { if (!_liveDisposed) { LiveTimingError = ""; await action(); } }
        catch (LiveValidationException ex) { LiveTimingError = ex.Message; }
        catch (Exception) { LiveTimingError = "Live timing control failed. Reopen the panel and check the selected run, time zone and installed components. Timing capture continues."; }
        finally { _liveControlGate.Release(); }
    }
    private async Task BuildLiveSnapshotAsync()
    {
        var race = TimingCompetition ?? throw new LiveValidationException("Choose a timing competition.");
        var activeList = _timingList ?? throw new LiveValidationException("Choose a saved timing run.");
        var timing = workspace.Timing ?? throw new LiveValidationException("Timing state unavailable.");
        var lists = await workspace.ReadStartListsAsync(race.Id);
        var selected = lists.Revisions.GroupBy(x => x.Plan.RunNumber).Select(g => g.OrderByDescending(x => x.Revision).First())
            .Where(x => x.Plan.RunNumber != activeList.Plan.RunNumber).Append(activeList).OrderBy(x => x.Plan.RunNumber).ToArray();
        var runs = new List<LiveRun>(); var zone = TimeZoneInfo.FindSystemTimeZoneById(LiveTimeZone);
        var current = timing.Snapshot ?? throw new LiveValidationException("Timing snapshot unavailable.");
        foreach (var list in selected)
        {
            var data = await workspace.ReadTimingAsync(list.Id);
            var snapshot = list.Id == current.ListId ? current : TimingReplay.Restore(data, new AlgeDecoderFactory());
            if (data.Sessions.Count == 0 && activeList.Plan.SourceListId == list.Id && activeList.Plan.SourceResults.Count > 0)
            {
                snapshot = snapshot with { Results = snapshot.Results.Select(r =>
                {
                    var source = activeList.Plan.SourceResults.FirstOrDefault(x => x.CompetitorId == r.CompetitorId);
                    return source is null ? r : r with { Status = Enum.Parse<TimingStatus>(source.Status.ToString()), Hundredths = source.Hundredths };
                }).ToArray() };
            }
            runs.Add(LiveSnapshotMapper.MapRun(list, snapshot, zone, DateTimeOffset.UtcNow));
        }
        if (TimingCompetition?.Id != race.Id || _timingList?.Id != activeList.Id || _liveDisposed)
        { throw new LiveValidationException("Timing selection changed. Start live timing for the selected race."); }
        var c = race.Values;
        var state = new LiveSnapshot(++_liveVersion, new(c.Name, c.Calendar?.Location ?? _current?.Values.Location ?? "", DisciplineCode(c.Discipline),
            c.Date, c.RaceType == RaceType.Fis, c.FisCode ?? "", activeList.Plan.Gender == Gender.Female ? "L" : "M", c.Calendar?.Category ?? "FIS", c.IntermediateCount, c.CourseName ?? ""),
            LiveSnapshotMapper.Competitors(selected), activeList.Plan.RunNumber, LiveSnapshotMapper.WithTotals(runs), DateTimeOffset.UtcNow);
        state.Validate(); _liveSnapshot = state; _liveLists = selected; _liveObservedTiming = current;
        _liveCompetitionId = race.Id; _livePanel?.Offer(race.Id, state);
    }
    private void ApplyLivePanelState(ControlPanelOutput state, bool updateError = true)
    {
        foreach (var channel in LiveChannels)
        {
            var health = state.Channels.FirstOrDefault(x => x.Kind == channel.Kind);
            if (health is not null) { channel.Update(health); }
        }
        if (updateError) { LiveTimingError = state.Error; }
    }
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional state projection cannot interrupt capture.")]
    private void PollLiveTiming()
    {
        if (_livePanel is null || _liveDisposed) { return; }
        try
        {
            ApplyLivePanelState(_livePanel.State, _livePanel.IsConnected);
            if (!_livePanel.IsConnected) { return; }
            if (_liveCompetitionId != TimingCompetition?.Id)
            {
                _liveCompetitionId = TimingCompetition?.Id; _liveSnapshot = null;
                _livePanel.ClearSnapshot();
                _ = RunLiveControlAsync(async () =>
                {
                    if (_livePanel?.IsConnected != true) { return; }
                    await _livePanel.RequestAsync(new(Guid.NewGuid(), ControlPanelAction.Reset));
                    if (TimingCompetition is not null && _timingList is not null && workspace.Timing?.Snapshot is not null)
                    { await BuildLiveSnapshotAsync(); }
                });
                return;
            }
            var current = workspace.Timing?.Snapshot;
            if (current is null || ReferenceEquals(current, _liveObservedTiming) || _liveSnapshot is null || _timingList is null || current.ListId != _timingList.Id) { return; }
            var list = _timingList;
            _liveLists = _liveLists.Where(x => x.Plan.RunNumber != list.Plan.RunNumber).Append(list).ToArray();
            var run = LiveSnapshotMapper.MapRun(list, current, TimeZoneInfo.FindSystemTimeZoneById(LiveTimeZone), DateTimeOffset.UtcNow);
            _liveSnapshot = _liveSnapshot with { Version = ++_liveVersion, CurrentRun = TimingRun,
                Competitors = LiveSnapshotMapper.Competitors(_liveLists),
                Runs = LiveSnapshotMapper.WithTotals(_liveSnapshot.Runs.Where(x => x.Number != run.Number).Append(run)), UpdatedAt = DateTimeOffset.UtcNow };
            _liveSnapshot.Validate(); _liveObservedTiming = current;
            _livePanel.Offer(_liveCompetitionId!.Value, _liveSnapshot);
        }
        catch (Exception) { LiveTimingError = "Live timing state could not update. Reopen the panel and check race settings. Timing capture continues."; }
    }
    private void DisposeLiveTiming()
    {
        _liveDisposed = true; _liveTimer?.Stop();
        if (_livePanel is not null) { _ = _livePanel.DisposeAsync().AsTask(); }
    }
    private static string DisciplineCode(Discipline value) => value switch
    { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG", Discipline.Downhill => "DH", Discipline.AlpineCombined => "SC", _ => "Other" };
    internal static async Task CopyLiveUrlAsync(string url)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && TopLevel.GetTopLevel(desktop.MainWindow!)?.Clipboard is { } clipboard)
        { await clipboard.SetTextAsync(url); }
    }
}
