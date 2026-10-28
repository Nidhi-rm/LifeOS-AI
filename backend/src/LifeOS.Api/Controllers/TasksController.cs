using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Authorize, Route("api/tasks")]
public class TasksController(LifeOSDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<IEnumerable<TaskItem>>> GetTasks() =>
        await db.Tasks.Where(x => x.UserId == UserId).OrderBy(x => x.DueAt).ThenByDescending(x => x.CreatedAt).ToListAsync();

    [HttpPost]
    public async Task<ActionResult<TaskItem>> CreateTask(TaskRequest request)
    {
        if (!ValidOptions(request.Status, request.Priority, request.Recurrence)) return BadRequest(new { message = "Status, priority, or recurrence is invalid." });
        var task = new TaskItem { UserId = UserId, Title = request.Title.Trim(), Description = request.Description?.Trim(), Status = request.Status, Priority = request.Priority, DueAt = request.DueAt?.ToUniversalTime(), Recurrence = request.Recurrence, ReminderAt = request.ReminderAt?.ToUniversalTime() };
        db.Tasks.Add(task);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetTasks), new { id = task.Id }, task);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateTask(int id, TaskRequest request)
    {
        if (!ValidOptions(request.Status, request.Priority, request.Recurrence)) return BadRequest(new { message = "Status, priority, or recurrence is invalid." });
        var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (task is null) return NotFound();
        task.Title = request.Title.Trim(); task.Description = request.Description?.Trim();
        var wasCompleted = task.Status == "done";
        task.Status = request.Status; task.Priority = request.Priority; task.DueAt = request.DueAt?.ToUniversalTime(); task.Recurrence = request.Recurrence;
        var reminder = request.ReminderAt?.ToUniversalTime();
        if (task.ReminderAt != reminder) task.ReminderSentAt = null;
        task.ReminderAt = reminder;
        if (!wasCompleted && task.Status == "done" && task.Recurrence != "none")
        {
            var nextDue = task.DueAt is null ? (DateTime?)null : NextOccurrence(task.DueAt.Value, task.Recurrence);
            db.Tasks.Add(new TaskItem
            {
                UserId = UserId, Title = task.Title, Description = task.Description, Status = "open", Priority = task.Priority,
                DueAt = nextDue, Recurrence = task.Recurrence,
                ReminderAt = task.ReminderAt is null ? null : NextOccurrence(task.ReminderAt.Value, task.Recurrence)
            });
        }
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteTask(int id)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (task is null) return NotFound();
        db.Tasks.Remove(task); await db.SaveChangesAsync(); return NoContent();
    }

    private static bool ValidOptions(string status, string priority, string recurrence) =>
        new[] { "open", "in-progress", "done" }.Contains(status) && new[] { "low", "normal", "high" }.Contains(priority)
        && new[] { "none", "daily", "weekly", "monthly" }.Contains(recurrence);

    private static DateTime NextOccurrence(DateTime date, string recurrence) => recurrence switch
    {
        "daily" => date.AddDays(1), "weekly" => date.AddDays(7), "monthly" => date.AddMonths(1), _ => date
    };
}

public record TaskRequest([Required, StringLength(160, MinimumLength = 1)] string Title,
    [StringLength(2000)] string? Description, string Status = "open", string Priority = "normal", DateTime? DueAt = null,
    string Recurrence = "none", DateTime? ReminderAt = null);
