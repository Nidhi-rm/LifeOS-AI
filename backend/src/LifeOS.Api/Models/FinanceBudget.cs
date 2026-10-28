using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class FinanceBudget
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(7, MinimumLength = 7)] public string Month { get; set; } = string.Empty;
    [Required, StringLength(60)] public string Category { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
