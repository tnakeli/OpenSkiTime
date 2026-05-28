using Avalonia;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSkiTime.Application.Abstractions;
using OpenSkiTime.Desktop.Services;
using OpenSkiTime.Fis;
using OpenSkiTime.Persistence;

namespace OpenSkiTime.Desktop;

internal static class Program
{
    /// <summary>
    /// Composition root. Resolves the SQLite database path under the user's
    /// LocalApplicationData directory and wires only the services this
    /// feature needs. NO <c>HttpClient</c>/<c>IHttpClientFactory</c> is
    /// registered — see Constitution Principle IV and SC-007.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        ServiceProvider = BuildServices();
        ApplyMigrations(ServiceProvider);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void ApplyMigrations(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OpenSkiTimeDbContext>();
        db.Database.Migrate();
    }

    /// <summary>
    /// Set by <see cref="Main"/> before Avalonia starts. <see cref="App"/>
    /// reads this to locate the main window. A static handoff is acceptable
    /// here because the Avalonia bootstrapper instantiates <see cref="App"/>
    /// reflectively; we cannot pass services via its constructor.
    /// </summary>
    public static IServiceProvider ServiceProvider { get; private set; } = null!;

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static ServiceProvider BuildServices()
    {
        var sqlitePath = ResolveSqlitePath();

        var services = new ServiceCollection();

        services.AddLogging(b => b.AddDebug().SetMinimumLevel(LogLevel.Information));

        services.AddSingleton<IClock, SystemClock>();

        services.AddOpenSkiTimePersistence(sqlitePath);

        // FIS placeholder: explicit single binding to the no-network impl.
        services.AddSingleton<IFisCompetitionUpdater, NotImplementedFisUpdater>();

        // Desktop services
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();

        return services.BuildServiceProvider();
    }

    private static string ResolveSqlitePath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenSkiTime");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "openskitime.db");
    }
}
