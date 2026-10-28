using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using LifeOS.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Authorize, Route("api/export")]
public class ExportController(LifeOSDbContext db, IWebHostEnvironment environment, IConfiguration configuration) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> Download(CancellationToken cancellationToken)
    {
        var userId = UserId;
        var user = await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => new { x.Name, x.Email, x.CreatedAt }).SingleOrDefaultAsync(cancellationToken);
        if (user is null) return NotFound();

        var tasks = await db.Tasks.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var finance = await db.FinanceTransactions.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var budgets = await db.FinanceBudgets.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var workouts = await db.Workouts.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var habits = await db.FitnessHabits.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var checkIns = await db.HabitCheckIns.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var documents = await db.Documents.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var applications = await db.CareerApplications.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var skills = await db.CareerSkills.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(cancellationToken);
        var storageSetting = configuration["Documents:StoragePath"] ?? "App_Data/documents";
        var storageRoot = Path.GetFullPath(Path.IsPathRooted(storageSetting) ? storageSetting : Path.Combine(environment.ContentRootPath, storageSetting));
        var userDirectory = Path.Combine(storageRoot, userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var missingFile = documents.FirstOrDefault(document => !System.IO.File.Exists(Path.Combine(userDirectory, document.StoredFileName)));
        if (missingFile is not null)
            return Conflict(new { message = $"The stored file for '{missingFile.OriginalFileName}' is missing, so a complete backup cannot be created." });
        var documentFiles = documents.Select(document => new
        {
            document.Id, document.Title, document.Category, document.OriginalFileName, document.MediaType,
            document.SizeBytes, document.UploadedAt, document.ExpiresAt,
            ArchivePath = $"documents/{document.Id}-{SafeFileName(document.OriginalFileName)}"
        }).ToList();
        var backup = new
        {
            formatVersion = 1, exportedAtUtc = DateTime.UtcNow, profile = user, tasks, financeTransactions = finance, financeBudgets = budgets,
            workouts, habits, habitCheckIns = checkIns, documents = documentFiles,
            careerApplications = applications, careerSkills = skills
        };

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=lifeos-backup-{DateTime.UtcNow:yyyy-MM-dd}.zip";
        using (var archive = new ZipArchive(Response.Body, ZipArchiveMode.Create, leaveOpen: true))
        {
            var dataEntry = archive.CreateEntry("workspace.json", CompressionLevel.Fastest);
            await using (var entry = dataEntry.Open())
                await JsonSerializer.SerializeAsync(entry, backup, cancellationToken: cancellationToken);

            foreach (var document in documents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(userDirectory, document.StoredFileName);
                if (!System.IO.File.Exists(path)) continue;
                var fileEntry = archive.CreateEntry($"documents/{document.Id}-{SafeFileName(document.OriginalFileName)}", CompressionLevel.Fastest);
                await using var output = fileEntry.Open();
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken);
            }
        }

        return new EmptyResult();
    }

    private static string SafeFileName(string name)
    {
        var baseName = Path.GetFileName(name.Replace('\\', '/'));
        var safe = new string(baseName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "document" : safe;
    }
}
