namespace LifeOS.Api.Models;

public class HabitCheckIn
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public int HabitId { get; set; }
    public DateTime Date { get; set; }
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}
