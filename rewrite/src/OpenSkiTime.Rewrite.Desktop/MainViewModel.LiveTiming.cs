using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using OpenSkiTime.LiveTiming;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Devices;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private ObservableCollection<LiveTimingChannel>? _liveChannels;
    public ObservableCollection<LiveTimingChannel> LiveChannels => _liveChannels ??= [
        new(this, "FIS Live Timing", PublisherKind.FisHttps), new(this, "Standalone Local", PublisherKind.Local), new(this, "Standalone Cloud", PublisherKind.Cloud)];
    [ObservableProperty] private string _liveCloudEndpoint = "https://live.openskiti.me";
    [ObservableProperty] private string _liveLocalEndpoint = "http://localhost:5078";
    [ObservableProperty] private string _liveFisHttpsEndpoint = "https://livedata.fis-ski.com/al/";
    [ObservableProperty] private string _liveFisTcpHost = "live.fis-ski.com";
    [ObservableProperty] private int _liveFisTcpPort = 1550;
    [ObservableProperty] private string _liveFisPassword = "";
    [ObservableProperty] private bool _liveFisUseTcp;
    [ObservableProperty] private string _liveTimeZone = TimeZoneInfo.Local.Id;
    [ObservableProperty] private string _liveTimingError = "";
    private readonly SemaphoreSlim _liveControlGate = new(1,1);
    private DispatcherTimer? _liveTimer;
    private Guid? _liveCompetitionId;
    private LiveSnapshot? _liveSnapshot;
    private TimingSnapshot? _liveObservedTiming;
    private StartListRevision[] _liveLists = [];
    private long _liveVersion;
    private bool _liveDisposed;

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Live timing is an optional downstream boundary; failures must not escape into timing UI/capture.")]
    internal async Task StartLiveChannelAsync(LiveTimingChannel channel)
    {
        channel.Busy = true;
        await _liveControlGate.WaitAsync();
        try
        {
            if (TimingCompetition is null || _timingList is null || workspace.Timing?.Snapshot is null)
            { throw new LiveValidationException("Choose a timing run with a saved start list first."); }
            if (channel.IsFis && TimingCompetition.Values.RaceType != RaceType.Fis)
            { throw new LiveValidationException("FIS Live Timing is available only for FIS competitions."); }
            if (_liveCompetitionId != TimingCompetition.Id)
            {
                await ResetLivePublishersAsync();
                _liveCompetitionId = TimingCompetition.Id;
                _liveSnapshot = null;
            }
            await BuildLiveSnapshotAsync();
            var options = channel.Kind switch
            {
                PublisherKind.Local => new PublisherOptions(PublisherKind.Local, LiveLocalEndpoint, LocalServerAssembly: LiveArtifact("Server")),
                PublisherKind.Cloud => new(PublisherKind.Cloud, LiveCloudEndpoint),
                _ => new(LiveFisUseTcp ? PublisherKind.FisTcp : PublisherKind.FisHttps, LiveFisUseTcp ? LiveFisTcpHost : LiveFisHttpsEndpoint, LiveFisPassword, LiveFisTcpPort)
            };
            if (_liveDisposed) { return; }
            if (channel.Options is not null && channel.Options != options)
            { await channel.Process.DisposeAsync(); channel.Process = new(); channel.Process.Offer(_liveSnapshot!); channel.SavedToken = null; }
            channel.Options = options;
            if (channel.Kind == PublisherKind.Cloud && OperatingSystem.IsWindows())
            {
                channel.CredentialTarget = "OpenSkiTime.LiveTiming:" + TimingCompetition.Id.ToString("N") + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Endpoint)))[..16];
                if (channel.Process.ResumeSession is null && new WindowsCredentialStore(channel.CredentialTarget).Read() is { } saved)
                { channel.Process.ResumeSession = JsonSerializer.Deserialize<LiveSession>(saved, LiveJson.Options); }
            }
            await channel.Process.StartAsync(LiveArtifact("Worker"), options);
            _liveTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => PollLiveTiming());
            _liveTimer.Start(); LiveTimingError = "";
        }
        catch (Exception ex) when (ex is LiveValidationException or IOException or InvalidOperationException or ArgumentException or TimeZoneNotFoundException or TimeoutException or SeriesFileException or JsonException)
        { LiveTimingError = ex is LiveValidationException ? ex.Message : "Live timing could not start. Check endpoint, time zone and installed worker/server files."; }
        catch (Exception) { LiveTimingError = "Live timing could not start. Timing capture continues; check installed live timing components."; }
        finally { _liveControlGate.Release(); channel.Busy = false; channel.Update(); }
    }
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Optional process control cannot interrupt authoritative capture.")]
    internal async Task ControlLiveChannelAsync(LiveTimingChannel channel, string command)
    {
        channel.Busy = true;
        await _liveControlGate.WaitAsync();
        try
        {
            if (command == "refresh") { await BuildLiveSnapshotAsync(); }
            await channel.Process.CommandAsync(command); LiveTimingError = "";
        }
        catch (IOException) { LiveTimingError = "Live timing worker is stopped. Press Start."; }
        catch (Exception) { LiveTimingError = "Live timing control failed. Timing capture continues; restart the live publisher."; }
        finally { _liveControlGate.Release(); channel.Busy = false; channel.Update(); }
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
            LiveSnapshotMapper.Competitors(selected), activeList.Plan.RunNumber, runs.ToArray(), DateTimeOffset.UtcNow);
        state.Validate(); _liveSnapshot = state; _liveLists = selected; _liveObservedTiming = current;
        foreach (var item in LiveChannels) { item.Process.Offer(state); }
    }
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Isolate optional projection/IPC/health errors from the authoritative timing UI.")]
    private void PollLiveTiming()
    {
        if (_liveChannels is null) { return; }
        try
        {
        foreach (var channel in _liveChannels)
        {
            channel.Update();
            if (channel.CredentialTarget is { } target && OperatingSystem.IsWindows())
            {
                try
                {
                    if (channel.Process.ResumeSession is { } session && session.PublisherToken != channel.SavedToken)
                    { new WindowsCredentialStore(target).Save(JsonSerializer.Serialize(session,LiveJson.Options)); channel.SavedToken = session.PublisherToken; }
                    else if (channel.Process.ResumeSession is null && channel.SavedToken is not null)
                    { new WindowsCredentialStore(target).Remove(); channel.SavedToken = null; }
                }
                catch (IOException) { LiveTimingError = "Could not retain the cloud session credential in Windows Credential Manager. Publishing continues."; }
            }
        }
        try
        {
            if (_liveCompetitionId != TimingCompetition?.Id)
            {
                _liveTimer?.Stop();
                _liveCompetitionId = null; _liveSnapshot = null;
                _ = ResetAfterSelectionChangeAsync(); return;
            }
            var current = workspace.Timing?.Snapshot;
            if (current is null || ReferenceEquals(current, _liveObservedTiming) || _liveSnapshot is null || _timingList is null || current.ListId != _timingList.Id) { return; }
            var list = _timingList;
            _liveLists = _liveLists.Where(x => x.Plan.RunNumber != list.Plan.RunNumber).Append(list).ToArray();
            var run = LiveSnapshotMapper.MapRun(list, current, TimeZoneInfo.FindSystemTimeZoneById(LiveTimeZone), DateTimeOffset.UtcNow);
            _liveSnapshot = _liveSnapshot with { Version = ++_liveVersion, CurrentRun = TimingRun,
                Competitors = LiveSnapshotMapper.Competitors(_liveLists),
                Runs = _liveSnapshot.Runs.Where(x => x.Number != run.Number).Append(run).OrderBy(x => x.Number).ToArray(), UpdatedAt = DateTimeOffset.UtcNow };
            _liveSnapshot.Validate(); _liveObservedTiming = current;
            foreach (var channel in _liveChannels) { channel.Process.Offer(_liveSnapshot); }
        }
        catch (Exception ex) when (ex is LiveValidationException or ArgumentException or InvalidOperationException or TimeZoneNotFoundException)
        { LiveTimingError = "Live state needs review. Check race time zone and bib consistency. Timing capture continues."; }
        }
        catch (Exception) { LiveTimingError = "Live timing projection failed. Timing capture continues; stop and restart the publisher."; }
    }
    private async Task ResetLivePublishersAsync()
    {
        if (_liveChannels is null) { return; }
        foreach (var channel in _liveChannels) { await channel.Process.DisposeAsync(); channel.Process = new(); channel.Options = null; channel.CredentialTarget = null; channel.SavedToken = null; channel.Update(); }
    }
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Background cleanup cannot escape into capture or dispose a newly started publisher.")]
    private async Task ResetAfterSelectionChangeAsync()
    {
        await _liveControlGate.WaitAsync();
        try { if (_liveCompetitionId is null && !_liveDisposed) { await ResetLivePublishersAsync(); } }
        catch (Exception) { LiveTimingError = "Live timing cleanup failed. Restart the live publisher; timing capture continues."; }
        finally { _liveControlGate.Release(); }
    }
    private void DisposeLiveTiming()
    {
        _liveDisposed = true;
        _liveTimer?.Stop();
        // Dispose closes private pipes and kills only owned downstream process trees. No timing lock is acquired.
        if (_liveChannels is not null) { foreach (var c in _liveChannels) { _ = c.Process.DisposeAsync().AsTask(); } }
    }
    private static string DisciplineCode(Discipline value) => value switch
    { Discipline.Slalom => "SL", Discipline.GiantSlalom => "GS", Discipline.SuperG => "SG", Discipline.Downhill => "DH", Discipline.AlpineCombined => "SC", _ => "Other" };
    private static string LiveArtifact(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LiveTiming", name, $"OpenSkiTime.LiveTiming.{name}.dll");
        if (!File.Exists(path)) { throw new IOException("Live timing artifact missing."); }
        return path;
    }
    internal static async Task CopyLiveUrlAsync(string url)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && TopLevel.GetTopLevel(desktop.MainWindow!)?.Clipboard is { } clipboard)
        { await clipboard.SetTextAsync(url); }
    }
}
