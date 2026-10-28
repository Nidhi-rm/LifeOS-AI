using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Models;

public class LifeDocument
{
    public int Id { get; set; }
    public int UserId { get; set; }
    [Required, StringLength(160)] public string Title { get; set; } = string.Empty;
    [Required, StringLength(40)] public string Category { get; set; } = "personal";
    [Required, StringLength(255)] public string OriginalFileName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string StoredFileName { get; set; } = string.Empty;
    [Required, StringLength(120)] public string MediaType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
}
