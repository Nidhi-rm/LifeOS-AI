using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class CareerSkill
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(30)] public string Category { get; set; } = "technical";
    public int CurrentLevel { get; set; } = 1;
    public int TargetLevel { get; set; } = 3;
    public DateTime? GoalDate { get; set; }
    [StringLength(500)] public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
