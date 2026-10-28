using LifeOS.Api.Data;
using LifeOS.Api.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Threading.RateLimiting;

var app = await LifeOsApplication.BuildAsync(args);
await app.RunAsync();

public static class LifeOsApplication
{
    public static async Task<WebApplication> BuildAsync(string[] args)
    {
        var frontendRoot = FindFrontendRoot();
        var webRoot = frontendRoot ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Directory.CreateDirectory(webRoot);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(LifeOsApplication).Assembly.GetName().Name,
            ContentRootPath = Directory.GetCurrentDirectory(),
            WebRootPath = webRoot
        });
        builder.Logging.AddProvider(new DailyFileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "logs")));
        builder.Services.AddDbContext<LifeOSDbContext>(options =>
            options.UseSqlite(builder.Configuration.GetConnectionString("LifeOS") ?? "Data Source=lifeos.db"));
        builder.Services.AddAuthentication("Bearer")
            .AddScheme<AuthenticationSchemeOptions, BearerTokenHandler>("Bearer", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddRateLimiter(options => options.AddPolicy("password-reset", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "desktop",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(10),
                    QueueLimit = 0,
                    AutoReplenishment = true
                })));
        builder.Services.AddHttpClient<OpenAiAssistantService>();
        builder.Services.AddSingleton<PasswordResetEmailSender>();
        builder.Services.AddHostedService<TaskReminderService>();
        builder.Services.AddControllers();

        var app = builder.Build();
        using (var scope = app.Services.CreateScope())
        {
            app.Logger.LogInformation("Applying database migrations");
            await scope.ServiceProvider.GetRequiredService<LifeOSDbContext>().Database.MigrateAsync();
            app.Logger.LogInformation("Database migrations applied");
        }

        app.Use(async (context, next) =>
        {
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("LifeOS.Http");
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                await next();
                logger.LogInformation("HTTP {Method} {Path} responded {StatusCode} in {ElapsedMilliseconds} ms",
                    context.Request.Method, context.Request.Path, context.Response.StatusCode,
                    System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "HTTP {Method} {Path} failed", context.Request.Method, context.Request.Path);
                throw;
            }
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapControllers();
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }));
        return app;
    }

    internal static string? FindFrontendRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, "frontend");
                if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
            }
        return null;
    }

    internal static void CopySqliteFiles(string source, string destination)
    {
        File.Copy(source, destination);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(source + suffix)) File.Copy(source + suffix, destination + suffix);
    }
}
