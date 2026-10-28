using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class FinanceTransaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(12)] public string Type { get; set; } = "expense";
    // Store minor currency units to avoid floating-point rounding errors.
    public long AmountMinor { get; set; }
    [Required, StringLength(60)] public string Category { get; set; } = string.Empty;
    [StringLength(500)] public string? Note { get; set; }
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
