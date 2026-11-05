using System.IO;
using LifeOS.Api.Data;
using LifeOS.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class DesktopBackendHost : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly IHostedService[] hostedServices;
    public IDesktopBackend Backend { get; }
    public string? ApiBaseUrl { get; private set; }

    private DesktopBackendHost(ServiceProvider provider)
    {
        this.provider = provider;
        Backend = provider.GetRequiredService<IDesktopBackend>();
        hostedServices = provider.GetServices<IHostedService>().ToArray();
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
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI:Provider"] = "Ollama",
                ["Ollama:ChatUrl"] = "http://localhost:11434/api/chat",
                ["Ollama:Model"] = "qwen3:4b",
                ["OpenAI:Model"] = "gpt-6-astra"
            })
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", "LifeOS.Api-Local-Development", "secrets.json"), optional: true)
            .AddJsonFile(Path.Combine(dataRoot, "appsettings.json"), optional: true)
            .AddEnvironmentVariables()
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.AddConfiguration(configuration.GetSection("Logging"));
            builder.AddProvider(new DailyFileLoggerProvider(logDirectory));
        });
        services.AddHttpClient<OpenAiAssistantService>(client => client.Timeout = TimeSpan.FromSeconds(90));
        services.AddSingleton<PasswordResetEmailSender>();
        services.AddHostedService<TaskReminderService>();
        var databaseProvider = configuration["LifeOS:DatabaseProvider"]?.Trim();
        if (string.Equals(databaseProvider, "PostgreSQL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(databaseProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
        {
            var connectionString = configuration.GetConnectionString("LifeOS");
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("PostgreSQL is selected, but ConnectionStrings:LifeOS is missing from appsettings.json.");

            services.AddDbContextFactory<LifeOSDbContext>(options => options.UseNpgsql(connectionString,
                postgres => postgres.MigrationsAssembly("LifeOS.PostgresMigrations")));
        }
        else if (string.IsNullOrWhiteSpace(databaseProvider)
            || string.Equals(databaseProvider, "SQLite", StringComparison.OrdinalIgnoreCase))
        {
            services.AddDbContextFactory<LifeOSDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
        }
        else
        {
            throw new InvalidOperationException($"Unsupported LifeOS:DatabaseProvider '{databaseProvider}'. Use SQLite or PostgreSQL.");
        }
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
            foreach (var hostedService in provider.GetServices<IHostedService>())
                await hostedService.StartAsync(cancellationToken);
            logger.LogInformation("LifeOS desktop backend started");
        }
        catch (Exception exception)
        {
            logger.LogCritical(exception, "LifeOS desktop backend failed during startup");
            await provider.DisposeAsync();
            throw;
        }
        return new DesktopBackendHost(provider)
        {
            ApiBaseUrl = configuration["LifeOS:ApiBaseUrl"]?.Trim().TrimEnd('/')
        };
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

    public async ValueTask DisposeAsync()
    {
        foreach (var hostedService in hostedServices.Reverse())
            await hostedService.StopAsync(CancellationToken.None);
        await provider.DisposeAsync();
    }
}
