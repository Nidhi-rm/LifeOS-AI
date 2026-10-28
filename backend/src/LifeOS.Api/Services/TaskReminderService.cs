using LifeOS.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Services;

public sealed class TaskReminderService(IServiceScopeFactory scopeFactory, ILogger<TaskReminderService> logger) : BackgroundService
{
    private static readonly TimeSpan FailedReminderRetryDelay = TimeSpan.FromMinutes(5);
    private readonly Dictionary<int, DateTime> retryAfterByTaskId = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SendDueReminders(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Task reminder check failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    private async Task SendDueReminders(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LifeOSDbContext>();
        var emailSender = scope.ServiceProvider.GetRequiredService<PasswordResetEmailSender>();
        if (!emailSender.IsConfigured) return;

        var now = DateTime.UtcNow;
        var due = await (from task in db.Tasks
                         join user in db.Users on task.UserId equals user.Id
                         where task.Status != "done" && task.ReminderAt <= now && task.ReminderAt != null && task.ReminderSentAt == null
                         orderby task.ReminderAt
                         select new { Task = task, user.Email }).Take(25).ToListAsync(cancellationToken);

        foreach (var reminder in due)
        {
            if (retryAfterByTaskId.TryGetValue(reminder.Task.Id, out var retryAfter) && retryAfter > now)
                continue;

            try
            {
                await emailSender.SendTaskReminderAsync(reminder.Email, reminder.Task.Title, reminder.Task.DueAt, cancellationToken);
                reminder.Task.ReminderSentAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                retryAfterByTaskId.Remove(reminder.Task.Id);
                logger.LogInformation("Task reminder sent for task {TaskId} and user {UserId}", reminder.Task.Id, reminder.Task.UserId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                retryAfterByTaskId[reminder.Task.Id] = DateTime.UtcNow.Add(FailedReminderRetryDelay);
                logger.LogWarning("Task reminder email failed for task {TaskId}; retrying after {RetryDelayMinutes} minutes. Error: {Error}",
                    reminder.Task.Id, FailedReminderRetryDelay.TotalMinutes, exception.Message);
            }
        }
    }
}
