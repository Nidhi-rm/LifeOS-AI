using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class TaskItem
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(160)] public string Title { get; set; } = string.Empty;
    [StringLength(2000)] public string? Description { get; set; }
    [StringLength(20)] public string Status { get; set; } = "open";
    [StringLength(20)] public string Priority { get; set; } = "normal";
    public DateTime? DueAt { get; set; }
    [StringLength(12)] public string Recurrence { get; set; } = "none";
    public DateTime? ReminderAt { get; set; }
    public DateTime? ReminderSentAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
