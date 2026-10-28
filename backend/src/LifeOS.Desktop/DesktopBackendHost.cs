using System.IO;
using LifeOS.Api.Data;
using LifeOS.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public sealed class DesktopBackendHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    public IDesktopBackend Backend { get; }

    private DesktopBackendHost(ServiceProvider provider)
    {
        this.provider = provider;
        Backend = provider.GetRequiredService<IDesktopBackend>();
    }

    public static async Task<DesktopBackendHost> StartAsync(CancellationToken cancellationToken = default)
    {
        var dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeOS");
        Directory.CreateDirectory(dataRoot);
        var logDirectory = GetLogDirectory(dataRoot);
        var databasePath = Path.Combine(dataRoot, "lifeos.db");
        var appDatabase = Path.Combine(AppContext.BaseDirectory, "lifeos.db");
        if (!File.Exists(databasePath) && File.Exists(appDatabase)) CopySqliteFiles(appDatabase, databasePath);
        if (!File.Exists(databasePath))
        {
            var frontend = FindFrontendRoot();
            var root = frontend is null ? null : Directory.GetParent(frontend)?.FullName;
            var oldDatabase = root is null ? null : Path.Combine(root, "backend", "src", "LifeOS.Api", "lifeos.db");
            if (oldDatabase is not null && File.Exists(oldDatabase)) CopySqliteFiles(oldDatabase, databasePath);
        }

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddProvider(new DailyFileLoggerProvider(logDirectory));
        });
        services.AddHttpClient<OpenAiAssistantService>();
        services.AddSingleton<PasswordResetEmailSender>();
        services.AddDbContextFactory<LifeOSDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        services.AddSingleton<DesktopBackend>();
        services.AddSingleton<IDesktopBackend>(sp => sp.GetRequiredService<DesktopBackend>());

        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILogger<DesktopBackendHost>>();
        try
        {
            logger.LogInformation("Starting LifeOS desktop backend. Log directory: {LogDirectory}", logDirectory);
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
            logger.LogInformation("Applying desktop database migrations");
            await db.Database.MigrateAsync(cancellationToken);
            logger.LogInformation("Desktop database migrations applied");
            logger.LogInformation("LifeOS desktop backend started");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "LifeOS desktop backend failed during startup");
            await provider.DisposeAsync();
            throw;
        }
        return new DesktopBackendHost(provider);
    }

    private static string? FindFrontendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "frontend");
                if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
            }
        return null;
    }

    private static string GetLogDirectory(string dataRoot)
    {
        var frontend = FindFrontendRoot();
        var repositoryRoot = frontend is null ? null : Directory.GetParent(frontend)?.FullName;
        var apiProject = repositoryRoot is null ? null : Path.Combine(repositoryRoot, "backend", "src", "LifeOS.Api");
        return apiProject is not null && File.Exists(Path.Combine(apiProject, "LifeOS.Api.csproj"))
            ? Path.Combine(apiProject, "logs")
            : Path.Combine(dataRoot, "logs");
    }

    private static void CopySqliteFiles(string source, string destination)
    {
        File.Copy(source, destination);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(source + suffix)) File.Copy(source + suffix, destination + suffix);
    }

    public ValueTask DisposeAsync() => provider.DisposeAsync();
}
