using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Application.Competitors;
using OpenSkiTime.Application.Competitions;
using OpenSkiTime.Application.Participations;
using OpenSkiTime.Application.Series;
using OpenSkiTime.Desktop.Services;
using OpenSkiTime.Desktop.ViewModels;
using OpenSkiTime.Domain.Common;
using OpenSkiTime.Domain.Competitions;
using OpenSkiTime.Domain.Series;
using OpenSkiTime.Fis;
using OpenSkiTime.Import;
using OpenSkiTime.Persistence;

namespace OpenSkiTime.Desktop.Tests;

// Uses the production grid commands and persistence, with synthetic data only.
// This does not drive the Avalonia controls or the Windows clipboard.
internal sealed class LegacyGridFixture : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"openskitime-grid-m0-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;
    private readonly AsyncServiceScope _scope;

    public LegacyGridFixture()
    {
        _services = new ServiceCollection()
            .AddOpenSkiTimePersistence(_dbPath)
            .AddSingleton<IClock, SystemClock>()
            .AddSingleton<IFisCompetitionUpdater, NotImplementedFisUpdater>()
            .AddScoped<CreateEventSeriesUseCase>()
            .AddScoped<UpdateEventSeriesUseCase>()
            .AddScoped<DeleteEventSeriesUseCase>()
            .AddScoped<AddCompetitionUseCase>()
            .AddScoped<UpdateCompetitionUseCase>()
            .AddScoped<RemoveCompetitionUseCase>()
            .AddScoped<EventSeriesValidationSummaryService>()
            .AddScoped<AddCompetitorUseCase>()
            .AddScoped<EditCompetitorUseCase>()
            .AddScoped<RemoveCompetitorUseCase>()
            .AddScoped<AssignBibUseCase>()
            .AddScoped<SetParticipationUseCase>()
            .AddScoped<ImportPreviewService>()
            .AddScoped<ImportApplyService>()
            .AddSingleton<IClipboardService>(Clipboard)
            .AddSingleton<IDialogService>(Dialogs)
            .AddScoped<CompetitorGridViewModel>()
            .AddScoped<EventSeriesOverviewViewModel>()
            .AddTransient<CompetitionEditorViewModel>()
            .AddScoped<ShellViewModel>()
            .BuildServiceProvider();
        _scope = _services.CreateAsyncScope();
    }

    public Guid SeriesId { get; private set; }
    public Guid CompetitionId { get; private set; }
    public ClipboardStub Clipboard { get; } = new();
    public DialogStub Dialogs { get; } = new();
    public CompetitorGridViewModel Grid => _scope.ServiceProvider.GetRequiredService<CompetitorGridViewModel>();
    public IServiceProvider Services => _scope.ServiceProvider;

    public async Task InitializeAsync()
    {
        var db = Services.GetRequiredService<OpenSkiTimeDbContext>();
        await db.Database.MigrateAsync();
        var series = EventSeries.Create(Guid.NewGuid(), "Grid M0", "Levi", "Test Club",
            new DateOnly(2026, 1, 10), new DateOnly(2026, 1, 11), "FIN", "2025/26",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var competition = Competition.Create(Guid.NewGuid(), series.Id, "Slalom", "3.1 SL",
            new DateOnly(2026, 1, 10), Discipline.SL, RaceType.Club, 2, 0);
        series.AddCompetition(competition);
        db.EventSeries.Add(series);
        await db.SaveChangesAsync();
        SeriesId = series.Id;
        CompetitionId = competition.Id;
        await Grid.LoadAsync(SeriesId);
    }

    public async Task<CompetitorRowViewModel> AddRowAsync()
    {
        Grid.AddCompetitorCommand.Execute(null);
        var row = Grid.Competitors[0];
        row.LastName = "Müller";
        row.FirstName = "Hannes";
        row.YearOfBirth = 2007;
        row.NationCode = "FIN";
        row.ClubName = "Original Club";
        row.FisCode = "123456";
        await Grid.AutoSaveRowAsync(row);
        return Grid.Competitors.Single();
    }

    public Task<EventSeries> ReopenAsync() => ReopenSeriesAsync(SeriesId);

    public async Task<EventSeries> ReopenSeriesAsync(Guid seriesId)
    {
        await using var reopened = _services.CreateAsyncScope();
        return await reopened.ServiceProvider.GetRequiredService<IEventSeriesRepository>()
            .GetByIdAsync(seriesId) ?? throw new InvalidOperationException("Series was not persisted.");
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    internal sealed class ClipboardStub : IClipboardService
    {
        public string? Text { get; set; }
        public Task<string?> GetTextAsync() => Task.FromResult(Text);
        public Task SetTextAsync(string text) { Text = text; return Task.CompletedTask; }
    }

    internal sealed class DialogStub : IDialogService
    {
        public List<string> Errors { get; } = [];
        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;
        public Task ShowErrorAsync(string title, string message) { Errors.Add(message); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string message) => Task.FromResult(true);
    }
}
