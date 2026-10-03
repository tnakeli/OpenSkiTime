using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Rewrite.Application;
using OpenSkiTime.Rewrite.Domain;
using OpenSkiTime.Rewrite.Recognition;
using OpenSkiTime.Rewrite.Timing;

namespace OpenSkiTime.Rewrite.Desktop;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _reportImageCancellation;
    private string? _importDraftSnapshot;
    private string? _importTargetsSnapshot;
    private long _importSeriesRevision;
    private bool _reportRefreshingImportChoices;
    private bool _reportSelectingImportRow;
    private readonly TesseractTimingImageRecognizer _reportRecognizer = new();
    public ObservableCollection<TimingReportImportRow> ReportImportPreview { get; } = [];
    public ObservableCollection<TimingReportImage> ReportImages { get; } = [];
    public IReadOnlyList<TimingReportImageRole> ReportImageRoles { get; } = Enum.GetValues<TimingReportImageRole>();
    public IReadOnlyList<int> ReportImportRuns => ReportRuns.Select(x => x.Run).ToArray();
    [ObservableProperty] private int? _reportImportRun;
    [ObservableProperty] private TimingReportImageRole _reportImageRole = TimingReportImageRole.B;
    [ObservableProperty] private string _reportImageDevice = "";
    [ObservableProperty] private string _reportImportStatus = "Drop one or more receipt/screen images from the same device here. Select its role and run first.";
    [ObservableProperty] private bool _reportImportVerified;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptReportImageMatchesCommand))]
    private bool _reportImportPreviewStale;
    [ObservableProperty] private bool _replaceReportEvidence;
    [ObservableProperty] private TimingReportImportRow? _selectedReportImportRow;
    [ObservableProperty] private TimingReportImage? _selectedReportImage;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportImageWidth))]
    private Bitmap? _reportImagePreview;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReportImageHorizontalScrolling))]
    [NotifyPropertyChangedFor(nameof(ReportImageWidth))]
    private bool _reportImageOriginalSize;
    public double ReportImageWidth => ReportImageOriginalSize && ReportImagePreview is { } bitmap ? bitmap.Size.Width : double.NaN;
    public Avalonia.Controls.Primitives.ScrollBarVisibility ReportImageHorizontalScrolling => ReportImageOriginalSize
        ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
    [ObservableProperty] private string _reportMatchTolerance = "1.0";
    partial void OnSelectedReportImportRowChanged(TimingReportImportRow? value)
    { if (!_reportSelectingImportRow && value?.ImageId is { } id) { SelectedReportImage = ReportImages.FirstOrDefault(x => x.Id == id); } }
    partial void OnSelectedReportImageChanged(TimingReportImage? value)
    {
        var old = ReportImagePreview;
        try
        {
            using var stream = value is null ? null : new MemoryStream(value.Bytes, writable: false);
            ReportImagePreview = stream is null ? null : new Bitmap(stream);
        }
        catch (ArgumentException) { ReportImagePreview = null; }
        old?.Dispose();
        if (!_reportSelectingImportRow) { SelectReportPreviewRowForImage(); }
    }
    partial void OnReportImageRoleChanged(TimingReportImageRole value) { ReportMatchTolerance = value == TimingReportImageRole.B ? "1.0" : "2.0"; ClearReportPreview(); }
    partial void OnReportImportRunChanged(int? value) { if (!_reportRefreshingImportChoices) { ClearReportPreview(); } }
    private void ClearReportPreview()
    { ReportImportPreview.Clear(); SelectedReportImportRow = null; ReportImportVerified = false; _importDraftSnapshot = null; _importTargetsSnapshot = null; ReportImportPreviewStale = false; }

    private void SelectReportPreviewRowForImage()
    {
        if (SelectedReportImportRow?.ImageId == SelectedReportImage?.Id) { return; }
        var previous = _reportSelectingImportRow;
        _reportSelectingImportRow = true;
        try { SelectedReportImportRow = ReportImportPreview.FirstOrDefault(x => x.ImageId == SelectedReportImage?.Id); }
        finally { _reportSelectingImportRow = previous; }
    }

    private void UpdateReportPreviewValidity()
    {
        if (_importDraftSnapshot is null || ReportImportPreviewStale) { return; }
        var current = ReadReportDraft();
        var serialized = JsonSerializer.Serialize(current);
        var previous = JsonSerializer.Deserialize<TimingReportDraft>(_importDraftSnapshot);
        if (_importTargetsSnapshot == ReadReportImportTargetsSnapshot() && previous is not null
            && JsonSerializer.Serialize(previous with { SourceFingerprint = current.SourceFingerprint }) == serialized)
        {
            // New heartbeats or unused impulses do not alter the reviewed targets or
            // proposals. Advance the concurrency baseline without losing verification.
            _importDraftSnapshot = serialized; _importSeriesRevision = _current!.Revision;
            return;
        }
        ReportImportPreviewStale = true; ReportImportVerified = false;
        ReportImportStatus = "Timing data changed. Preview and selections kept; preview saved images again before accepting.";
    }

    private string ReadReportImportTargetsSnapshot() => JsonSerializer.Serialize(ReportEvidence.Where(x => x.Run == ReportImportRun)
        .Select(x => new { x.Run, x.Bib, x.AStartStamp, x.AFinishStamp }));

    public async Task ImportReportImagesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (IsReportBusy || _reportDraft is null || paths.Count == 0) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        using var cancel = new CancellationTokenSource(); _reportImageCancellation = cancel;
        try { await GuardAsync(async () =>
        {
            if (paths.Count > 50) { throw new DomainValidationException("Import at most 50 images in one batch."); }
            var draft = _reportDraft; var file = workspace.FilePath;
            var role = ReportImageRole; var run = ReportImportRun ?? throw new DomainValidationException("Choose a saved run before importing its receipts."); var label = ReportImageDevice.Trim();
            if (!ReportRuns.Any(x => x.Run == run)) { throw new DomainValidationException("Choose a saved run before importing its receipts."); }
            var sources = new List<TimingReportImage>(); long total = 0;
            foreach (var path in paths)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff"))
                { throw new DomainValidationException("Choose PNG, JPEG, BMP or TIFF images of printed receipts or screens."); }
                var info = new FileInfo(path);
                total += info.Length;
                if (info.Length > 20_000_000 || total > 100_000_000)
                { throw new DomainValidationException("Images must be at most 20 MB each and 100 MB per batch."); }
                var bytes = await File.ReadAllBytesAsync(path, cancel.Token);
                ReportImportStatus = $"Reading image {sources.Count + 1}/{paths.Count} locally - not saved yet.";
                TimingImageText text;
                try { text = await _reportRecognizer.RecognizeAsync(bytes, cancel.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                {
                    // Preserve the original even when OCR cannot interpret it. No image content is logged.
                    text = new("Recognition failed; review original", []);
                }
                sources.Add(new(Guid.NewGuid(), draft.CompetitionId, role, Path.GetFileName(path),
                    extension == ".png" ? "image/png" : extension is ".jpg" or ".jpeg" ? "image/jpeg" : "image/" + extension.TrimStart('.'),
                    bytes, string.Join("\n", text.Lines.Select(x => x.Text)), text.Engine, DateTimeOffset.UtcNow)
                    { RunNumber = run, DeviceLabel = label });
            }
            if (_reportDraft?.CompetitionId != draft.CompetitionId || file != workspace.FilePath)
            { throw new DomainValidationException("The report changed during recognition. Import the images again in their original report."); }
            var series = await workspace.ReadAsync();
            await workspace.SaveTimingReportImagesAsync(sources, series.Revision, cancel.Token);
            _current = await workspace.ReadAsync();
            await LoadReportImagesCoreAsync();
            await PreviewReportImagesCoreAsync(sources);
        }); }
        catch (OperationCanceledException) { ReportImportStatus = "Image reading cancelled. No proposed matches were applied."; }
        finally { _reportImageCancellation = null; IsReportBusy = false; }
    }

    [RelayCommand] private void CancelReportImages() => _reportImageCancellation?.Cancel();

    private async Task LoadReportImagesCoreAsync()
    {
        if (_reportDraft is null) { return; }
        var selectedId = SelectedReportImage?.Id;
        var images = await workspace.ReadTimingReportImagesAsync(_reportDraft.CompetitionId);
        // Originals are immutable. Reuse the existing objects instead of resetting the
        // collection and letting the ComboBox replace the operator's selection.
        foreach (var old in ReportImages.Where(x => !images.Any(i => i.Id == x.Id)).ToArray()) { ReportImages.Remove(old); }
        for (var index = 0; index < images.Count; index++)
        {
            var existing = ReportImages.FirstOrDefault(x => x.Id == images[index].Id);
            if (existing is null) { ReportImages.Insert(index, images[index]); }
            else if (ReportImages.IndexOf(existing) != index) { ReportImages.Move(ReportImages.IndexOf(existing), index); }
        }
        SelectedReportImage = ReportImages.FirstOrDefault(x => x.Id == selectedId)
            ?? ReportImages.FirstOrDefault(x => x.Role == ReportImageRole && x.RunNumber == ReportImportRun)
            ?? ReportImages.FirstOrDefault();
    }

    [RelayCommand]
    private async Task PreviewSavedReportImagesAsync()
    {
        if (IsReportBusy || _reportDraft is null) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        await GuardAsync(async () =>
        {
            await LoadReportImagesCoreAsync();
            var selected = ReportImages.Where(x => x.Role == ReportImageRole && x.RunNumber == ReportImportRun
                && (ReportImageDevice.Length == 0 || x.DeviceLabel == ReportImageDevice.Trim())).ToArray();
            await PreviewReportImagesCoreAsync(selected);
        });
        IsReportBusy = false;
    }

    private async Task PreviewReportImagesCoreAsync(IReadOnlyList<TimingReportImage> images)
    {
        if (_reportDraft is null) { return; }
        _reportSelectingImportRow = true;
        try
        {
        if (!decimal.TryParse(ReportMatchTolerance, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 60)
        { throw new DomainValidationException("Match tolerance must be 0-60 seconds (greater than zero)."); }
        var tolerance = checked((long)(seconds * TimeSpan.TicksPerSecond));
        ClearReportPreview();
        var date = _reportDraft.Header.Date;
        var evidence = images.SelectMany(image => image.RecognizedText.Split('\n').SelectMany((line, index) =>
            TimingEvidenceMatching.ParseLine($"image:{image.Id:D}:{index}", line, date,
                image.Role == TimingReportImageRole.B ? null : image.Role == TimingReportImageRole.HandStart ? 0 : 1))).ToArray();
        var run = ReportImportRun ?? throw new DomainValidationException("Choose a saved run before reviewing its receipts.");
        var targets = ReportTargets(run, ReportImageRole);
        foreach (var match in TimingEvidenceMatching.Match(targets, evidence, tolerance, allowAdjacentDay: true))
        {
            var stamp = match.Evidence is { } item ? new TimingReportStamp(item.Ticks, item.Precision, item.Key, Verified: false) : null;
            var keyParts = stamp?.SourceReference.Split(':');
            ReportImportPreview.Add(new() { Target = ReportEvidence.Single(x => x.Run == ReportImportRun && x.Bib == match.Target.Bib),
                Channel = match.Target.Channel, Role = ReportImageRole, Stamp = stamp, Accept = stamp is not null,
                Difference = match.DifferenceTicks is { } delta ? (delta / (decimal)TimeSpan.TicksPerSecond).ToString("+0.0000000;-0.0000000;0", CultureInfo.InvariantCulture) : "",
                State = match.State, SourceText = match.Evidence?.Text ?? "",
                ImageId = keyParts?.Length >= 2 && Guid.TryParse(keyParts[1], out var id) ? id : null });
        }
        _importDraftSnapshot = JsonSerializer.Serialize(ReadReportDraft());
        _importTargetsSnapshot = ReadReportImportTargetsSnapshot();
        _importSeriesRevision = (await workspace.ReadAsync()).Revision;
        SelectedReportImportRow = ReportImportPreview.FirstOrDefault(x => x.ImageId == SelectedReportImage?.Id && x.CanAccept);
        ReportImportStatus = $"{images.Count} original image(s) saved; {evidence.Length} timestamp(s) read; {ReportImportPreview.Count(x => x.CanAccept)} unique proposals. Check the images before accepting. Missing/ambiguous cells stay empty.";
        var disputed = images.Sum(image => image.RecognizedText.Split('\n')
            .Count(line => line.StartsWith(TimingEvidenceMatching.OcrReviewRequiredPrefix, StringComparison.Ordinal)));
        if (disputed > 0)
        { ReportImportStatus += $" {disputed} receipt row(s) have conflicting or clipped OCR readings: inspect Recognized text and enter verified values manually."; }
        SetStatus(ReportImportStatus);
        }
        finally { _reportSelectingImportRow = false; }
    }

    private bool CanAcceptReportImageMatches() => !IsReportBusy && !ReportImportPreviewStale;

    [RelayCommand(CanExecute = nameof(CanAcceptReportImageMatches))]
    private async Task AcceptReportImageMatchesAsync()
    {
        if (IsReportBusy) { return; }
        IsReportBusy = true;
        await GuardAsync(async () =>
        {
            if (!ReportImportVerified) { throw new DomainValidationException("Check the original images and confirm the proposed timestamps first."); }
            var current = ReadReportDraft();
            if (_importDraftSnapshot is null || _importDraftSnapshot != JsonSerializer.Serialize(current))
            { throw new DomainValidationException("The report changed after this preview. Preview the saved images again."); }
            var latest = new List<TimingReplayData>();
            var desk = await workspace.ReadStartListsAsync(current.CompetitionId);
            foreach (var source in desk.Revisions.GroupBy(x => x.Plan.RunNumber).Select(g => g.MaxBy(x => x.Revision)!))
            { latest.Add(await workspace.ReadTimingAsync(source.Id)); }
            if (TimingReportProjection.Fingerprint(latest) != current.SourceFingerprint)
            { throw new DomainValidationException("A timing changed after this preview. Reopen Timing report, then preview the saved images again."); }
            var assignments = current.Associations.ToList(); var count = 0;
            foreach (var row in ReportImportPreview.Where(x => x.Accept && x.Stamp is not null))
            {
                var existing = assignments.FindIndex(x => x.Run == row.Run && x.Bib == row.Bib && x.Channel == row.Channel && x.Role == row.Role);
                if (existing >= 0 && !ReplaceReportEvidence) { continue; }
                var replacement = new TimingReportAssociation(row.Run, row.Bib, row.Channel, row.Role, row.Stamp! with { Verified = true });
                if (existing >= 0) { assignments[existing] = replacement; } else { assignments.Add(replacement); }
                count++;
            }
            if (count == 0) { throw new DomainValidationException("No new matches selected. Existing values are preserved unless replacement is explicitly enabled."); }
            TimingReportBib Sample(int run, TimingReportBib sample)
            {
                TimingReportStamp? Find(TimingReportImageRole role, int channel) => assignments.FirstOrDefault(x => x.Run == run && x.Bib == sample.Bib && x.Role == role && x.Channel == channel)?.Stamp;
                return sample with { BStart = Find(TimingReportImageRole.B, 0), BFinish = Find(TimingReportImageRole.B, 1), HandStart = Find(TimingReportImageRole.HandStart, 0), HandFinish = Find(TimingReportImageRole.HandFinish, 1) };
            }
            var accepted = current with { Associations = assignments.ToArray(), Reviewed = false, CertifyFis = false,
                Runs = current.Runs.Select(r => r with { First = Sample(r.Run, r.First), Last = Sample(r.Run, r.Last) }).ToArray() };
            await workspace.ApplyTimingReportImportAsync(accepted, _importSeriesRevision, TimingOperator,
                "Operator verified image import: " + ReportChangeReason, DateTimeOffset.UtcNow);
            _reportDraft = accepted; _reportSaved = await workspace.ReadTimingReportAsync(current.CompetitionId);
            _current = await workspace.ReadAsync(); await PopulateReportAsync(accepted);
            HasTimingReportEdits = false; ClearReportPreview();
            ReportImportStatus = $"{count} verified timestamp(s) saved with source images and audit history.";
            ReportStatus = "Image assignments saved. Complete the report review.";
            SetStatus(ReportStatus);
        });
        IsReportBusy = false;
    }

    private void ResetReportImport()
    {
        _reportImageCancellation?.Cancel(); ClearReportPreview(); ReportImages.Clear(); SelectedReportImage = null;
    }
    private void DisposeReportUi() { DisposeReportAutosave(); _reportImageCancellation?.Cancel(); _reportSubmissionCancellation?.Cancel(); ReportImagePreview?.Dispose(); }
}
