using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Authorize, Route("api/fitness")]
public class FitnessController(LifeOSDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet("workouts")]
    public async Task<IActionResult> GetWorkouts([FromQuery] string? month = null)
    {
        if (month is not null && ParseMonth(month) is null) return BadRequest(new { message = "Month must use YYYY-MM format." });
        var start = ParseMonth(month) ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var workouts = await db.Workouts.AsNoTracking().Where(x => x.UserId == UserId && x.PerformedAt >= start && x.PerformedAt < end)
            .OrderByDescending(x => x.PerformedAt).ThenByDescending(x => x.Id).ToListAsync();
        return Ok(new WorkoutMonthResponse(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), workouts.Count,
            workouts.Sum(x => x.DurationMinutes), workouts.Select(ToResponse).ToList()));
    }

    [HttpGet("workouts/{id:int}")]
    public async Task<ActionResult<WorkoutResponse>> GetWorkout(int id)
    {
        var entry = await db.Workouts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        return entry is null ? NotFound() : ToResponse(entry);
    }

    [HttpPost("workouts")]
    public async Task<IActionResult> CreateWorkout(WorkoutRequest request)
    {
        if (!ValidWorkoutType(request.Type)) return BadRequest(new { message = "Choose a valid workout type." });
        var entry = new WorkoutEntry { UserId = UserId, Name = request.Name.Trim(), Type = request.Type,
            DurationMinutes = request.DurationMinutes, Notes = request.Notes?.Trim(), PerformedAt = AsUtcDate(request.PerformedAt) };
        db.Workouts.Add(entry); await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetWorkout), new { id = entry.Id }, ToResponse(entry));
    }

    [HttpPut("workouts/{id:int}")]
    public async Task<IActionResult> UpdateWorkout(int id, WorkoutRequest request)
    {
        if (!ValidWorkoutType(request.Type)) return BadRequest(new { message = "Choose a valid workout type." });
        var entry = await db.Workouts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry is null) return NotFound();
        entry.Name = request.Name.Trim(); entry.Type = request.Type; entry.DurationMinutes = request.DurationMinutes;
        entry.Notes = request.Notes?.Trim(); entry.PerformedAt = AsUtcDate(request.PerformedAt);
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpDelete("workouts/{id:int}")]
    public async Task<IActionResult> DeleteWorkout(int id)
    {
        var entry = await db.Workouts.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (entry is null) return NotFound();
        db.Workouts.Remove(entry); await db.SaveChangesAsync(); return NoContent();
    }

    [HttpGet("habits")]
    public async Task<IActionResult> GetHabits([FromQuery] string? month = null)
    {
        if (month is not null && ParseMonth(month) is null) return BadRequest(new { message = "Month must use YYYY-MM format." });
        var start = ParseMonth(month) ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = start.AddMonths(1);
        var habits = await db.FitnessHabits.AsNoTracking().Where(x => x.UserId == UserId && x.IsActive).OrderBy(x => x.Name).ToListAsync();
        var checkIns = await db.HabitCheckIns.AsNoTracking().Where(x => x.UserId == UserId && x.Date >= start && x.Date < end)
            .Select(x => new { x.HabitId, x.Date }).ToListAsync();
        var rows = habits.Select(h => new HabitResponse(h.Id, h.Name, h.Category,
            checkIns.Where(c => c.HabitId == h.Id).Select(c => c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList())).ToList();
        return Ok(new HabitMonthResponse(start.ToString("yyyy-MM", CultureInfo.InvariantCulture), rows));
    }

    [HttpPost("habits")]
    public async Task<IActionResult> CreateHabit(HabitRequest request)
    {
        var habit = new FitnessHabit { UserId = UserId, Name = request.Name.Trim(), Category = request.Category.Trim() };
        db.FitnessHabits.Add(habit); await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetHabit), new { id = habit.Id }, new HabitResponse(habit.Id, habit.Name, habit.Category, []));
    }

    [HttpGet("habits/{id:int}")]
    public async Task<ActionResult<HabitResponse>> GetHabit(int id)
    {
        var habit = await db.FitnessHabits.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        return habit is null ? NotFound() : new HabitResponse(habit.Id, habit.Name, habit.Category, []);
    }

    [HttpPut("habits/{id:int}")]
    public async Task<IActionResult> UpdateHabit(int id, HabitRequest request)
    {
        var habit = await db.FitnessHabits.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (habit is null) return NotFound();
        habit.Name = request.Name.Trim(); habit.Category = request.Category.Trim();
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpDelete("habits/{id:int}")]
    public async Task<IActionResult> DeleteHabit(int id)
    {
        var habit = await db.FitnessHabits.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (habit is null) return NotFound();
        db.FitnessHabits.Remove(habit); await db.SaveChangesAsync(); return NoContent();
    }

    [HttpPut("habits/{id:int}/check-ins/{date}")]
    public async Task<IActionResult> CompleteHabit(int id, string date)
    {
        if (!TryParseDate(date, out var day)) return BadRequest(new { message = "Date must use YYYY-MM-DD format." });
        var habit = await db.FitnessHabits.AnyAsync(x => x.Id == id && x.UserId == UserId && x.IsActive);
        if (!habit) return NotFound();
        var exists = await db.HabitCheckIns.AnyAsync(x => x.HabitId == id && x.UserId == UserId && x.Date == day);
        if (!exists) { db.HabitCheckIns.Add(new HabitCheckIn { HabitId = id, UserId = UserId, Date = day }); await db.SaveChangesAsync(); }
        return NoContent();
    }

    [HttpDelete("habits/{id:int}/check-ins/{date}")]
    public async Task<IActionResult> UndoHabit(int id, string date)
    {
        if (!TryParseDate(date, out var day)) return BadRequest(new { message = "Date must use YYYY-MM-DD format." });
        var checkIn = await db.HabitCheckIns.FirstOrDefaultAsync(x => x.HabitId == id && x.UserId == UserId && x.Date == day);
        if (checkIn is null) return NotFound();
        db.HabitCheckIns.Remove(checkIn); await db.SaveChangesAsync(); return NoContent();
    }

    private static bool ValidWorkoutType(string type) => new[] { "strength", "cardio", "mobility", "sport", "other" }.Contains(type);
    private static DateTime AsUtcDate(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
    private static DateTime? ParseMonth(string? month) => DateTime.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc) : null;
    private static bool TryParseDate(string date, out DateTime day) => DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    private static WorkoutResponse ToResponse(WorkoutEntry x) => new(x.Id, x.Name, x.Type, x.DurationMinutes, x.Notes, x.PerformedAt);
}

public record WorkoutRequest([Required, StringLength(120, MinimumLength = 1)] string Name,
    [Required, RegularExpression("^(strength|cardio|mobility|sport|other)$")] string Type,
    [Range(1, 1440)] int DurationMinutes, [StringLength(1000)] string? Notes, DateTime PerformedAt);
public record WorkoutResponse(int Id, string Name, string Type, int DurationMinutes, string? Notes, DateTime PerformedAt);
public record WorkoutMonthResponse(string Month, int WorkoutCount, int TotalMinutes, IReadOnlyList<WorkoutResponse> Workouts);
public record HabitRequest([Required, StringLength(100, MinimumLength = 1)] string Name,
    [Required, StringLength(30, MinimumLength = 1)] string Category = "wellness");
public record HabitResponse(int Id, string Name, string Category, IReadOnlyList<string> CompletedDates);
public record HabitMonthResponse(string Month, IReadOnlyList<HabitResponse> Habits);
