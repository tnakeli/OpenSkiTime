using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Recognition;
using OpenSkiTime.Timing;

namespace OpenSkiTime.Desktop;

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
    public bool AllReportMatchesSelected => ReportImportPreview.Any(x => x.CanAccept)
        && ReportImportPreview.Where(x => x.CanAccept).All(x => x.Accept);
    public IReadOnlyList<TimingReportImageRole> ReportImageRoles { get; } = Enum.GetValues<TimingReportImageRole>();
    public IReadOnlyList<int> ReportImportRuns => ReportRuns.Select(x => x.Run).ToArray();
    [ObservableProperty] private int? _reportImportRun;
    [ObservableProperty] private TimingReportImageRole _reportImageRole = TimingReportImageRole.B;
    [ObservableProperty] private string _reportImportStatus = "Reading receipt images locally. Images and detected text remain in this dialog only.";
    [ObservableProperty] private bool _reportImportVerified;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AcceptReportImageMatchesCommand))]
    private bool _reportImportPreviewStale;
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
    partial void OnReportImageRoleChanged(TimingReportImageRole value)
    {
        ReportMatchTolerance = value == TimingReportImageRole.B ? "1.0" : "2.0"; ClearReportPreview();
        ReportDeviceHandChannel = value == TimingReportImageRole.HandFinish ? 1 : 0; NotifyReportDeviceRole();
    }
    partial void OnReportImportRunChanged(int? value) { if (!_reportRefreshingImportChoices) { ClearReportPreview(); } }
    private void ClearReportPreview()
    {
        foreach (var row in ReportImportPreview) { row.PropertyChanged -= ReportReceiptSelectionChanged; }
        ReportImportPreview.Clear(); SelectedReportImportRow = null; ReportImportVerified = false;
        _importDraftSnapshot = null; _importTargetsSnapshot = null; ReportImportPreviewStale = false;
        ReportImportWarnings = ""; _reportImportProvenance = ImageImportProvenance;
        OnPropertyChanged(nameof(AllReportMatchesSelected));
    }

    private void ReportReceiptSelectionChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(TimingReportImportRow.Accept)) { OnPropertyChanged(nameof(AllReportMatchesSelected)); } }

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
        ReportImportStatus = "Timing data changed. Close the dialog and read the images again before accepting.";
    }

    private string ReadReportImportTargetsSnapshot() => JsonSerializer.Serialize(ReportEvidence.Where(x => x.Run == ReportImportRun)
        .Select(x => new { x.Run, x.Bib, x.AStartStamp, x.AFinishStamp }));

    public Task ImportReportImagesAsync(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return ImportReportImageBatchAsync(async ct =>
        {
            if (paths.Count > 50) { throw new DomainValidationException("Read at most 50 images in one dialog."); }
            var inputs = new List<ReceiptImageInput>(); long total = 0;
            foreach (var path in paths)
            {
                var info = new FileInfo(path); total += info.Length;
                if (info.Length > 20_000_000 || total > 100_000_000)
                { throw new DomainValidationException("Images must be at most 20 MB each and 100 MB per dialog."); }
                inputs.Add(new(Path.GetFileName(path), await File.ReadAllBytesAsync(path, ct)));
            }
            return inputs;
        });
    }

    internal Task ImportReportImageBytesAsync(byte[] bytes, string fileName)
        => ImportReportImageBatchAsync(_ => Task.FromResult<IReadOnlyList<ReceiptImageInput>>([new(fileName, bytes)]));

    private sealed record ReceiptImageInput(string FileName, byte[] Bytes);

    private async Task ImportReportImageBatchAsync(Func<CancellationToken, Task<IReadOnlyList<ReceiptImageInput>>> read)
    {
        if (IsReportBusy || _reportDraft is null) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        using var cancel = new CancellationTokenSource(); _reportImageCancellation = cancel;
        try { await GuardAsync(async () =>
        {
            var draft = _reportDraft; var file = workspace.FilePath;
            var role = ReportImageRole; var run = ReportImportRun ?? throw new DomainValidationException("Choose a saved run before reading receipts.");
            if (!ReportRuns.Any(x => x.Run == run)) { throw new DomainValidationException("Choose a saved run before reading receipts."); }
            var inputs = await read(cancel.Token);
            if (inputs.Count == 0) { return; }
            if (inputs.Count + ReportImages.Count > 50) { throw new DomainValidationException("Read at most 50 images in one dialog."); }
            var sources = new List<TimingReportImage>(); long total = ReportImages.Sum(x => (long)x.Bytes.Length);
            foreach (var input in inputs)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var extension = Path.GetExtension(input.FileName).ToLowerInvariant();
                if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".tif" or ".tiff"))
                { throw new DomainValidationException("Choose PNG, JPEG, BMP or TIFF receipt/screen images."); }
                total += input.Bytes.Length;
                if (input.Bytes.Length > 20_000_000 || total > 100_000_000)
                { throw new DomainValidationException("Images must be at most 20 MB each and 100 MB per dialog."); }
                ReportImportStatus = $"Reading image {sources.Count + 1}/{inputs.Count} locally; images remain in memory.";
                TimingImageText text;
                try { text = await _reportRecognizer.RecognizeAsync(input.Bytes, cancel.Token); }
                catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                { text = new("Recognition failed; review original", []); }
                sources.Add(new(Guid.NewGuid(), draft.CompetitionId, role, input.FileName,
                    extension == ".png" ? "image/png" : extension is ".jpg" or ".jpeg" ? "image/jpeg" : "image/" + extension.TrimStart('.'),
                    input.Bytes, string.Join("\n", text.Lines.Select(x => x.Text)), text.Engine, DateTimeOffset.UtcNow) { RunNumber = run });
            }
            cancel.Token.ThrowIfCancellationRequested();
            if (_reportDraft?.CompetitionId != draft.CompetitionId || file != workspace.FilePath)
            { throw new DomainValidationException("The report changed during recognition. Read the images again in their original report."); }
            foreach (var source in sources) { ReportImages.Add(source); }
            SelectedReportImage = sources.FirstOrDefault();
            await PreviewReportImagesCoreAsync(ReportImages.Where(x => x.Role == role && x.RunNumber == run).ToArray());
        }); }
        catch (OperationCanceledException) { ReportImportStatus = "Image reading cancelled. No times were applied."; }
        finally { _reportImageCancellation = null; IsReportBusy = false; }
        if (IsError) { ReportImportStatus = StatusMessage; }
    }
    [RelayCommand] private void CancelReportImages() => _reportImageCancellation?.Cancel();

    internal async Task RebuildReportReceiptMatchesAsync()
    {
        if (IsReportBusy || _reportDraft is null) { return; }
        if (!await FlushTimingReportAsync()) { return; }
        IsReportBusy = true;
        await GuardAsync(async () =>
        {

            var selected = ReportImages.Where(x => x.Role == ReportImageRole && x.RunNumber == ReportImportRun).ToArray();
            await PreviewReportImagesCoreAsync(selected);
        });
        IsReportBusy = false;
    }

    private async Task PreviewReportImagesCoreAsync(TimingReportImage[] images)
    {
        if (_reportDraft is null) { return; }
        var date = _reportDraft.Header.Date;
        var evidence = images.SelectMany(image => image.RecognizedText.Split('\n').SelectMany((line, index) =>
            TimingEvidenceMatching.ParseLine($"image:{image.Id:D}:{index}", line, date,
                image.Role == TimingReportImageRole.B ? null : image.Role == TimingReportImageRole.HandStart ? 0 : 1))).ToArray();
        if (!await PreviewReportEvidenceCoreAsync(evidence, ImageImportProvenance)) { return; }
        ReportImportStatus = $"{images.Length} image(s) in memory; {evidence.Length} timestamp(s) read; {ReportImportPreview.Count(x => x.CanAccept)} report timestamps matched. Check selected timestamps before pressing OK. Unmatched fields remain unchanged.";
        var disputed = images.Sum(image => image.RecognizedText.Split('\n')
            .Count(line => line.StartsWith(TimingEvidenceMatching.OcrReviewRequiredPrefix, StringComparison.Ordinal)));
        if (disputed > 0)
        { ReportImportStatus += $" {disputed} receipt row(s) have conflicting or clipped OCR readings: inspect Recognized text and enter verified values manually."; }
        SetStatus(ReportImportStatus);
    }

    private const string ImageImportProvenance = "image import";
    // Describes the evidence behind the current preview for the accepted change reason (image/OCR or a device source).
    private string _reportImportProvenance = ImageImportProvenance;

    // One matching/preview path for every evidence input. OCR text and device impulses both arrive as
    // EvidenceTimestamp values on the same device clock time as A.
    private async Task<bool> PreviewReportEvidenceCoreAsync(IReadOnlyList<EvidenceTimestamp> evidence, string provenance)
    {
        if (_reportDraft is null) { return false; }
        _reportSelectingImportRow = true;
        try
        {
        if (!decimal.TryParse(ReportMatchTolerance, NumberStyles.Number, CultureInfo.InvariantCulture, out var seconds) || seconds is <= 0 or > 60)
        { throw new DomainValidationException("Match tolerance must be 0-60 seconds (greater than zero)."); }
        var tolerance = checked((long)(seconds * TimeSpan.TicksPerSecond));
        ClearReportPreview();
        var run = ReportImportRun ?? throw new DomainValidationException("Choose a saved run before reviewing its receipts.");
        var targets = ReportTargets(run, ReportImageRole);
        var matches = TimingEvidenceMatching.Match(targets, evidence, tolerance, allowAdjacentDay: true);
        _reportImportProvenance = provenance;
        foreach (var match in matches.OrderBy(x => Array.IndexOf(targets, x.Target)))
        {
            var stamp = match.Evidence is { } item ? new TimingReportStamp(item.Ticks, item.Precision, item.Key, Verified: false) : null;
            var keyParts = stamp?.SourceReference.Split(':');
            var target = ReportEvidence.Single(x => x.Run == run && x.Bib == match.Target.Bib);
            var current = ReportImageRole == TimingReportImageRole.B ? match.Target.Channel == 0 ? target.BStart : target.BFinish
                : ReportImageRole == TimingReportImageRole.HandStart ? target.HandStart : target.HandFinish;
            var row = new TimingReportImportRow { Target = target,
                Channel = match.Target.Channel, Role = ReportImageRole, Stamp = stamp, Accept = stamp is not null && (current.Length == 0 || current == TimingReportEvidenceRow.Format(stamp)),
                Difference = match.DifferenceTicks is { } delta ? (delta / (decimal)TimeSpan.TicksPerSecond).ToString("+0.0000000;-0.0000000;0", CultureInfo.InvariantCulture) : "",
                State = stamp is not null && current.Length > 0 && current != TimingReportEvidenceRow.Format(stamp) ? "Replace current time - select to confirm" : match.State, SourceText = match.Evidence?.Text ?? "",
                ImageId = keyParts?.Length >= 2 && Guid.TryParse(keyParts[1], out var id) ? id : null };
            row.PropertyChanged += ReportReceiptSelectionChanged;
            ReportImportPreview.Add(row);
        }
        OnPropertyChanged(nameof(AllReportMatchesSelected));
        _importDraftSnapshot = JsonSerializer.Serialize(ReadReportDraft());
        _importTargetsSnapshot = ReadReportImportTargetsSnapshot();
        _importSeriesRevision = (await workspace.ReadAsync()).Revision;
        SelectedReportImportRow = ReportImportPreview.FirstOrDefault(x => x.ImageId == SelectedReportImage?.Id && x.CanAccept);
        return true;
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
            { throw new DomainValidationException("The report changed after this preview. Read the images again."); }
            // Receipt assignments use the A/B snapshot already displayed in the report.
            // Reading more device packets must not interrupt operator entry.
            var assignments = current.Associations.ToList(); var count = 0;
            foreach (var row in ReportImportPreview.Where(x => x.Accept && x.Stamp is not null))
            {
                var existing = assignments.FindIndex(x => x.Run == row.Run && x.Bib == row.Bib && x.Channel == row.Channel && x.Role == row.Role);
                if (existing >= 0 && assignments[existing].Stamp.Ticks == row.Stamp!.Ticks
                    && assignments[existing].Stamp.Precision == row.Stamp.Precision && assignments[existing].Stamp.Verified) { continue; }
                var replacement = new TimingReportAssociation(row.Run, row.Bib, row.Channel, row.Role, row.Stamp! with { Verified = true, SourceReference = "manual" });
                if (existing >= 0) { assignments[existing] = replacement; } else { assignments.Add(replacement); }
                count++;
            }
            if (count == 0)
            {
                if (!ReportImportPreview.Any(x => x.Accept && x.CanAccept)) { throw new DomainValidationException("Select at least one matched timestamp."); }
                ClearReportPreview(); ReportStatus = "Selected timestamps are already present."; SetStatus(ReportStatus); return;
            }
            TimingReportBib Sample(int run, TimingReportBib sample)
            {
                TimingReportStamp? Find(TimingReportImageRole role, int channel) => assignments.FirstOrDefault(x => x.Run == run && x.Bib == sample.Bib && x.Role == role && x.Channel == channel)?.Stamp;
                return sample with { BStart = Find(TimingReportImageRole.B, 0), BFinish = Find(TimingReportImageRole.B, 1), HandStart = Find(TimingReportImageRole.HandStart, 0), HandFinish = Find(TimingReportImageRole.HandFinish, 1) };
            }
            var accepted = current with { Associations = assignments.ToArray(), Reviewed = false, CertifyFis = false,
                Runs = current.Runs.Select(r => r with { First = Sample(r.Run, r.First), Last = Sample(r.Run, r.Last) }).ToArray() };
            await workspace.ApplyTimingReportImportAsync(accepted, _importSeriesRevision, TimingOperator,
                "Operator verified " + _reportImportProvenance + ": " + ReportChangeReason, DateTimeOffset.UtcNow);
            _reportDraft = accepted; _reportSaved = await workspace.ReadTimingReportAsync(current.CompetitionId);
            _current = await workspace.ReadAsync(); await PopulateReportAsync(accepted);
            HasTimingReportEdits = false; ClearReportPreview();
            ReportImportStatus = $"{count} verified timestamp(s) saved with audit history.";
            ReportStatus = "Image assignments saved. Complete the report review.";
            SetStatus(ReportStatus);
        });
        IsReportBusy = false;
    }

    public void ResetReportImport()
    {
        _reportImageCancellation?.Cancel(); ResetReportDeviceRead(); ClearReportPreview(); ReportImages.Clear(); SelectedReportImage = null; ReportImageOriginalSize = false;
        ReportInputMode = ReportImageInputMode;
    }
    private void DisposeReportUi() { DisposeReportAutosave(); _reportImageCancellation?.Cancel(); ResetReportDeviceRead(); _reportSubmissionCancellation?.Cancel(); ReportImagePreview?.Dispose(); }
}
