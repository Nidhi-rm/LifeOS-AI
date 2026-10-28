using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class CareerApplication
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(120)] public string Company { get; set; } = string.Empty;
    [Required, StringLength(120)] public string Role { get; set; } = string.Empty;
    [Required, StringLength(20)] public string Status { get; set; } = "saved";
    public DateTime? AppliedAt { get; set; }
    public DateTime? NextStepAt { get; set; }
    [StringLength(500)] public string? JobUrl { get; set; }
    [StringLength(2000)] public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
