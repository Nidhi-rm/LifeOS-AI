using System.IO.Compression;
using System.Security.Claims;
using System.Text.Json;
using LifeOS.Api.Controllers;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using LifeOS.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace LifeOS.Api.Tests;

public sealed class WorkspaceAndControllerTests
{
    [Fact]
    public async Task RestoreAsync_ReplacesOnlyCurrentUsersDataAndRestoresDocumentFiles()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        db.Tasks.AddRange(
            new TaskItem { UserId = 1, Title = "old current task" },
            new TaskItem { UserId = 2, Title = "other account task" });
        await db.SaveChangesAsync();

        var directory = Path.Combine(Path.GetTempPath(), $"lifeos-restore-test-{Guid.NewGuid():N}");
        try
        {
            await using var backup = CreateBackupWithDocument();
            var summary = await WorkspaceBackupRestore.RestoreAsync(db, 1, backup, directory, CancellationToken.None);

            Assert.Equal(1, summary.Tasks);
            Assert.Equal(1, summary.Habits);
            Assert.Equal(1, summary.HabitCheckIns);
            Assert.Equal(1, summary.Documents);
            var tasks = await db.Tasks.OrderBy(x => x.UserId).ToListAsync();
            Assert.Collection(tasks,
                task => { Assert.Equal(1, task.UserId); Assert.Equal("restored task", task.Title); },
                task => { Assert.Equal(2, task.UserId); Assert.Equal("other account task", task.Title); });
            var habit = await db.FitnessHabits.SingleAsync(x => x.UserId == 1);
            var checkIn = await db.HabitCheckIns.SingleAsync(x => x.UserId == 1);
            Assert.Equal(habit.Id, checkIn.HabitId);
            var document = await db.Documents.SingleAsync(x => x.UserId == 1);
            Assert.Equal("notes.txt", document.OriginalFileName);
            Assert.Equal("restored document", await File.ReadAllTextAsync(Path.Combine(directory, document.StoredFileName)));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreAsync_RejectsArchiveWithoutWorkspaceAndKeepsCurrentData()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        db.Tasks.Add(new TaskItem { UserId = 1, Title = "keep me" });
        await db.SaveChangesAsync();
        await using var invalid = CreateZip(("readme.txt", "not a workspace backup"));

        var exception = await Assert.ThrowsAsync<WorkspaceBackupException>(() =>
            WorkspaceBackupRestore.RestoreAsync(db, 1, invalid, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None));

        Assert.Contains("does not contain", exception.Message);
        Assert.Equal("keep me", (await db.Tasks.SingleAsync(x => x.UserId == 1)).Title);
    }

    [Fact]
    public async Task RestoreAsync_RejectsUnsupportedVersionBeforeReplacingWorkspace()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        db.Tasks.Add(new TaskItem { UserId = 1, Title = "keep me" });
        await db.SaveChangesAsync();
        await using var backup = CreateZip(("workspace.json", "{\"formatVersion\":99,\"tasks\":[],\"financeTransactions\":[],\"financeBudgets\":[],\"workouts\":[],\"habits\":[],\"habitCheckIns\":[],\"documents\":[],\"careerApplications\":[],\"careerSkills\":[]}"));

        var exception = await Assert.ThrowsAsync<WorkspaceBackupException>(() =>
            WorkspaceBackupRestore.RestoreAsync(db, 1, backup, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), CancellationToken.None));

        Assert.Contains("version is not supported", exception.Message);
        Assert.Equal("keep me", (await db.Tasks.SingleAsync(x => x.UserId == 1)).Title);
    }

    [Fact]
    public async Task TasksController_ListsOnlyCurrentUsersTasks()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        db.Tasks.AddRange(new TaskItem { UserId = 1, Title = "mine" }, new TaskItem { UserId = 2, Title = "theirs" });
        await db.SaveChangesAsync();
        var controller = AsUser(new TasksController(db), 1);

        var result = await controller.GetTasks();

        Assert.Equal("mine", Assert.Single(Assert.IsAssignableFrom<IEnumerable<TaskItem>>(result.Value)).Title);
    }

    [Fact]
    public async Task TasksController_CompletingRecurringTaskCreatesNextOccurrence()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        var due = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var task = new TaskItem { UserId = 1, Title = "weekly task", DueAt = due, Recurrence = "weekly" };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        var controller = AsUser(new TasksController(db), 1);

        var result = await controller.UpdateTask(task.Id, new TaskRequest("weekly task", null, "done", "normal", due, "weekly"));

        Assert.IsType<NoContentResult>(result);
        var next = await db.Tasks.SingleAsync(x => x.UserId == 1 && x.Id != task.Id);
        Assert.Equal("open", next.Status);
        Assert.Equal(due.AddDays(7), next.DueAt);
    }

    [Fact]
    public async Task FinanceController_StoresDecimalAmountsInMinorUnitsAndScopesToUser()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        var controller = AsUser(new FinanceController(db), 1);

        var result = await controller.CreateTransaction(new TransactionRequest("expense", 123.45m, "Food", null,
            new DateTime(2026, 11, 2)));

        Assert.IsType<CreatedAtActionResult>(result);
        var saved = await db.FinanceTransactions.SingleAsync();
        Assert.Equal(12345, saved.AmountMinor);
        Assert.Equal(1, saved.UserId);
    }

    [Fact]
    public async Task FitnessController_HabitCheckInIsIdempotentAndUserScoped()
    {
        await using var connection = await OpenDatabase();
        await using var db = CreateContext(connection);
        await SeedUsers(db);
        var mine = new FitnessHabit { UserId = 1, Name = "Walk" };
        var theirs = new FitnessHabit { UserId = 2, Name = "Read" };
        db.FitnessHabits.AddRange(mine, theirs);
        await db.SaveChangesAsync();
        var controller = AsUser(new FitnessController(db), 1);

        Assert.IsType<NoContentResult>(await controller.CompleteHabit(mine.Id, "2026-11-02"));
        Assert.IsType<NoContentResult>(await controller.CompleteHabit(mine.Id, "2026-11-02"));
        Assert.IsType<NotFoundResult>(await controller.CompleteHabit(theirs.Id, "2026-11-02"));
        Assert.Equal(1, await db.HabitCheckIns.CountAsync(x => x.UserId == 1));
    }

    private static async Task<SqliteConnection> OpenDatabase()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        return connection;
    }

    private static LifeOSDbContext CreateContext(SqliteConnection connection)
    {
        var options = new DbContextOptionsBuilder<LifeOSDbContext>().UseSqlite(connection).Options;
        var context = new LifeOSDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private static async Task SeedUsers(LifeOSDbContext db)
    {
        db.Users.AddRange(new User { Id = 1, Name = "Current", Email = "current@example.com" },
            new User { Id = 2, Name = "Other", Email = "other@example.com" });
        await db.SaveChangesAsync();
    }

    private static T AsUser<T>(T controller, int userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            }
        };
        return controller;
    }

    private static MemoryStream CreateBackupWithDocument()
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var workspace = archive.CreateEntry("workspace.json");
            using (var writer = new StreamWriter(workspace.Open()))
                writer.Write(JsonSerializer.Serialize(new
                {
                    formatVersion = 1,
                    tasks = new[] { new { id = 10, title = "restored task", description = (string?)null, status = "open", priority = "normal", dueAt = (DateTime?)null, recurrence = "none", reminderAt = (DateTime?)null, reminderSentAt = (DateTime?)null, createdAt = DateTime.UtcNow } },
                    financeTransactions = Array.Empty<object>(), financeBudgets = Array.Empty<object>(), workouts = Array.Empty<object>(),
                    habits = new[] { new { id = 20, name = "Walk", category = "wellness", isActive = true, createdAt = DateTime.UtcNow } },
                    habitCheckIns = new[] { new { id = 21, habitId = 20, date = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc), completedAt = DateTime.UtcNow } },
                    documents = new[] { new { id = 5, title = "Notes", category = "personal", originalFileName = "notes.txt", mediaType = "text/plain", sizeBytes = 17, uploadedAt = DateTime.UtcNow, expiresAt = (DateTime?)null, archivePath = "documents/5-notes.txt" } },
                    careerApplications = Array.Empty<object>(), careerSkills = Array.Empty<object>()
                }));
            var document = archive.CreateEntry("documents/5-notes.txt");
            using var output = new StreamWriter(document.Open());
            output.Write("restored document");
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream CreateZip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (name, content) in files)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        stream.Position = 0;
        return stream;
    }
}
