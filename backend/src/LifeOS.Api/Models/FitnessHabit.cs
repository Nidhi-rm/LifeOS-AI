using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class FitnessHabit
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(100)] public string Name { get; set; } = string.Empty;
    [StringLength(30)] public string Category { get; set; } = "wellness";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
