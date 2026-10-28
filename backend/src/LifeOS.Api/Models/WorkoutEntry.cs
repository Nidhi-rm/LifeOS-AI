using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class WorkoutEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(120)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(30)] public string Type { get; set; } = "strength";
    public int DurationMinutes { get; set; }
    [StringLength(1000)] public string? Notes { get; set; }
    public DateTime PerformedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
