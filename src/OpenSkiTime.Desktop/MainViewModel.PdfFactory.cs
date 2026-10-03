using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenSkiTime.Application;
using OpenSkiTime.Domain;
using OpenSkiTime.Reporting;

namespace OpenSkiTime.Desktop;

public sealed class PdfFactoryRow
{
    public PdfReportDescriptor Descriptor { get; }
    public string Name => Descriptor.DisplayName;
    public string FileName => Descriptor.FileName;
    public string OutputPath { get; }
    public string Status { get; }
    public string? Reason => Descriptor.UnavailableReason;
    public bool CanGenerate => Descriptor.CanGenerate;
    public bool CanOpen { get; }
    public string GenerateLabel => CanOpen ? "Regenerate" : "Generate";
    public string OpenLabel => Status == "Outdated" ? "Open old" : "Open";
    public bool IsOutdated => Status == "Outdated";
    public IAsyncRelayCommand GenerateCommand { get; }
    public IRelayCommand OpenCommand { get; }
    public PdfFactoryRow(PdfReportDescriptor descriptor, string outputPath, PdfReportStatus status, Func<Task> generate, Action open)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        OutputPath = outputPath;
        Status = status switch { PdfReportStatus.Generated => "Generated", PdfReportStatus.Outdated => "Outdated", _ => "Not generated" };
        CanOpen = status != PdfReportStatus.NotGenerated;
        GenerateCommand = new AsyncRelayCommand(generate);
        OpenCommand = new RelayCommand(open);
    }
}

public sealed partial class MainViewModel
{
    private readonly ReportGenerationService _pdfGenerator = pdfGenerator ?? new(new QuestPdfRenderer(), new PdfTraceLogger());
    private readonly IPdfOpener _pdfOpener = pdfOpener ?? new SystemPdfOpener();
    private EventPrintProfile _pdfProfile = new();
    private int _pdfLoad;
    private readonly List<string> _pdfProfilePreviews = [];
    public ObservableCollection<PdfFactoryRow> PdfReports { get; } = [];
    [ObservableProperty] private CompetitionDetails? _pdfCompetition;
    [ObservableProperty] private bool _isPdfBusy;
    [ObservableProperty] private string _pdfSummary = "Save an event series as an .ost file first.";
    [ObservableProperty] private string _pdfTemplateLabel = "Default OpenSkiTime styling";
    [ObservableProperty] private decimal _pdfTopMargin = 15;
    [ObservableProperty] private decimal _pdfBottomMargin = 15;
    [ObservableProperty] private decimal _pdfLeftMargin = 12;
    [ObservableProperty] private decimal _pdfRightMargin = 12;
    public bool IsPdfFactorySection => ActiveSection == WorkspaceSection.PdfFactory;
    [RelayCommand]
    private async Task ShowPdfFactoryAsync()
    {
        if (IsPdfBusy) { return; }
        if (!await FlushRaceInformationAsync() || !await FlushTimingReportAsync()) { return; }
        SwitchSection(WorkspaceSection.PdfFactory);
        if (!IsPdfFactorySection) { return; }
        await SelectPdfCompetitionAsync(Competitions.FirstOrDefault(x => x.Id == _activeRaceId) ?? Competitions.FirstOrDefault());
    }
    private async Task SelectPdfCompetitionAsync(CompetitionDetails? competition)
    {
        PdfCompetition = competition;
        if (competition is not null)
        { SetActiveRace(competition, _activeRaceId == competition.Id ? _activeRaceRun : 1, _activeRaceSection); }
        await RefreshPdfFactoryAsync();
    }
    private async Task RefreshPdfFactoryAsync()
    {
        var load = ++_pdfLoad;
        if (!workspace.IsOpen)
        {
            PdfReports.Clear();
            PdfSummary = "Save an event series as an .ost file first.";
            return;
        }
        var competitionId = PdfCompetition?.Id;
        try
        {
            var source = await Task.Run(() => new PdfReportSourceBuilder(new Devices.AlgeDecoderFactory()).BuildAsync(workspace, competitionId));
            var rows = await Task.Run(() => ReportCatalog.GetAvailableReports(source).Select(d => (Descriptor: d, Status: ReportStatusService.GetStatus(source, d))).ToArray());
            if (load != _pdfLoad || workspace.FilePath != source.FilePath) { return; }
            _pdfProfile = source.Settings.Profile;
            PdfTopMargin = _pdfProfile.TopMm; PdfBottomMargin = _pdfProfile.BottomMm;
            PdfLeftMargin = _pdfProfile.LeftMm; PdfRightMargin = _pdfProfile.RightMm;
            PdfTemplateLabel = _pdfProfile.TemplatePdf is { Length: > 0 } ? "Embedded background: " + _pdfProfile.TemplateName
                : _pdfProfile.TemplateName is null ? "Default OpenSkiTime styling" : "Background unavailable; using default styling";
            PdfReports.Clear();
            foreach (var row in rows)
            {
                var descriptor = row.Descriptor;
                var outputPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source.FilePath)!, descriptor.FileName));
                PdfReports.Add(new(descriptor, outputPath, row.Status, () => GeneratePdfAsync(descriptor.Id), () => OpenPdf(outputPath)));
            }
            PdfSummary = $"{rows.Length} reports | PDFs are stored beside {Path.GetFileName(source.FilePath)}.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("PDF Factory refresh failed: {0}", ex.GetType().Name);
            PdfSummary = "PDF Factory could not read this series. Check the file and reopen this view.";
            SetStatus(PdfSummary, true);
        }
    }
    private void OpenPdf(string outputPath)
    {
        try { _pdfOpener.Open(outputPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceError("Opening PDF failed: {0}", ex.GetType().Name);
            SetStatus("Could not open the PDF. Check that it exists and a default PDF reader is installed.", true);
        }
    }
    private async Task GeneratePdfAsync(string? id)
    {
        if (IsPdfBusy || !workspace.IsOpen) { return; }
        IsPdfBusy = true;
        try
        {
            // Capture fresh immutable data before rendering. Rendering never holds the workspace gate.
            var competitionId = PdfCompetition?.Id;
            var source = await Task.Run(() => new PdfReportSourceBuilder(new Devices.AlgeDecoderFactory()).BuildAsync(workspace, competitionId));
            async Task SaveMetadata(GeneratedPdf metadata)
            {
                await Task.Run(() => workspace.SaveGeneratedPdfForFileAsync(source.FilePath, metadata));
            }
            IReadOnlyList<PdfGenerationOutcome> outcomes;
            if (id is null) { outcomes = await _pdfGenerator.GenerateAllAsync(source, SaveMetadata, DateTimeOffset.Now); }
            else
            {
                var descriptor = ReportCatalog.GetAvailableReports(source).FirstOrDefault(x => x.Id == id);
                if (descriptor is null) { return; }
                outcomes = [await _pdfGenerator.GenerateAsync(source, descriptor, SaveMetadata, DateTimeOffset.Now)];
            }
            var failures = outcomes.Where(x => !x.Success).ToArray();
            var skipped = id is null ? ReportCatalog.GetAvailableReports(source).Count(x => !x.CanGenerate) : 0;
            await RefreshPdfFactoryAsync();
            PdfSummary = $"{outcomes.Count(x => x.Success)} report(s) generated; {skipped} unavailable skipped; {failures.Length} failed."
                + string.Join("", failures.Select(x => " " + ReportCatalog.GetAvailableReports(source).First(d => d.Id == x.ReportId).DisplayName + ": " + x.Error));
            SetStatus(PdfSummary, failures.Length > 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("PDF Factory generation failed: {0}", ex.GetType().Name);
            SetStatus("Could not prepare PDF data. Check the series file and retry.", true);
        }
        finally { IsPdfBusy = false; }
    }
    [RelayCommand] private Task GenerateAllPdfsAsync() => GeneratePdfAsync(null);
    private EventPrintProfile CurrentPdfProfile() => _pdfProfile with
    { TopMm = PdfTopMargin, BottomMm = PdfBottomMargin, LeftMm = PdfLeftMargin, RightMm = PdfRightMargin };
    [RelayCommand]
    private async Task PreviewPdfProfileAsync()
    {
        if (IsPdfBusy) { return; }
        IsPdfBusy = true;
        try
        {
            var profile = CurrentPdfProfile();
            profile.Validate();
            var path = Path.Combine(Path.GetTempPath(), "OpenSkiTime-PrintProfile-" + Guid.NewGuid().ToString("N") + ".pdf");
            _pdfProfilePreviews.Add(path);
            await Task.Run(() => QuestPdfRenderer.RenderPrintProfilePreview(profile, path));
            _pdfOpener.Open(path);
            SetStatus("Print profile preview opened. The red box shows the current margins; save the profile to apply them.");
        }
        catch (DomainValidationException ex) { SetStatus(ex.Message, true); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("Print profile preview failed: {0}", ex.GetType().Name);
            SetStatus("Could not open the print profile preview. Check the background, temporary folder permissions and default PDF reader, then retry.", true);
        }
        finally { IsPdfBusy = false; }
    }
    private void DisposePdfPreviews()
    {
        foreach (var path in _pdfProfilePreviews)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Trace.TraceWarning("Print profile preview cleanup failed: {0}", ex.GetType().Name); }
        }
    }
    [RelayCommand]
    private async Task SavePdfProfileAsync()
    {
        if (!workspace.IsOpen || IsPdfBusy) { return; }
        await GuardAsync(async () =>
        {
            var profile = CurrentPdfProfile();
            profile.Validate();
            await Task.Run(() => workspace.SavePrintProfileAsync(profile));
            await RefreshPdfFactoryAsync();
            SetStatus("Print profile saved. Existing PDFs can be regenerated with this profile.");
        });
    }
    public async Task SetPdfTemplateAsync(string path)
    {
        if (!workspace.IsOpen || IsPdfBusy) { return; }
        try
        {
            if (new FileInfo(path).Length > 20_000_000) { throw new DomainValidationException("The PDF background must be at most 20 MB."); }
            var bytes = await File.ReadAllBytesAsync(path);
            await Task.Run(() => QuestPdfRenderer.ValidateTemplate(bytes));
            _pdfProfile = _pdfProfile with { TemplateName = Path.GetFileName(path), TemplatePdf = bytes };
            await SavePdfProfileAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError("PDF background import failed: {0}", ex.GetType().Name);
            SetStatus("The background could not be loaded. Use an unencrypted A4 PDF of at most 20 MB.", true);
        }
    }
    [RelayCommand]
    private async Task RemovePdfTemplateAsync()
    {
        _pdfProfile = _pdfProfile with { TemplateName = null, TemplatePdf = null };
        await SavePdfProfileAsync();
    }
}
