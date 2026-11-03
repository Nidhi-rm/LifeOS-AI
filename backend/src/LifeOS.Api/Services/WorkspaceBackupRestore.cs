using System.IO.Compression;
using System.Text.Json;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Services;

public static class WorkspaceBackupRestore
{
    private const long MaximumArchiveBytes = 100 * 1024 * 1024;
    private const long MaximumWorkspaceJsonBytes = 5 * 1024 * 1024;
    private const long MaximumDocumentBytes = 15 * 1024 * 1024;
    private const long MaximumDocumentsBytes = 90 * 1024 * 1024;
    private static readonly HashSet<string> DocumentExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".docx", ".xlsx", ".txt", ".png", ".jpg", ".jpeg" };

    public static async Task<WorkspaceRestoreSummary> RestoreAsync(
        LifeOSDbContext db, int userId, Stream source, string userDocumentDirectory, CancellationToken cancellationToken)
    {
        using var archiveBytes = new MemoryStream();
        var buffer = new byte[81920];
        long archiveLength = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            archiveLength += read;
            if (archiveLength > MaximumArchiveBytes)
                throw new WorkspaceBackupException("The backup must be 100 MB or smaller.");
            await archiveBytes.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (archiveLength == 0) throw new WorkspaceBackupException("Choose a LifeOS backup ZIP file.");
        archiveBytes.Position = 0;

        WorkspaceBackup backup;
        Dictionary<string, ZipArchiveEntry> entries;
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true))
        {
            entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                if (!entries.TryAdd(entry.FullName, entry))
                    throw new WorkspaceBackupException("The backup contains duplicate file entries.");
            }
            if (!entries.TryGetValue("workspace.json", out var workspaceEntry))
                throw new WorkspaceBackupException("This ZIP does not contain a LifeOS workspace backup.");
            if (workspaceEntry.Length is <= 0 or > MaximumWorkspaceJsonBytes)
                throw new WorkspaceBackupException("The workspace data in this backup is invalid or too large.");

            await using var jsonStream = workspaceEntry.Open();
            backup = await JsonSerializer.DeserializeAsync<WorkspaceBackup>(jsonStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new WorkspaceBackupException("The workspace data in this backup is invalid.");
        }

        ValidateBackup(backup, entries);
        Directory.CreateDirectory(userDocumentDirectory);
        var stagedFiles = new List<(string TemporaryPath, string FinalPath, string StoredName)>();
        var committed = false;
        try
        {
            using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Read, leaveOpen: true))
            {
                long totalDocumentBytes = 0;
                foreach (var document in backup.Documents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.GetEntry(document.ArchivePath)
                        ?? throw new WorkspaceBackupException($"The backup file for '{document.OriginalFileName}' is missing.");
                    totalDocumentBytes += entry.Length;
                    if (entry.Length > MaximumDocumentBytes || totalDocumentBytes > MaximumDocumentsBytes)
                        throw new WorkspaceBackupException("Documents in the backup exceed the supported size limit.");
                    if (entry.Length != document.SizeBytes)
                        throw new WorkspaceBackupException($"The backup file for '{document.OriginalFileName}' has an unexpected size.");

                    var extension = Path.GetExtension(document.OriginalFileName).ToLowerInvariant();
                    var storedName = $"{Guid.NewGuid():N}{extension}";
                    var temporaryPath = Path.Combine(userDocumentDirectory, $".{storedName}.restore-tmp");
                    var finalPath = Path.Combine(userDocumentDirectory, storedName);
                    stagedFiles.Add((temporaryPath, finalPath, storedName));
                    await using (var input = entry.Open())
                    await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                        await input.CopyToAsync(output, cancellationToken);
                }
            }

            var oldStoredNames = await db.Documents.AsNoTracking().Where(x => x.UserId == userId)
                .Select(x => x.StoredFileName).ToListAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            await db.HabitCheckIns.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.Tasks.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.FinanceTransactions.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.FinanceBudgets.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.Workouts.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.FitnessHabits.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.Documents.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.CareerApplications.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
            await db.CareerSkills.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);

            var tasks = backup.Tasks.Select(x => new TaskItem
            {
                UserId = userId, Title = x.Title, Description = x.Description, Status = x.Status,
                Priority = x.Priority, DueAt = x.DueAt, Recurrence = x.Recurrence,
                ReminderAt = x.ReminderAt, ReminderSentAt = x.ReminderSentAt, CreatedAt = x.CreatedAt
            }).ToList();
            db.Tasks.AddRange(tasks);
            var transactions = backup.FinanceTransactions.Select(x => new FinanceTransaction
            {
                UserId = userId, Type = x.Type, AmountMinor = x.AmountMinor, Category = x.Category,
                Note = x.Note, OccurredAt = x.OccurredAt, CreatedAt = x.CreatedAt
            }).ToList();
            db.FinanceTransactions.AddRange(transactions);
            var budgets = backup.FinanceBudgets.Select(x => new FinanceBudget
            {
                UserId = userId, Month = x.Month, Category = x.Category,
                AmountMinor = x.AmountMinor, CreatedAt = x.CreatedAt
            }).ToList();
            db.FinanceBudgets.AddRange(budgets);
            var workouts = backup.Workouts.Select(x => new WorkoutEntry
            {
                UserId = userId, Name = x.Name, Type = x.Type, DurationMinutes = x.DurationMinutes,
                Notes = x.Notes, PerformedAt = x.PerformedAt, CreatedAt = x.CreatedAt
            }).ToList();
            db.Workouts.AddRange(workouts);
            var habits = backup.Habits.Select(x => new FitnessHabit
            {
                UserId = userId, Name = x.Name, Category = x.Category, IsActive = x.IsActive, CreatedAt = x.CreatedAt
            }).ToList();
            db.FitnessHabits.AddRange(habits);
            var applications = backup.CareerApplications.Select(x => new CareerApplication
            {
                UserId = userId, Company = x.Company, Role = x.Role, Status = x.Status, AppliedAt = x.AppliedAt,
                NextStepAt = x.NextStepAt, JobUrl = x.JobUrl, Notes = x.Notes, CreatedAt = x.CreatedAt
            }).ToList();
            db.CareerApplications.AddRange(applications);
            var skills = backup.CareerSkills.Select(x => new CareerSkill
            {
                UserId = userId, Name = x.Name, Category = x.Category, CurrentLevel = x.CurrentLevel,
                TargetLevel = x.TargetLevel, GoalDate = x.GoalDate, Notes = x.Notes, CreatedAt = x.CreatedAt
            }).ToList();
            db.CareerSkills.AddRange(skills);
            await db.SaveChangesAsync(cancellationToken);

            var habitIdMap = backup.Habits.Zip(habits).ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
            var checkIns = backup.HabitCheckIns.Select(x => new HabitCheckIn
            {
                UserId = userId, HabitId = habitIdMap[x.HabitId], Date = x.Date, CompletedAt = x.CompletedAt
            }).ToList();
            db.HabitCheckIns.AddRange(checkIns);
            var documents = backup.Documents.Zip(stagedFiles).Select(pair => new LifeDocument
            {
                UserId = userId, Title = pair.First.Title, Category = pair.First.Category,
                OriginalFileName = pair.First.OriginalFileName, StoredFileName = pair.Second.StoredName,
                MediaType = pair.First.MediaType, SizeBytes = pair.First.SizeBytes,
                UploadedAt = pair.First.UploadedAt, ExpiresAt = pair.First.ExpiresAt
            }).ToList();
            db.Documents.AddRange(documents);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var staged in stagedFiles) File.Move(staged.TemporaryPath, staged.FinalPath);
            await transaction.CommitAsync(cancellationToken);
            committed = true;

            foreach (var oldName in oldStoredNames)
            {
                if (Path.GetFileName(oldName) != oldName) continue;
                var oldPath = Path.Combine(userDocumentDirectory, oldName);
                if (File.Exists(oldPath)) File.Delete(oldPath);
            }

            return new WorkspaceRestoreSummary(tasks.Count, transactions.Count, budgets.Count, workouts.Count,
                habits.Count, checkIns.Count, documents.Count, applications.Count, skills.Count);
        }
        catch
        {
            foreach (var staged in stagedFiles)
            {
                if (File.Exists(staged.TemporaryPath)) File.Delete(staged.TemporaryPath);
                if (!committed && File.Exists(staged.FinalPath)) File.Delete(staged.FinalPath);
            }
            throw;
        }
    }

    private static void ValidateBackup(WorkspaceBackup backup, IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        if (backup.FormatVersion != 1) throw new WorkspaceBackupException("This LifeOS backup format version is not supported.");
        if (backup.Tasks is null || backup.FinanceTransactions is null || backup.FinanceBudgets is null
            || backup.Workouts is null || backup.Habits is null || backup.HabitCheckIns is null
            || backup.Documents is null || backup.CareerApplications is null || backup.CareerSkills is null)
            throw new WorkspaceBackupException("The backup is missing one or more workspace sections.");
        ValidateUniqueIds(backup.Tasks.Select(x => x.Id), "task");
        ValidateUniqueIds(backup.FinanceTransactions.Select(x => x.Id), "transaction");
        ValidateUniqueIds(backup.FinanceBudgets.Select(x => x.Id), "budget");
        ValidateUniqueIds(backup.Workouts.Select(x => x.Id), "workout");
        ValidateUniqueIds(backup.Habits.Select(x => x.Id), "habit");
        ValidateUniqueIds(backup.Documents.Select(x => x.Id), "document");
        ValidateUniqueIds(backup.CareerApplications.Select(x => x.Id), "career application");
        ValidateUniqueIds(backup.CareerSkills.Select(x => x.Id), "career skill");

        foreach (var task in backup.Tasks)
            if (task.Id <= 0 || string.IsNullOrWhiteSpace(task.Title) || task.Title.Length > 160 || task.Description?.Length > 2000
                || task.Status is not ("open" or "in-progress" or "done") || task.Priority is not ("low" or "normal" or "high")
                || task.Recurrence is not ("none" or "daily" or "weekly" or "monthly"))
                throw new WorkspaceBackupException("The backup contains an invalid task.");
        foreach (var item in backup.FinanceTransactions)
            if (item.Id <= 0 || item.Type is not ("income" or "expense") || item.AmountMinor <= 0
                || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 60 || item.Note?.Length > 500)
                throw new WorkspaceBackupException("The backup contains an invalid finance transaction.");
        foreach (var item in backup.FinanceBudgets)
            if (item.Id <= 0 || !DateOnly.TryParseExact(item.Month + "-01", "yyyy-MM-dd", out _)
                || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 60 || item.AmountMinor <= 0)
                throw new WorkspaceBackupException("The backup contains an invalid finance budget.");
        foreach (var item in backup.Workouts)
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 120
                || item.Type is not ("strength" or "cardio" or "mobility" or "sport" or "other")
                || item.DurationMinutes is < 1 or > 1440 || item.Notes?.Length > 1000)
                throw new WorkspaceBackupException("The backup contains an invalid workout.");
        foreach (var item in backup.Habits)
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100
                || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 30)
                throw new WorkspaceBackupException("The backup contains an invalid fitness habit.");
        var habitIds = backup.Habits.Select(x => x.Id).ToHashSet();
        var checkInKeys = new HashSet<(int HabitId, DateTime Date)>();
        foreach (var item in backup.HabitCheckIns)
            if (item.Id <= 0 || !habitIds.Contains(item.HabitId) || !checkInKeys.Add((item.HabitId, item.Date.Date)))
                throw new WorkspaceBackupException("The backup contains an invalid habit check-in.");
        foreach (var item in backup.Documents)
        {
            var safeName = SafeFileName(item.OriginalFileName);
            var expectedPath = $"documents/{item.Id}-{safeName}";
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 160
                || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 40
                || string.IsNullOrWhiteSpace(item.MediaType) || item.MediaType.Length > 120
                || safeName != item.OriginalFileName || !DocumentExtensions.Contains(Path.GetExtension(safeName))
                || item.SizeBytes is <= 0 or > MaximumDocumentBytes || item.ArchivePath != expectedPath
                || item.MediaType != MediaTypeFor(Path.GetExtension(safeName)) || !entries.ContainsKey(expectedPath))
                throw new WorkspaceBackupException("The backup contains an invalid document entry.");
        }
        foreach (var item in backup.CareerApplications)
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Company) || item.Company.Length > 120
                || string.IsNullOrWhiteSpace(item.Role) || item.Role.Length > 120
                || item.Status is not ("saved" or "applied" or "interview" or "offer" or "rejected")
                || item.JobUrl?.Length > 500 || item.Notes?.Length > 2000)
                throw new WorkspaceBackupException("The backup contains an invalid career application.");
        foreach (var item in backup.CareerSkills)
            if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100
                || string.IsNullOrWhiteSpace(item.Category) || item.Category.Length > 30 || item.CurrentLevel is < 1 or > 5
                || item.TargetLevel is < 1 or > 5 || item.Notes?.Length > 500)
                throw new WorkspaceBackupException("The backup contains an invalid career skill.");

        var expectedEntries = backup.Documents.Select(x => x.ArchivePath).Append("workspace.json").ToHashSet(StringComparer.Ordinal);
        if (entries.Keys.Any(name => !expectedEntries.Contains(name)))
            throw new WorkspaceBackupException("The backup contains unexpected files.");
    }

    private static void ValidateUniqueIds(IEnumerable<int> ids, string label)
    {
        var values = ids.ToList();
        if (values.Count > 100_000 || values.Any(id => id <= 0) || values.Distinct().Count() != values.Count)
            throw new WorkspaceBackupException($"The backup contains invalid {label} identifiers.");
    }

    private static string SafeFileName(string name)
    {
        var baseName = Path.GetFileName((name ?? string.Empty).Replace('\\', '/'));
        return new string(baseName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
    }

    private static string MediaTypeFor(string extension) => extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".txt" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        _ => string.Empty
    };
}

public sealed class WorkspaceBackupException(string message) : Exception(message);
public sealed record WorkspaceRestoreSummary(int Tasks, int Transactions, int Budgets, int Workouts, int Habits,
    int HabitCheckIns, int Documents, int CareerApplications, int CareerSkills);

internal sealed class WorkspaceBackup
{
    public int FormatVersion { get; set; }
    public required List<TaskItem> Tasks { get; set; }
    public required List<FinanceTransaction> FinanceTransactions { get; set; }
    public required List<FinanceBudget> FinanceBudgets { get; set; }
    public required List<WorkoutEntry> Workouts { get; set; }
    public required List<FitnessHabit> Habits { get; set; }
    public required List<HabitCheckIn> HabitCheckIns { get; set; }
    public required List<BackupDocument> Documents { get; set; }
    public required List<CareerApplication> CareerApplications { get; set; }
    public required List<CareerSkill> CareerSkills { get; set; }
}

internal sealed class BackupDocument
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string MediaType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
}
