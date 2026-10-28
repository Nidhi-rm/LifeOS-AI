using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Services;

/// <summary>
/// In-process application boundary used by the desktop host. This class contains
/// business operations and deliberately has no ASP.NET request/controller dependency.
/// </summary>
public interface IDesktopBackend
{
    Task<DesktopResult> ExecuteAsync(DesktopCommand command, CancellationToken cancellationToken = default);
}

public sealed class DesktopBackend(IDbContextFactory<LifeOSDbContext> contexts, ILogger<DesktopBackend> logger) : IDesktopBackend
{
    public async Task<DesktopResult> ExecuteAsync(DesktopCommand command, CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Desktop operation started: {Operation}", command.Operation);
            var result = command.Operation switch
            {
                "health" => DesktopResult.Ok(new { status = "ok" }),
                "auth.register" => await Register(command.Data, cancellationToken),
                "auth.login" => await Login(command.Data, cancellationToken),
                "auth.logout" => await Logout(command.Token, cancellationToken),
                "tasks.list" => await ListTasks(command.Token, cancellationToken),
                "tasks.create" => await CreateTask(command.Token, command.Data, cancellationToken),
                "tasks.update" => await UpdateTask(command.Token, command.Id, command.Data, cancellationToken),
                "tasks.delete" => await DeleteTask(command.Token, command.Id, cancellationToken),
                "users.me" => await CurrentUser(command.Token, cancellationToken),
                "users.update" => await UpdateCurrentUser(command.Token, command.Data, cancellationToken),
                _ => DesktopResult.Error(501, "This operation has not been moved to the in-process service layer yet.")
            };
            logger.LogInformation("Desktop operation completed: {Operation}, status {Status}", command.Operation, result.Status);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Desktop backend operation {Operation} failed", command.Operation);
            return DesktopResult.Error(500, "The operation could not be completed.");
        }
    }

    private async Task<DesktopResult> Register(JsonElement data, CancellationToken ct)
    {
        var name = Text(data, "name").Trim();
        var email = Text(data, "email").Trim().ToLowerInvariant();
        var password = Text(data, "password");
        if (name.Length is < 1 or > 100 || !email.Contains('@') || password.Length < 8)
            return DesktopResult.Error(400, "Enter a valid name and email, and a password with at least 8 characters.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        if (await db.Users.AnyAsync(x => x.Email.ToLower() == email, ct))
            return DesktopResult.Error(409, "An account with this email already exists.");
        var user = new User { Name = name, Email = email, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        db.UserCredentials.Add(new UserCredential { UserId = user.Id, PasswordHash = HashPassword(password) });
        var session = NewSession(user.Id);
        db.Sessions.Add(session.Record);
        await db.SaveChangesAsync(ct);
        return DesktopResult.Ok(new { token = session.Token, expiresAt = session.Record.ExpiresAt, user = new { user.Id, user.Name, user.Email } });
    }

    private async Task<DesktopResult> Login(JsonElement data, CancellationToken ct)
    {
        var email = Text(data, "email").Trim().ToLowerInvariant();
        await using var db = await contexts.CreateDbContextAsync(ct);
        var found = await (from user in db.Users join credential in db.UserCredentials on user.Id equals credential.UserId
                           where user.Email.ToLower() == email select new { user, credential.PasswordHash }).FirstOrDefaultAsync(ct);
        if (found is null || !VerifyPassword(Text(data, "password"), found.PasswordHash))
            return DesktopResult.Error(401, "Email or password is incorrect.");
        var session = NewSession(found.user.Id);
        db.Sessions.Add(session.Record);
        await db.SaveChangesAsync(ct);
        return DesktopResult.Ok(new { token = session.Token, expiresAt = session.Record.ExpiresAt, user = new { found.user.Id, found.user.Name, found.user.Email } });
    }

    private async Task<DesktopResult> Logout(string? token, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var session = await FindSession(db, token, ct);
        if (session is not null) { db.Sessions.Remove(session); await db.SaveChangesAsync(ct); }
        return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> ListTasks(string? token, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = await UserId(db, token, ct);
        if (userId is null) return Unauthorized();
        var tasks = await db.Tasks.Where(x => x.UserId == userId).OrderBy(x => x.DueAt).ThenByDescending(x => x.CreatedAt).ToListAsync(ct);
        return DesktopResult.Ok(tasks);
    }

    private async Task<DesktopResult> CreateTask(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = await UserId(db, token, ct);
        if (userId is null) return Unauthorized();
        var title = Text(data, "title").Trim();
        if (title.Length is < 1 or > 160) return DesktopResult.Error(400, "Task title is required and must be at most 160 characters.");
        var task = new TaskItem { UserId = userId.Value, Title = title, Description = NullableText(data, "description"), Status = TextOr(data, "status", "open"), Priority = TextOr(data, "priority", "normal"), DueAt = Date(data, "dueAt"), Recurrence = TextOr(data, "recurrence", "none"), ReminderAt = Date(data, "reminderAt"), CreatedAt = DateTime.UtcNow };
        if (!Valid(task.Status, task.Priority, task.Recurrence)) return DesktopResult.Error(400, "Invalid task status, priority, or recurrence.");
        db.Tasks.Add(task); await db.SaveChangesAsync(ct); return DesktopResult.Ok(task, 201);
    }

    private async Task<DesktopResult> UpdateTask(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        if (id is null) return DesktopResult.Error(400, "Task ID is required.");
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = await UserId(db, token, ct);
        if (userId is null) return Unauthorized();
        var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (task is null) return DesktopResult.Error(404, "Task not found.");
        var status = TextOr(data, "status", task.Status); var priority = TextOr(data, "priority", task.Priority); var recurrence = TextOr(data, "recurrence", task.Recurrence);
        if (!Valid(status, priority, recurrence)) return DesktopResult.Error(400, "Invalid task status, priority, or recurrence.");
        task.Title = TextOr(data, "title", task.Title).Trim(); task.Description = NullableText(data, "description"); task.Status = status; task.Priority = priority; task.Recurrence = recurrence;
        task.DueAt = Date(data, "dueAt"); task.ReminderAt = Date(data, "reminderAt"); task.ReminderSentAt = null;
        await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> DeleteTask(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = await UserId(db, token, ct);
        if (userId is null) return Unauthorized();
        var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == userId, ct);
        if (task is null) return DesktopResult.Error(404, "Task not found.");
        db.Tasks.Remove(task); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> CurrentUser(string? token, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = await UserId(db, token, ct); if (userId is null) return Unauthorized();
        var user = await db.Users.Where(x => x.Id == userId).Select(x => new { x.Id, x.Name, x.Email, x.CreatedAt }).FirstOrDefaultAsync(ct);
        return user is null ? DesktopResult.Error(404, "User not found.") : DesktopResult.Ok(user);
    }

    private async Task<DesktopResult> UpdateCurrentUser(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var userId = await UserId(db, token, ct); if (userId is null) return Unauthorized();
        var user = await db.Users.FindAsync([userId.Value], ct); if (user is null) return DesktopResult.Error(404, "User not found.");
        var email = Text(data, "email").Trim().ToLowerInvariant(); var name = Text(data, "name").Trim();
        if (name.Length is < 1 or > 100 || !email.Contains('@')) return DesktopResult.Error(400, "Enter a valid name and email.");
        if (await db.Users.AnyAsync(x => x.Id != userId && x.Email.ToLower() == email, ct)) return DesktopResult.Error(409, "An account with this email already exists.");
        user.Name = name; user.Email = email; await db.SaveChangesAsync(ct);
        return DesktopResult.Ok(new { user.Id, user.Name, user.Email, user.CreatedAt });
    }

    private static async Task<int?> UserId(LifeOSDbContext db, string? token, CancellationToken ct) => (await FindSession(db, token, ct))?.UserId;
    private static async Task<ApiSession?> FindSession(LifeOSDbContext db, string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash && s.ExpiresAt > DateTime.UtcNow, ct);
    }
    private static (string Token, ApiSession Record) NewSession(int userId)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        return (token, new ApiSession { UserId = userId, TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), ExpiresAt = DateTime.UtcNow.AddDays(7) });
    }
    private static string HashPassword(string password) { var salt = RandomNumberGenerator.GetBytes(16); return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32))}"; }
    private static bool VerifyPassword(string password, string value) { try { var p = value.Split(':'); if (p.Length != 2) return false; var salt = Convert.FromBase64String(p[0]); var expected = Convert.FromBase64String(p[1]); return CryptographicOperations.FixedTimeEquals(expected, Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, expected.Length)); } catch (FormatException) { return false; } }
    private static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string TextOr(JsonElement e, string key, string fallback) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static string? NullableText(JsonElement e, string key) => Text(e, key) is { Length: > 0 } value ? value : null;
    private static DateTime? Date(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var date) ? date.ToUniversalTime() : null;
    private static bool Valid(string status, string priority, string recurrence) => new[] { "open", "in-progress", "done" }.Contains(status) && new[] { "low", "normal", "high" }.Contains(priority) && new[] { "none", "daily", "weekly", "monthly" }.Contains(recurrence);
    private static DesktopResult Unauthorized() => DesktopResult.Error(401, "Your session expired. Please sign in again.");
}

public sealed record DesktopCommand(string Operation, JsonElement Data, string? Token = null, int? Id = null);
public sealed record DesktopResult(int Status, object? Value, string? Message = null)
{
    public static DesktopResult Ok(object value, int status = 200) => new(status, value);
    public static DesktopResult Empty(int status) => new(status, null);
    public static DesktopResult Error(int status, string message) => new(status, new { message }, message);
}
