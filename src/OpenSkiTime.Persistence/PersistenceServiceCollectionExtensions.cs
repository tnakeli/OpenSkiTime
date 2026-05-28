using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Persistence.Repositories;

namespace OpenSkiTime.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>
    /// Register Open Ski Time persistence with a SQLite database backed by
    /// <paramref name="sqliteFilePath"/>. The composition root (Desktop) is
    /// the only place that knows the on-disk path; everything downstream
    /// only sees the abstractions.
    /// </summary>
    public static IServiceCollection AddOpenSkiTimePersistence(
        this IServiceCollection services,
        string sqliteFilePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(sqliteFilePath);

        services.AddDbContext<OpenSkiTimeDbContext>(opts =>
            opts.UseSqlite($"Data Source={sqliteFilePath}"));

        services.AddScoped<IUnitOfWork, EfCoreUnitOfWork>();
        services.AddScoped<IEventSeriesRepository, EventSeriesRepository>();

        return services;
    }
}
