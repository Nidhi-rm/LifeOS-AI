using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Authorize, Route("api/career")]
public class CareerController(LifeOSDbContext db) : ControllerBase
{
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<ActionResult<CareerOverviewResponse>> GetOverview()
    {
        var applications = await db.CareerApplications.AsNoTracking().Where(x => x.UserId == UserId)
            .OrderBy(x => x.NextStepAt == null).ThenBy(x => x.NextStepAt).ThenByDescending(x => x.CreatedAt).ToListAsync();
        var skills = await db.CareerSkills.AsNoTracking().Where(x => x.UserId == UserId).OrderBy(x => x.Name).ToListAsync();
        return new CareerOverviewResponse(applications.Count, applications.Count(x => x.Status == "interview"),
            applications.Count(x => x.Status == "offer"), skills.Count,
            applications.Select(ToResponse).ToList(), skills.Select(ToResponse).ToList());
    }

    [HttpPost("applications")]
    public async Task<IActionResult> CreateApplication(ApplicationRequest request)
    {
        if (!ValidJobUrl(request.JobUrl)) return BadRequest(new { message = "Job link must be an HTTP or HTTPS URL." });
        var application = new CareerApplication { UserId = UserId };
        Apply(application, request);
        db.CareerApplications.Add(application);
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetApplication), new { id = application.Id }, ToResponse(application));
    }

    [HttpGet("applications/{id:int}")]
    public async Task<ActionResult<ApplicationResponse>> GetApplication(int id)
    {
        var item = await db.CareerApplications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        return item is null ? NotFound() : ToResponse(item);
    }

    [HttpPut("applications/{id:int}")]
    public async Task<IActionResult> UpdateApplication(int id, ApplicationRequest request)
    {
        if (!ValidJobUrl(request.JobUrl)) return BadRequest(new { message = "Job link must be an HTTP or HTTPS URL." });
        var item = await db.CareerApplications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (item is null) return NotFound();
        Apply(item, request);
        await db.SaveChangesAsync(); return NoContent();
    }

    [HttpDelete("applications/{id:int}")]
    public async Task<IActionResult> DeleteApplication(int id)
    {
        var item = await db.CareerApplications.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (item is null) return NotFound();
        db.CareerApplications.Remove(item); await db.SaveChangesAsync(); return NoContent();
    }

    [HttpPost("skills")]
    public async Task<IActionResult> CreateSkill(SkillRequest request)
    {
        var skill = new CareerSkill { UserId = UserId };
        Apply(skill, request);
        db.CareerSkills.Add(skill); await db.SaveChangesAsync();
        return CreatedAtAction(nameof(GetSkill), new { id = skill.Id }, ToResponse(skill));
    }

    [HttpGet("skills/{id:int}")]
    public async Task<ActionResult<SkillResponse>> GetSkill(int id)
    {
        var skill = await db.CareerSkills.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        return skill is null ? NotFound() : ToResponse(skill);
    }

    [HttpPut("skills/{id:int}")]
    public async Task<IActionResult> UpdateSkill(int id, SkillRequest request)
    {
        var skill = await db.CareerSkills.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (skill is null) return NotFound();
        Apply(skill, request); await db.SaveChangesAsync(); return NoContent();
    }

    [HttpDelete("skills/{id:int}")]
    public async Task<IActionResult> DeleteSkill(int id)
    {
        var skill = await db.CareerSkills.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (skill is null) return NotFound();
        db.CareerSkills.Remove(skill); await db.SaveChangesAsync(); return NoContent();
    }

    private static void Apply(CareerApplication item, ApplicationRequest request)
    {
        item.Company = request.Company.Trim(); item.Role = request.Role.Trim(); item.Status = request.Status;
        item.AppliedAt = request.AppliedAt is null ? null : AsUtcDate(request.AppliedAt.Value);
        item.NextStepAt = request.NextStepAt is null ? null : AsUtcDate(request.NextStepAt.Value);
        item.JobUrl = string.IsNullOrWhiteSpace(request.JobUrl) ? null : request.JobUrl.Trim();
        item.Notes = request.Notes?.Trim();
    }

    private static void Apply(CareerSkill skill, SkillRequest request)
    {
        skill.Name = request.Name.Trim(); skill.Category = request.Category;
        skill.CurrentLevel = request.CurrentLevel; skill.TargetLevel = request.TargetLevel;
        skill.GoalDate = request.GoalDate is null ? null : AsUtcDate(request.GoalDate.Value);
        skill.Notes = request.Notes?.Trim();
    }

    private static bool ValidJobUrl(string? value) => string.IsNullOrWhiteSpace(value) ||
        (Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
    private static DateTime AsUtcDate(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);
    private static ApplicationResponse ToResponse(CareerApplication x) => new(x.Id, x.Company, x.Role, x.Status, x.AppliedAt, x.NextStepAt, x.JobUrl, x.Notes, x.CreatedAt);
    private static SkillResponse ToResponse(CareerSkill x) => new(x.Id, x.Name, x.Category, x.CurrentLevel, x.TargetLevel, x.GoalDate, x.Notes);
}

public record ApplicationRequest([Required, StringLength(120, MinimumLength = 1)] string Company,
    [Required, StringLength(120, MinimumLength = 1)] string Role,
    [Required, RegularExpression("^(saved|applied|interview|offer|rejected)$")] string Status,
    DateTime? AppliedAt, DateTime? NextStepAt, [StringLength(500)] string? JobUrl, [StringLength(2000)] string? Notes);
public record SkillRequest([Required, StringLength(100, MinimumLength = 1)] string Name,
    [Required, RegularExpression("^(technical|soft|language|certification|other)$")] string Category,
    [Range(1, 5)] int CurrentLevel, [Range(1, 5)] int TargetLevel, DateTime? GoalDate, [StringLength(500)] string? Notes);
public record ApplicationResponse(int Id, string Company, string Role, string Status, DateTime? AppliedAt,
    DateTime? NextStepAt, string? JobUrl, string? Notes, DateTime CreatedAt);
public record SkillResponse(int Id, string Name, string Category, int CurrentLevel, int TargetLevel, DateTime? GoalDate, string? Notes);
public record CareerOverviewResponse(int ApplicationCount, int InterviewCount, int OfferCount, int SkillCount,
    IReadOnlyList<ApplicationResponse> Applications, IReadOnlyList<SkillResponse> Skills);
