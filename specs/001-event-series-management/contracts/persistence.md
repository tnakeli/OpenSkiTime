# Contract — Persistence

**Feature**: 001-event-series-management
**Module**: `OpenSkiTime.Application.Abstractions` (interfaces),
            `OpenSkiTime.Persistence` (EF Core implementation).

## Ports (defined in Application, depended on by everything else)

```csharp
namespace OpenSkiTime.Application.Abstractions;

public interface IClock
{
    DateOnly Today();      // Local calendar day
    DateTime UtcNow();     // For audit fields
}

public interface IUnitOfWork : IAsyncDisposable
{
    Task<IDisposable> BeginTransactionAsync(CancellationToken ct = default);
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}

public interface IEventSeriesRepository
{
    Task<EventSeries?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<EventSeriesSummary>> ListAsync(CancellationToken ct = default);

    Task AddAsync(EventSeries series, CancellationToken ct = default);
    void Update(EventSeries series);     // EF Core change-tracked
    void Remove(EventSeries series);

    Task<EventSeriesSnapshot> LoadSnapshotAsync(Guid id, CancellationToken ct = default);
}

public sealed record EventSeriesSummary(Guid Id, string Name, DateOnly StartDate, DateOnly EndDate);

public sealed record EventSeriesSnapshot(
    Guid Id,
    long Version,
    IReadOnlyList<Competition> Competitions,
    IReadOnlyList<Competitor> Competitors,
    IReadOnlyList<Participation> Participations
);
```

## Adapter (Persistence) implementation rules

- `OpenSkiTimeDbContext` extends `DbContext`. Constructor takes
  `DbContextOptions<OpenSkiTimeDbContext>` only. **It does not
  reference any UI or Avalonia type.**
- Configuration of every entity lives in `Configurations/<Entity>Configuration.cs`
  and is registered via `modelBuilder.ApplyConfigurationsFromAssembly`.
- Migrations live under `Migrations/`, generated with
  `dotnet ef migrations add <Name> --project src/OpenSkiTime.Persistence`.
- `EventSeries.RowVersion` is a `long` incremented in
  `SaveChangesAsync` whenever any entity within the series is added,
  updated, or removed in the current change set.

## DI registration

```csharp
public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSkiTimePersistence(
        this IServiceCollection services,
        string sqliteFilePath)
    {
        services.AddDbContext<OpenSkiTimeDbContext>(opts =>
            opts.UseSqlite($"Data Source={sqliteFilePath}"));
        services.AddScoped<IUnitOfWork, EfCoreUnitOfWork>();
        services.AddScoped<IEventSeriesRepository, EventSeriesRepository>();
        return services;
    }
}
```

The desktop composition root resolves the SQLite path from
`Environment.SpecialFolder.LocalApplicationData` and passes it in;
nothing else knows the path.

## Test contract

- `OpenSkiTime.Persistence.Tests` provides a `using var fixture = new
  TempSqliteFixture();` helper that:
  - creates a temp file path,
  - wires the DbContext,
  - applies migrations,
  - deletes the file on dispose.
- Tests do not share databases between cases.
