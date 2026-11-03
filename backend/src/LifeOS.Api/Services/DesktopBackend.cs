using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using System.IO.Compression;
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

public sealed class DesktopBackend(IDbContextFactory<LifeOSDbContext> contexts, ILogger<DesktopBackend> logger,
    IConfiguration configuration, OpenAiAssistantService assistant, PasswordResetEmailSender emailSender) : IDesktopBackend
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
                "auth.change.password" => await ChangePassword(command.Token, command.Data, cancellationToken),
                "auth.forgot.password" => await ForgotPassword(command.Data, cancellationToken),
                "auth.reset.password" => await ResetPassword(command.Data, cancellationToken),
                "auth.logout" => await Logout(command.Token, cancellationToken),
                "tasks.list" => await ListTasks(command.Token, cancellationToken),
                "tasks.create" => await CreateTask(command.Token, command.Data, cancellationToken),
                "tasks.update" => await UpdateTask(command.Token, command.Id, command.Data, cancellationToken),
                "tasks.delete" => await DeleteTask(command.Token, command.Id, cancellationToken),
                "users.me" => await CurrentUser(command.Token, cancellationToken),
                "users.update" => await UpdateCurrentUser(command.Token, command.Data, cancellationToken),
                "finance.list" => await FinanceList(command.Token, command.Data, cancellationToken),
                "finance.create" => await FinanceCreate(command.Token, command.Data, cancellationToken),
                "finance.update" => await FinanceUpdate(command.Token, command.Id, command.Data, cancellationToken),
                "finance.delete" => await FinanceDelete(command.Token, command.Id, cancellationToken),
                "finance.budgets.save" => await SaveBudget(command.Token, command.Data, cancellationToken),
                "finance.budgets.delete" => await DeleteBudget(command.Token, command.Id, cancellationToken),
                "fitness.workouts.list" => await ListWorkouts(command.Token, command.Data, cancellationToken),
                "fitness.workouts.create" => await CreateWorkout(command.Token, command.Data, cancellationToken),
                "fitness.workouts.update" => await UpdateWorkout(command.Token, command.Id, command.Data, cancellationToken),
                "fitness.workouts.delete" => await DeleteWorkout(command.Token, command.Id, cancellationToken),
                "fitness.habits.list" => await ListHabits(command.Token, command.Data, cancellationToken),
                "fitness.habits.create" => await CreateHabit(command.Token, command.Data, cancellationToken),
                "fitness.habits.update" => await UpdateHabit(command.Token, command.Id, command.Data, cancellationToken),
                "fitness.habits.delete" => await DeleteHabit(command.Token, command.Id, cancellationToken),
                "fitness.habits.checkin.save" => await HabitCheckIn(command.Token, command.Id, command.Data, false, cancellationToken),
                "fitness.habits.checkin.delete" => await HabitCheckIn(command.Token, command.Id, command.Data, true, cancellationToken),
                "career.overview" => await CareerOverview(command.Token, cancellationToken),
                "career.applications.create" => await CreateApplication(command.Token, command.Data, cancellationToken),
                "career.applications.update" => await UpdateApplication(command.Token, command.Id, command.Data, cancellationToken),
                "career.applications.delete" => await DeleteApplication(command.Token, command.Id, cancellationToken),
                "career.skills.create" => await CreateSkill(command.Token, command.Data, cancellationToken),
                "career.skills.update" => await UpdateSkill(command.Token, command.Id, command.Data, cancellationToken),
                "career.skills.delete" => await DeleteSkill(command.Token, command.Id, cancellationToken),
                "documents.list" => await ListDocuments(command.Token, command.Data, cancellationToken),
                "documents.create" => await UploadDocument(command.Token, command.Data, cancellationToken),
                "documents.download" => await DownloadDocument(command.Token, command.Id, cancellationToken),
                "documents.update" => await UpdateDocument(command.Token, command.Id, command.Data, cancellationToken),
                "documents.delete" => await DeleteDocument(command.Token, command.Id, cancellationToken),
                "assistant.status" => DesktopResult.Ok(new { configured = assistant.IsConfigured }),
                "assistant.chat" => await AssistantChat(command.Token, command.Data, cancellationToken),
                "export.create" => await ExportWorkspace(command.Token, cancellationToken),
                "export.restore" => await RestoreWorkspace(command.Token, command.Data, cancellationToken),
                _ => DesktopResult.Error(501, "This operation is not supported by the desktop app.")
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
        if (name.Length is < 1 or > 100 || email.Length > 255 || !email.Contains('@') || password.Length is < 10 or > 128)
            return DesktopResult.Error(400, "Enter a valid name and email, and a password with at least 10 characters.");
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

    private async Task<DesktopResult> ChangePassword(string? token, JsonElement data, CancellationToken ct)
    {
        var current = Text(data, "currentPassword"); var next = Text(data, "newPassword"); if (next.Length is < 10 or > 128) return DesktopResult.Error(400, "New password must be between 10 and 128 characters.");
        await using var db = await contexts.CreateDbContextAsync(ct); var session = await FindSession(db, token, ct); if (session is null) return Unauthorized();
        var credential = await db.UserCredentials.FirstOrDefaultAsync(x => x.UserId == session.UserId, ct); if (credential is null || !VerifyPassword(current, credential.PasswordHash)) return DesktopResult.Error(400, "Current password is incorrect.");
        credential.PasswordHash = HashPassword(next); var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token!)));
        var others = await db.Sessions.Where(x => x.UserId == session.UserId && x.TokenHash != hash).ToListAsync(ct); db.Sessions.RemoveRange(others); await db.SaveChangesAsync(ct); return DesktopResult.Ok(new { message = "Password changed. Other signed-in sessions have been signed out." });
    }

    private async Task<DesktopResult> ForgotPassword(JsonElement data, CancellationToken ct)
    {
        const string message = "If an account matches that email, a reset code will be sent shortly.";
        if (!emailSender.IsConfigured) return DesktopResult.Error(503, "Password reset email is not configured yet. Set the SMTP settings and restart LifeOS.");
        var email = Text(data, "email").Trim().ToLowerInvariant(); await using var db = await contexts.CreateDbContextAsync(ct); var user = await db.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == email, ct); if (user is null) return DesktopResult.Ok(new { message }, 202);
        var now = DateTime.UtcNow; var reset = await db.PasswordResetCodes.FirstOrDefaultAsync(x => x.UserId == user.Id, ct); if (reset is not null && reset.CreatedAt > now.AddMinutes(-1)) return DesktopResult.Ok(new { message }, 202);
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"); if (reset is null) { reset = new PasswordResetCode { UserId = user.Id }; db.PasswordResetCodes.Add(reset); }
        reset.CodeHash = HashPassword(code); reset.CreatedAt = now; reset.ExpiresAt = now.AddMinutes(15); reset.FailedAttempts = 0; await db.SaveChangesAsync(ct);
        try { await emailSender.SendCodeAsync(user.Email, code, ct); } catch (Exception ex) when (ex is not OperationCanceledException) { db.PasswordResetCodes.Remove(reset); await db.SaveChangesAsync(ct); logger.LogError(ex, "Password reset email delivery failed for user {UserId}", user.Id); }
        return DesktopResult.Ok(new { message }, 202);
    }

    private async Task<DesktopResult> ResetPassword(JsonElement data, CancellationToken ct)
    {
        var email = Text(data, "email").Trim().ToLowerInvariant(); var code = Text(data, "code").Trim(); var next = Text(data, "newPassword");
        if (code.Length != 6 || !code.All(char.IsAsciiDigit) || next.Length is < 10 or > 128) return DesktopResult.Error(400, "Enter a valid code and a password with at least 10 characters.");
        await using var db = await contexts.CreateDbContextAsync(ct); var user = await db.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == email, ct); var reset = user is null ? null : await db.PasswordResetCodes.FirstOrDefaultAsync(x => x.UserId == user.Id, ct);
        const string invalid = "That code is invalid or expired. Request a new code and try again.";
        if (user is null || reset is null) return DesktopResult.Error(400, invalid);
        if (reset.ExpiresAt <= DateTime.UtcNow || reset.FailedAttempts >= 5) { db.PasswordResetCodes.Remove(reset); await db.SaveChangesAsync(ct); return DesktopResult.Error(400, invalid); }
        reset.FailedAttempts++; if (!VerifyPassword(code, reset.CodeHash)) { await db.SaveChangesAsync(ct); return DesktopResult.Error(400, invalid); }
        var credential = await db.UserCredentials.FirstOrDefaultAsync(x => x.UserId == user.Id, ct); if (credential is null) return DesktopResult.Error(400, invalid);
        credential.PasswordHash = HashPassword(next); db.PasswordResetCodes.Remove(reset); var sessions = await db.Sessions.Where(x => x.UserId == user.Id).ToListAsync(ct); db.Sessions.RemoveRange(sessions); await db.SaveChangesAsync(ct);
        return DesktopResult.Ok(new { message = "Your password has been changed. Sign in with your new password." });
    }

    private async Task<DesktopResult> FinanceList(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var monthText = Text(data, "month"); if (monthText.Length > 0 && !TryMonth(monthText, out _)) return DesktopResult.Error(400, "Month must use YYYY-MM format.");
        var start = monthText.Length == 0 ? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc) : Month(monthText);
        var end = start.AddMonths(1); var entries = await db.FinanceTransactions.AsNoTracking().Where(x => x.UserId == uid && x.OccurredAt >= start && x.OccurredAt < end).OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).ToListAsync(ct);
        var budgets = await db.FinanceBudgets.AsNoTracking().Where(x => x.UserId == uid && x.Month == start.ToString("yyyy-MM", CultureInfo.InvariantCulture)).OrderBy(x => x.Category).ToListAsync(ct);
        var income = entries.Where(x => x.Type == "income").Sum(x => x.AmountMinor); var expenses = entries.Where(x => x.Type == "expense").Sum(x => x.AmountMinor);
        var spending = entries.Where(x => x.Type == "expense").GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(x => x.AmountMinor), StringComparer.OrdinalIgnoreCase);
        return DesktopResult.Ok(new FinanceMonthResponse(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), "INR", income, expenses, income - expenses,
            entries.Select(x => new FinanceTransactionResponse(x.Id, x.Type, x.AmountMinor, x.Category, x.Note, x.OccurredAt)).ToList(),
            budgets.Select(x => new FinanceBudgetResponse(x.Id, x.Category, x.AmountMinor)).ToList(), spending));
    }

    private async Task<DesktopResult> FinanceCreate(string? token, JsonElement data, CancellationToken ct)
    {
        var request = Read<TransactionRequest>(data); if (request is null || !Valid(request)) return DesktopResult.Error(400, "Enter a valid transaction type, amount, category, and date.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = new FinanceTransaction { UserId = uid.Value, Type = request.Type, AmountMinor = checked((long)(request.Amount * 100m)), Category = request.Category.Trim(), Note = request.Note?.Trim(), OccurredAt = DateTime.SpecifyKind(request.OccurredAt.Date, DateTimeKind.Utc) };
        db.FinanceTransactions.Add(row); await db.SaveChangesAsync(ct); return DesktopResult.Ok(new FinanceTransactionResponse(row.Id, row.Type, row.AmountMinor, row.Category, row.Note, row.OccurredAt), 201);
    }

    private async Task<DesktopResult> FinanceUpdate(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        var request = Read<TransactionRequest>(data); if (id is null || request is null || !Valid(request)) return DesktopResult.Error(400, "Enter a valid transaction type, amount, category, and date.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.FinanceTransactions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Transaction not found.");
        row.Type = request.Type; row.AmountMinor = checked((long)(request.Amount * 100m)); row.Category = request.Category.Trim(); row.Note = request.Note?.Trim(); row.OccurredAt = DateTime.SpecifyKind(request.OccurredAt.Date, DateTimeKind.Utc);
        await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> FinanceDelete(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.FinanceTransactions.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Transaction not found.");
        db.FinanceTransactions.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> SaveBudget(string? token, JsonElement data, CancellationToken ct)
    {
        var request = Read<BudgetRequest>(data); if (request is null || request.Category.Trim().Length is < 1 or > 60 || !TryMonth(request.Month, out _) || request.Amount <= 0 || request.Amount > 999999999.99m || request.Amount * 100m != decimal.Truncate(request.Amount * 100m)) return DesktopResult.Error(400, "Enter a valid month, category, and amount with at most two decimal places.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var category = request.Category.Trim(); var rows = await db.FinanceBudgets.Where(x => x.UserId == uid && x.Month == request.Month).ToListAsync(ct);
        var row = rows.FirstOrDefault(x => string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase));
        if (row is null) { row = new FinanceBudget { UserId = uid.Value, Month = request.Month, Category = category }; db.FinanceBudgets.Add(row); }
        row.AmountMinor = checked((long)(request.Amount * 100m)); await db.SaveChangesAsync(ct); return DesktopResult.Ok(new FinanceBudgetResponse(row.Id, row.Category, row.AmountMinor));
    }

    private async Task<DesktopResult> DeleteBudget(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.FinanceBudgets.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Budget not found.");
        db.FinanceBudgets.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> ListWorkouts(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var text = Text(data, "month"); if (text.Length > 0 && !TryMonth(text, out _)) return DesktopResult.Error(400, "Month must use YYYY-MM format.");
        var start = text.Length == 0 ? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc) : Month(text); var end = start.AddMonths(1);
        var rows = await db.Workouts.AsNoTracking().Where(x => x.UserId == uid && x.PerformedAt >= start && x.PerformedAt < end).OrderByDescending(x => x.PerformedAt).ThenByDescending(x => x.Id).ToListAsync(ct);
        var items = rows.Select(ToWorkout).ToList(); return DesktopResult.Ok(new WorkoutMonthResponse(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows.Count, rows.Sum(x => x.DurationMinutes), items));
    }

    private async Task<DesktopResult> CreateWorkout(string? token, JsonElement data, CancellationToken ct)
    {
        var request = Read<WorkoutRequest>(data); if (request is null || request.Name.Trim().Length is < 1 or > 120 || !new[] { "strength", "cardio", "mobility", "sport", "other" }.Contains(request.Type) || request.DurationMinutes is < 1 or > 1440) return DesktopResult.Error(400, "Enter valid workout details.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = new WorkoutEntry { UserId = uid.Value, Name = request.Name.Trim(), Type = request.Type, DurationMinutes = request.DurationMinutes, Notes = request.Notes?.Trim(), PerformedAt = UtcDate(request.PerformedAt) };
        db.Workouts.Add(row); await db.SaveChangesAsync(ct); return DesktopResult.Ok(ToWorkout(row), 201);
    }

    private async Task<DesktopResult> UpdateWorkout(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        var request = Read<WorkoutRequest>(data); if (id is null || request is null || request.Name.Trim().Length is < 1 or > 120 || !new[] { "strength", "cardio", "mobility", "sport", "other" }.Contains(request.Type) || request.DurationMinutes is < 1 or > 1440) return DesktopResult.Error(400, "Enter valid workout details.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.Workouts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Workout not found.");
        row.Name = request.Name.Trim(); row.Type = request.Type; row.DurationMinutes = request.DurationMinutes; row.Notes = request.Notes?.Trim(); row.PerformedAt = UtcDate(request.PerformedAt); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> DeleteWorkout(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.Workouts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Workout not found.");
        db.Workouts.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> ListHabits(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var text = Text(data, "month"); if (text.Length > 0 && !TryMonth(text, out _)) return DesktopResult.Error(400, "Month must use YYYY-MM format.");
        var start = text.Length == 0 ? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc) : Month(text); var end = start.AddMonths(1);
        var habits = await db.FitnessHabits.AsNoTracking().Where(x => x.UserId == uid && x.IsActive).OrderBy(x => x.Name).ToListAsync(ct);
        var checks = await db.HabitCheckIns.AsNoTracking().Where(x => x.UserId == uid && x.Date >= start && x.Date < end).Select(x => new { x.HabitId, x.Date }).ToListAsync(ct);
        return DesktopResult.Ok(new HabitMonthResponse(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), habits.Select(h => new HabitResponse(h.Id, h.Name, h.Category, checks.Where(c => c.HabitId == h.Id).Select(c => c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList())).ToList()));
    }

    private async Task<DesktopResult> CreateHabit(string? token, JsonElement data, CancellationToken ct)
    {
        var name = Text(data, "name").Trim(); var category = TextOr(data, "category", "wellness").Trim(); if (name.Length is < 1 or > 100 || category.Length is < 1 or > 30) return DesktopResult.Error(400, "Enter a valid habit name and category.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = new FitnessHabit { UserId = uid.Value, Name = name, Category = category }; db.FitnessHabits.Add(row); await db.SaveChangesAsync(ct); return DesktopResult.Ok(new HabitResponse(row.Id, row.Name, row.Category, []), 201);
    }

    private async Task<DesktopResult> UpdateHabit(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        var name = Text(data, "name").Trim(); var category = TextOr(data, "category", "wellness").Trim(); if (id is null || name.Length is < 1 or > 100 || category.Length is < 1 or > 30) return DesktopResult.Error(400, "Enter a valid habit name and category.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.FitnessHabits.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Habit not found."); row.Name = name; row.Category = category; await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> DeleteHabit(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var row = await db.FitnessHabits.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Habit not found."); db.FitnessHabits.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> HabitCheckIn(string? token, int? id, JsonElement data, bool remove, CancellationToken ct)
    {
        var text = Text(data, "date"); if (!DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return DesktopResult.Error(400, "Date must use YYYY-MM-DD format.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        if (remove) { var row = await db.HabitCheckIns.FirstOrDefaultAsync(x => x.HabitId == id && x.UserId == uid && x.Date == date, ct); if (row is null) return DesktopResult.Error(404, "Habit check-in not found."); db.HabitCheckIns.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204); }
        var exists = await db.FitnessHabits.AnyAsync(x => x.Id == id && x.UserId == uid && x.IsActive, ct); if (!exists) return DesktopResult.Error(404, "Habit not found.");
        if (!await db.HabitCheckIns.AnyAsync(x => x.HabitId == id && x.UserId == uid && x.Date == date, ct)) { db.HabitCheckIns.Add(new HabitCheckIn { HabitId = id!.Value, UserId = uid.Value, Date = date }); await db.SaveChangesAsync(ct); }
        return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> CareerOverview(string? token, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var apps = await db.CareerApplications.AsNoTracking().Where(x => x.UserId == uid).OrderBy(x => x.NextStepAt == null).ThenBy(x => x.NextStepAt).ThenByDescending(x => x.CreatedAt).ToListAsync(ct);
        var skills = await db.CareerSkills.AsNoTracking().Where(x => x.UserId == uid).OrderBy(x => x.Name).ToListAsync(ct);
        return DesktopResult.Ok(new CareerOverviewResponse(apps.Count, apps.Count(x => x.Status == "interview"), apps.Count(x => x.Status == "offer"), skills.Count, apps.Select(ToApplication).ToList(), skills.Select(ToSkill).ToList()));
    }

    private async Task<DesktopResult> CreateApplication(string? token, JsonElement data, CancellationToken ct)
    {
        var r = Read<ApplicationRequest>(data); if (!Valid(r)) return DesktopResult.Error(400, "Enter a valid application and an HTTP or HTTPS job link.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var row = new CareerApplication { UserId = uid.Value }; Apply(row, r!); db.CareerApplications.Add(row); await db.SaveChangesAsync(ct); return DesktopResult.Ok(ToApplication(row), 201);
    }

    private async Task<DesktopResult> UpdateApplication(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        var r = Read<ApplicationRequest>(data); if (id is null || !Valid(r)) return DesktopResult.Error(400, "Enter a valid application and an HTTP or HTTPS job link.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var row = await db.CareerApplications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Application not found."); Apply(row, r!); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> DeleteApplication(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var row = await db.CareerApplications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Application not found."); db.CareerApplications.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> CreateSkill(string? token, JsonElement data, CancellationToken ct)
    {
        var r = Read<SkillRequest>(data); if (!Valid(r)) return DesktopResult.Error(400, "Enter a valid skill and level."); await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var row = new CareerSkill { UserId = uid.Value }; Apply(row, r!); db.CareerSkills.Add(row); await db.SaveChangesAsync(ct); return DesktopResult.Ok(ToSkill(row), 201);
    }

    private async Task<DesktopResult> UpdateSkill(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        var r = Read<SkillRequest>(data); if (id is null || !Valid(r)) return DesktopResult.Error(400, "Enter a valid skill and level."); await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var row = await db.CareerSkills.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Skill not found."); Apply(row, r!); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> DeleteSkill(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var row = await db.CareerSkills.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (row is null) return DesktopResult.Error(404, "Skill not found."); db.CareerSkills.Remove(row); await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> ListDocuments(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var query = db.Documents.AsNoTracking().Where(x => x.UserId == uid); var search = Text(data, "q").Trim(); var category = Text(data, "category").Trim();
        if (search.Length > 0) query = query.Where(x => EF.Functions.Like(x.Title, $"%{search}%") || EF.Functions.Like(x.OriginalFileName, $"%{search}%"));
        if (category.Length > 0) query = query.Where(x => x.Category == category);
        var rows = await query.OrderBy(x => x.ExpiresAt == null).ThenBy(x => x.ExpiresAt).ThenByDescending(x => x.UploadedAt).ToListAsync(ct);
        return DesktopResult.Ok(rows.Select(ToDocument).ToList());
    }

    private async Task<DesktopResult> UploadDocument(string? token, JsonElement data, CancellationToken ct)
    {
        if (!data.TryGetProperty("file", out var file) || file.ValueKind != JsonValueKind.Object) return DesktopResult.Error(400, "Choose a file to upload.");
        var name = SafeFileName(Text(file, "fileName")); var ext = Path.GetExtension(name).ToLowerInvariant();
        var mime = ext switch { ".pdf" => "application/pdf", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".txt" => "text/plain", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", _ => null };
        if (mime is null) return DesktopResult.Error(400, "File type not supported. Use PDF, DOCX, XLSX, TXT, PNG, or JPG.");
        byte[] bytes; try { bytes = Convert.FromBase64String(Text(file, "data")); } catch (FormatException) { return DesktopResult.Error(400, "The selected file could not be read."); }
        if (bytes.Length == 0) return DesktopResult.Error(400, "Choose a file to upload."); if (bytes.Length > 15 * 1024 * 1024) return DesktopResult.Error(400, "Files must be 15 MB or smaller.");
        var title = Text(data, "title").Trim(); if (title.Length == 0) title = Path.GetFileNameWithoutExtension(name); if (title.Length > 160) return DesktopResult.Error(400, "Document title cannot exceed 160 characters.");
        var category = TextOr(data, "category", "personal").Trim(); if (category.Length is < 1 or > 40) return DesktopResult.Error(400, "Category must be between 1 and 40 characters.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var directory = UserDocumentDirectory(uid.Value); Directory.CreateDirectory(directory); var stored = $"{Guid.NewGuid():N}{ext}"; var path = Path.Combine(directory, stored);
        try { await File.WriteAllBytesAsync(path, bytes, ct); var expiry = ParseDate(Text(data, "expiresAt")); var doc = new LifeDocument { UserId = uid.Value, Title = title, Category = category, OriginalFileName = name, StoredFileName = stored, MediaType = mime, SizeBytes = bytes.LongLength, UploadedAt = DateTime.UtcNow, ExpiresAt = expiry is null ? null : UtcDate(expiry.Value) }; db.Documents.Add(doc); await db.SaveChangesAsync(ct); return DesktopResult.Ok(ToDocument(doc), 201); }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    private async Task<DesktopResult> DownloadDocument(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var doc = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (doc is null) return DesktopResult.Error(404, "Document not found.");
        var path = Path.Combine(UserDocumentDirectory(uid.Value), doc.StoredFileName); if (!File.Exists(path)) return DesktopResult.Error(404, "The stored file is missing.");
        return DesktopResult.File(await File.ReadAllBytesAsync(path, ct), doc.MediaType, doc.OriginalFileName);
    }

    private async Task<DesktopResult> UpdateDocument(string? token, int? id, JsonElement data, CancellationToken ct)
    {
        var title = Text(data, "title").Trim(); var category = Text(data, "category").Trim(); if (id is null || title.Length is < 1 or > 160 || category.Length is < 1 or > 40) return DesktopResult.Error(400, "Enter a valid title and category.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var doc = await db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (doc is null) return DesktopResult.Error(404, "Document not found.");
        doc.Title = title; doc.Category = category; var expiryText = Text(data, "expiresAt"); doc.ExpiresAt = ParseDate(expiryText) is { } expiry ? UtcDate(expiry) : null; await db.SaveChangesAsync(ct); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> DeleteDocument(string? token, int? id, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized(); var doc = await db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.UserId == uid, ct); if (doc is null) return DesktopResult.Error(404, "Document not found.");
        var path = Path.Combine(UserDocumentDirectory(uid.Value), doc.StoredFileName); db.Documents.Remove(doc); await db.SaveChangesAsync(ct); if (File.Exists(path)) File.Delete(path); return DesktopResult.Empty(204);
    }

    private async Task<DesktopResult> ExportWorkspace(string? token, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        var user = await db.Users.AsNoTracking().Where(x => x.Id == uid).Select(x => new { x.Name, x.Email, x.CreatedAt }).SingleOrDefaultAsync(ct); if (user is null) return DesktopResult.Error(404, "User not found.");
        var tasks = await db.Tasks.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var finance = await db.FinanceTransactions.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var budgets = await db.FinanceBudgets.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct);
        var workouts = await db.Workouts.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var habits = await db.FitnessHabits.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var checkIns = await db.HabitCheckIns.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct);
        var docs = await db.Documents.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var apps = await db.CareerApplications.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var skills = await db.CareerSkills.AsNoTracking().Where(x => x.UserId == uid).ToListAsync(ct); var directory = UserDocumentDirectory(uid.Value);
        var missing = docs.FirstOrDefault(d => !File.Exists(Path.Combine(directory, d.StoredFileName))); if (missing is not null) return DesktopResult.Error(409, $"The stored file for '{missing.OriginalFileName}' is missing, so a complete backup cannot be created.");
        var documentMetadata = docs.Select(d => new { d.Id, d.Title, d.Category, d.OriginalFileName, d.MediaType, d.SizeBytes, d.UploadedAt, d.ExpiresAt, ArchivePath = $"documents/{d.Id}-{SafeFileName(d.OriginalFileName)}" }).ToList();
        var backup = new { formatVersion = 1, exportedAtUtc = DateTime.UtcNow, profile = user, tasks, financeTransactions = finance, financeBudgets = budgets, workouts, habits, habitCheckIns = checkIns, documents = documentMetadata, careerApplications = apps, careerSkills = skills };
        await using var stream = new MemoryStream(); using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            var entry = archive.CreateEntry("workspace.json", CompressionLevel.Fastest); await using (var writer = entry.Open()) await JsonSerializer.SerializeAsync(writer, backup, new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
            foreach (var doc in docs) { ct.ThrowIfCancellationRequested(); var file = archive.CreateEntry($"documents/{doc.Id}-{SafeFileName(doc.OriginalFileName)}", CompressionLevel.Fastest); await using var output = file.Open(); await using var input = new FileStream(Path.Combine(directory, doc.StoredFileName), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan); await input.CopyToAsync(output, ct); }
        }
        return DesktopResult.File(stream.ToArray(), "application/zip", $"lifeos-backup-{DateTime.UtcNow:yyyy-MM-dd}.zip");
    }

    private async Task<DesktopResult> RestoreWorkspace(string? token, JsonElement data, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var uid = await UserId(db, token, ct);
        if (uid is null) return Unauthorized();
        if (!data.TryGetProperty("backup", out var file) || file.ValueKind != JsonValueKind.Object)
            return DesktopResult.Error(400, "Choose a LifeOS backup ZIP file.");
        var encoded = Text(file, "data");
        if (encoded.Length > 140_000_000) return DesktopResult.Error(400, "The backup must be 100 MB or smaller.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(encoded); }
        catch (FormatException) { return DesktopResult.Error(400, "The selected file could not be read."); }
        try
        {
            await using var stream = new MemoryStream(bytes, writable: false);
            var summary = await WorkspaceBackupRestore.RestoreAsync(db, uid.Value, stream, UserDocumentDirectory(uid.Value), ct);
            return DesktopResult.Ok(new { message = "Workspace restored from backup.", summary });
        }
        catch (WorkspaceBackupException exception) { return DesktopResult.Error(400, exception.Message); }
        catch (InvalidDataException) { return DesktopResult.Error(400, "The selected file is not a valid LifeOS backup ZIP."); }
        catch (System.Text.Json.JsonException) { return DesktopResult.Error(400, "The workspace data in this backup is invalid."); }
    }

    private async Task<DesktopResult> AssistantChat(string? token, JsonElement data, CancellationToken ct)
    {
        var request = Read<ChatRequest>(data); if (request is null || string.IsNullOrWhiteSpace(request.Message) || request.Message.Length > 1200) return DesktopResult.Error(400, "Enter a message up to 1200 characters.");
        await using var db = await contexts.CreateDbContextAsync(ct); var uid = await UserId(db, token, ct); if (uid is null) return Unauthorized();
        if (OpenAiAssistantService.SimpleGreetingReply(request.Message) is { } greeting) return DesktopResult.Ok(new { reply = greeting });
        if (!assistant.IsConfigured) return DesktopResult.Error(503, "Add OPENAI_API_KEY to your environment, then restart LifeOS to enable the assistant.");
        try
        {
            var snapshot = new Dictionary<string, object>(); var selection = request.Context;
            if (selection.Tasks) snapshot["tasks"] = await db.Tasks.AsNoTracking().Where(x => x.UserId == uid && x.Status != "done").OrderBy(x => x.DueAt == null).ThenBy(x => x.DueAt).Take(10).Select(x => new { x.Title, x.Status, x.Priority, x.DueAt }).ToListAsync(ct);
            if (selection.Finance) { var now = DateTime.UtcNow; var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc); var end = start.AddMonths(1); var rows = await db.FinanceTransactions.AsNoTracking().Where(x => x.UserId == uid && x.OccurredAt >= start && x.OccurredAt < end).Select(x => new { x.Type, x.AmountMinor }).ToListAsync(ct); snapshot["finance"] = new { month = start.ToString("yyyy-MM", CultureInfo.InvariantCulture), currency = "INR", income = rows.Where(x => x.Type == "income").Sum(x => x.AmountMinor) / 100m, expenses = rows.Where(x => x.Type == "expense").Sum(x => x.AmountMinor) / 100m }; }
            if (selection.Fitness) { var now = DateTime.UtcNow; var start = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc); var end = start.AddMonths(1); var workouts = await db.Workouts.AsNoTracking().Where(x => x.UserId == uid && x.PerformedAt >= start && x.PerformedAt < end).Select(x => new { x.Name, x.Type, x.DurationMinutes, x.PerformedAt }).ToListAsync(ct); var habits = await db.FitnessHabits.AsNoTracking().Where(x => x.UserId == uid && x.IsActive).Select(x => new { x.Id, x.Name, x.Category }).ToListAsync(ct); var today = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc); var done = await db.HabitCheckIns.AsNoTracking().Where(x => x.UserId == uid && x.Date == today).Select(x => x.HabitId).ToListAsync(ct); snapshot["fitness"] = new { workoutsThisMonth = workouts.Count, minutesThisMonth = workouts.Sum(x => x.DurationMinutes), recentWorkouts = workouts.OrderByDescending(x => x.PerformedAt).Take(6), habits = habits.Select(x => new { x.Name, x.Category, completedToday = done.Contains(x.Id) }) }; }
            if (selection.Documents) { var today = DateTime.UtcNow.Date; var until = today.AddDays(60); snapshot["documents"] = await db.Documents.AsNoTracking().Where(x => x.UserId == uid && x.ExpiresAt != null && x.ExpiresAt >= today && x.ExpiresAt <= until).OrderBy(x => x.ExpiresAt).Take(20).Select(x => new { x.Title, x.Category, x.ExpiresAt }).ToListAsync(ct); }
            if (selection.Career) snapshot["career"] = new { applications = await db.CareerApplications.AsNoTracking().Where(x => x.UserId == uid && x.Status != "rejected").OrderBy(x => x.NextStepAt == null).ThenBy(x => x.NextStepAt).Take(10).Select(x => new { x.Company, x.Role, x.Status, x.NextStepAt }).ToListAsync(ct), skills = await db.CareerSkills.AsNoTracking().Where(x => x.UserId == uid).Select(x => new { x.Name, x.Category, x.CurrentLevel, x.TargetLevel, x.GoalDate }).ToListAsync(ct) };
            var history = string.Join("\n", (request.History ?? []).Where(x => x.Role is "user" or "assistant").TakeLast(4).Select(x => $"{x.Role}: {x.Text.Trim()[..Math.Min(x.Text.Trim().Length, 600)]}"));
            var promptParts = new List<string>();
            if (snapshot.Count > 0) promptParts.Add($"Relevant workspace information (JSON):\n{JsonSerializer.Serialize(snapshot)}");
            if (!string.IsNullOrWhiteSpace(history)) promptParts.Add($"Recent conversation, for continuity:\n{history}");
            promptParts.Add($"User's latest message:\n{request.Message.Trim()[..Math.Min(request.Message.Trim().Length, 1200)]}");
            var prompt = string.Join("\n\n", promptParts);
            return DesktopResult.Ok(new { reply = await assistant.ReplyAsync(prompt, ct) });
        }
        catch (AssistantApiException ex) { logger.LogWarning("Assistant provider returned status {StatusCode} for user {UserId}", ex.StatusCode, uid); return DesktopResult.Error(ex.StatusCode is >= 400 and <= 599 ? ex.StatusCode : 502, ex.Message); }
        catch (HttpRequestException ex) { logger.LogError(ex, "Assistant provider could not be reached for user {UserId}", uid); return DesktopResult.Error(503, assistant.UsesLocalModel ? "LifeOS could not reach Ollama on this PC. Install and start Ollama, then run: ollama run qwen3:4b" : "Could not reach the AI service. Check your connection and try again."); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return DesktopResult.Error(504, "The AI request took too long. Please try again."); }
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
        task.Title = TextOr(data, "title", task.Title).Trim(); task.Description = NullableText(data, "description");
        var wasCompleted = task.Status == "done"; var dueAt = Date(data, "dueAt"); var reminderAt = Date(data, "reminderAt");
        task.Status = status; task.Priority = priority; task.Recurrence = recurrence; task.DueAt = dueAt;
        if (task.ReminderAt != reminderAt) task.ReminderSentAt = null;
        task.ReminderAt = reminderAt;
        if (!wasCompleted && status == "done" && recurrence != "none")
            db.Tasks.Add(new TaskItem { UserId = userId.Value, Title = task.Title, Description = task.Description, Status = "open", Priority = task.Priority,
                DueAt = task.DueAt is null ? null : NextOccurrence(task.DueAt.Value, recurrence), Recurrence = recurrence,
                ReminderAt = task.ReminderAt is null ? null : NextOccurrence(task.ReminderAt.Value, recurrence) });
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
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static T? Read<T>(JsonElement data) where T : class { try { return data.Deserialize<T>(WebJson); } catch (JsonException) { return null; } }
    private static bool Valid(TransactionRequest? r) => r is not null && r.Type is "income" or "expense" && r.Amount is >= 0.01m and <= 999999999.99m && r.Amount * 100m == decimal.Truncate(r.Amount * 100m) && !string.IsNullOrWhiteSpace(r.Category) && r.Category.Trim().Length <= 60 && (r.Note?.Length ?? 0) <= 500;
    private static bool Valid(ApplicationRequest? r) => r is not null && r.Company.Trim().Length is > 0 and <= 120 && r.Role.Trim().Length is > 0 and <= 120 && r.Status is "saved" or "applied" or "interview" or "offer" or "rejected" && (string.IsNullOrWhiteSpace(r.JobUrl) || Uri.TryCreate(r.JobUrl, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)) && (r.Notes?.Length ?? 0) <= 2000;
    private static bool Valid(SkillRequest? r) => r is not null && r.Name.Trim().Length is > 0 and <= 100 && r.Category is "technical" or "soft" or "language" or "certification" or "other" && r.CurrentLevel is >= 1 and <= 5 && r.TargetLevel is >= 1 and <= 5 && (r.Notes?.Length ?? 0) <= 500;
    private static bool Valid(WorkoutRequest? r) => r is not null && r.Name.Trim().Length is > 0 and <= 120 && r.Type is "strength" or "cardio" or "mobility" or "sport" or "other" && r.DurationMinutes is >= 1 and <= 1440 && (r.Notes?.Length ?? 0) <= 1000;
    private static bool TryMonth(string value, out DateTime date) => DateTime.TryParseExact(value, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    private static DateTime Month(string value) { TryMonth(value, out var date); return new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc); }
    private static DateTime UtcDate(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
    private static DateTime? ParseDate(string value) => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date) ? date : null;
    private static WorkoutResponse ToWorkout(WorkoutEntry x) => new(x.Id, x.Name, x.Type, x.DurationMinutes, x.Notes, x.PerformedAt);
    private static ApplicationResponse ToApplication(CareerApplication x) => new(x.Id, x.Company, x.Role, x.Status, x.AppliedAt, x.NextStepAt, x.JobUrl, x.Notes, x.CreatedAt);
    private static SkillResponse ToSkill(CareerSkill x) => new(x.Id, x.Name, x.Category, x.CurrentLevel, x.TargetLevel, x.GoalDate, x.Notes);
    private static void Apply(CareerApplication x, ApplicationRequest r) { x.Company = r.Company.Trim(); x.Role = r.Role.Trim(); x.Status = r.Status; x.AppliedAt = r.AppliedAt is null ? null : UtcDate(r.AppliedAt.Value); x.NextStepAt = r.NextStepAt is null ? null : UtcDate(r.NextStepAt.Value); x.JobUrl = string.IsNullOrWhiteSpace(r.JobUrl) ? null : r.JobUrl.Trim(); x.Notes = r.Notes?.Trim(); }
    private static void Apply(CareerSkill x, SkillRequest r) { x.Name = r.Name.Trim(); x.Category = r.Category; x.CurrentLevel = r.CurrentLevel; x.TargetLevel = r.TargetLevel; x.GoalDate = r.GoalDate is null ? null : UtcDate(r.GoalDate.Value); x.Notes = r.Notes?.Trim(); }
    private string UserDocumentDirectory(int userId) { var setting = configuration["Documents:StoragePath"]; var root = string.IsNullOrWhiteSpace(setting) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeOS", "documents") : Path.GetFullPath(Path.IsPathRooted(setting) ? setting : Path.Combine(AppContext.BaseDirectory, setting)); return Path.Combine(root, userId.ToString(CultureInfo.InvariantCulture)); }
    private static string SafeFileName(string name) { var baseName = Path.GetFileName(name.Replace('\\', '/')); var safe = new string(baseName.Where(c => !char.IsControl(c) && c is not '/' and not '\\').ToArray()).Trim(); return string.IsNullOrWhiteSpace(safe) ? "document" : safe.Length > 255 ? safe[^255..] : safe; }
    private static DocumentResponse ToDocument(LifeDocument x) => new(x.Id, x.Title, x.Category, x.OriginalFileName, x.MediaType, x.SizeBytes, x.UploadedAt, x.ExpiresAt);
    private static string Text(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static string TextOr(JsonElement e, string key, string fallback) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
    private static string? NullableText(JsonElement e, string key) => Text(e, key) is { Length: > 0 } value ? value : null;
    private static DateTime? Date(JsonElement e, string key) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && DateTime.TryParse(v.GetString(), out var date) ? date.ToUniversalTime() : null;
    private static bool Valid(string status, string priority, string recurrence) => new[] { "open", "in-progress", "done" }.Contains(status) && new[] { "low", "normal", "high" }.Contains(priority) && new[] { "none", "daily", "weekly", "monthly" }.Contains(recurrence);
    private static DateTime NextOccurrence(DateTime value, string recurrence) => recurrence switch { "daily" => value.AddDays(1), "weekly" => value.AddDays(7), "monthly" => value.AddMonths(1), _ => value };
    private static DesktopResult Unauthorized() => DesktopResult.Error(401, "Your session expired. Please sign in again.");
}

public sealed record DesktopCommand(string Operation, JsonElement Data, string? Token = null, int? Id = null);
public sealed record DesktopResult(int Status, object? Value, string? Message = null, byte[]? Binary = null, string? ContentType = null, string? FileName = null)
{
    public static DesktopResult Ok(object value, int status = 200) => new(status, value);
    public static DesktopResult Empty(int status) => new(status, null);
    public static DesktopResult Error(int status, string message) => new(status, new { message }, message);
    public static DesktopResult File(byte[] bytes, string contentType, string fileName) => new(200, null, null, bytes, contentType, fileName);
}

internal sealed record TransactionRequest(string Type, decimal Amount, string Category, string? Note, DateTime OccurredAt);
internal sealed record BudgetRequest(string Month, string Category, decimal Amount);
internal sealed record FinanceTransactionResponse(int Id, string Type, long AmountMinor, string Category, string? Note, DateTime OccurredAt);
internal sealed record FinanceBudgetResponse(int Id, string Category, long AmountMinor);
internal sealed record FinanceMonthResponse(string Month, string Currency, long IncomeMinor, long ExpenseMinor, long BalanceMinor,
    IReadOnlyList<FinanceTransactionResponse> Transactions, IReadOnlyList<FinanceBudgetResponse> Budgets, IReadOnlyDictionary<string, long> CategorySpending);
internal sealed record WorkoutRequest(string Name, string Type, int DurationMinutes, string? Notes, DateTime PerformedAt);
internal sealed record WorkoutResponse(int Id, string Name, string Type, int DurationMinutes, string? Notes, DateTime PerformedAt);
internal sealed record WorkoutMonthResponse(string Month, int WorkoutCount, int TotalMinutes, IReadOnlyList<WorkoutResponse> Workouts);
internal sealed record HabitResponse(int Id, string Name, string Category, IReadOnlyList<string> CompletedDates);
internal sealed record HabitMonthResponse(string Month, IReadOnlyList<HabitResponse> Habits);
internal sealed record ApplicationRequest(string Company, string Role, string Status, DateTime? AppliedAt, DateTime? NextStepAt, string? JobUrl, string? Notes);
internal sealed record SkillRequest(string Name, string Category, int CurrentLevel, int TargetLevel, DateTime? GoalDate, string? Notes);
internal sealed record ApplicationResponse(int Id, string Company, string Role, string Status, DateTime? AppliedAt, DateTime? NextStepAt, string? JobUrl, string? Notes, DateTime CreatedAt);
internal sealed record SkillResponse(int Id, string Name, string Category, int CurrentLevel, int TargetLevel, DateTime? GoalDate, string? Notes);
internal sealed record CareerOverviewResponse(int ApplicationCount, int InterviewCount, int OfferCount, int SkillCount, IReadOnlyList<ApplicationResponse> Applications, IReadOnlyList<SkillResponse> Skills);
internal sealed record DocumentResponse(int Id, string Title, string Category, string FileName, string MediaType, long SizeBytes, DateTime UploadedAt, DateTime? ExpiresAt);
internal sealed record ChatRequest(string Message, IReadOnlyList<ChatHistoryItem>? History, ContextSelection Context);
internal sealed record ChatHistoryItem(string Role, string Text);
internal sealed record ContextSelection(bool Tasks, bool Finance, bool Fitness, bool Documents, bool Career);
