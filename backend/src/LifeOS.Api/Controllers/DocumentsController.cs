using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Authorize, Route("api/documents")]
public class DocumentsController : ControllerBase
{
    private const long MaximumFileBytes = 15 * 1024 * 1024;
    private static readonly Dictionary<string, string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf", [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".txt"] = "text/plain", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg"
    };

    private readonly LifeOSDbContext _db;
    private readonly string _storageRoot;
    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public DocumentsController(LifeOSDbContext db, IWebHostEnvironment environment, IConfiguration configuration)
    {
        _db = db;
        var configuredPath = configuration["Documents:StoragePath"] ?? "App_Data/documents";
        _storageRoot = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<DocumentResponse>>> GetDocuments([FromQuery] string? q = null, [FromQuery] string? category = null)
    {
        var query = _db.Documents.AsNoTracking().Where(x => x.UserId == UserId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var search = $"%{q.Trim()}%";
            query = query.Where(x => EF.Functions.Like(x.Title, search) || EF.Functions.Like(x.OriginalFileName, search));
        }
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category.Trim());
        var items = await query.OrderBy(x => x.ExpiresAt == null).ThenBy(x => x.ExpiresAt).ThenByDescending(x => x.UploadedAt).ToListAsync();
        return items.Select(ToResponse).ToList();
    }

    [HttpPost, RequestSizeLimit(MaximumFileBytes + 128_000), RequestFormLimits(MultipartBodyLengthLimit = MaximumFileBytes + 128_000)]
    public async Task<IActionResult> Upload([FromForm] DocumentUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.File is null || request.File.Length == 0) return BadRequest(new { message = "Choose a file to upload." });
        if (request.File.Length > MaximumFileBytes) return BadRequest(new { message = "Files must be 15 MB or smaller." });
        var originalName = SafeOriginalName(request.File.FileName);
        var extension = Path.GetExtension(originalName);
        if (!AllowedTypes.TryGetValue(extension, out var mediaType))
            return BadRequest(new { message = "File type not supported. Use PDF, DOCX, XLSX, TXT, PNG, or JPG." });

        var title = string.IsNullOrWhiteSpace(request.Title) ? Path.GetFileNameWithoutExtension(originalName) : request.Title.Trim();
        if (title.Length > 160) return BadRequest(new { message = "Document title cannot exceed 160 characters." });
        var category = request.Category.Trim();
        if (category.Length is < 1 or > 40) return BadRequest(new { message = "Category must be between 1 and 40 characters." });

        var userDirectory = UserDirectory();
        Directory.CreateDirectory(userDirectory);
        var storedName = $"{Guid.NewGuid():N}{extension.ToLowerInvariant()}";
        var fullPath = Path.Combine(userDirectory, storedName);
        try
        {
            await using (var output = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
                await request.File.CopyToAsync(output, cancellationToken);

            var document = new LifeDocument
            {
                UserId = UserId, Title = title, Category = category, OriginalFileName = originalName,
                StoredFileName = storedName, MediaType = mediaType, SizeBytes = request.File.Length,
                UploadedAt = DateTime.UtcNow, ExpiresAt = request.ExpiresAt is null ? null : AsUtcDate(request.ExpiresAt.Value)
            };
            _db.Documents.Add(document);
            await _db.SaveChangesAsync(cancellationToken);
            return CreatedAtAction(nameof(Download), new { id = document.Id }, ToResponse(document));
        }
        catch
        {
            if (System.IO.File.Exists(fullPath)) System.IO.File.Delete(fullPath);
            throw;
        }
    }

    [HttpGet("{id:int}/download")]
    public async Task<IActionResult> Download(int id)
    {
        var document = await _db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (document is null) return NotFound();
        var path = Path.Combine(UserDirectory(), document.StoredFileName);
        if (!System.IO.File.Exists(path)) return NotFound(new { message = "The stored file is missing." });
        return PhysicalFile(path, document.MediaType, document.OriginalFileName, enableRangeProcessing: true);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateMetadata(int id, DocumentUpdateRequest request)
    {
        var document = await _db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (document is null) return NotFound();
        document.Title = request.Title.Trim();
        document.Category = request.Category.Trim();
        document.ExpiresAt = request.ExpiresAt is null ? null : AsUtcDate(request.ExpiresAt.Value);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var document = await _db.Documents.FirstOrDefaultAsync(x => x.Id == id && x.UserId == UserId);
        if (document is null) return NotFound();
        var path = Path.Combine(UserDirectory(), document.StoredFileName);
        _db.Documents.Remove(document);
        await _db.SaveChangesAsync();
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        return NoContent();
    }

    private string UserDirectory() => Path.Combine(_storageRoot, UserId.ToString(CultureInfo.InvariantCulture));
    private static DateTime AsUtcDate(DateTime date) => DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
    private static string SafeOriginalName(string fileName)
    {
        var baseName = Path.GetFileName(fileName.Replace('\\', '/'));
        var safe = new string(baseName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray()).Trim();
        return safe.Length > 255 ? safe[^255..] : safe;
    }
    private static DocumentResponse ToResponse(LifeDocument document) => new(document.Id, document.Title, document.Category,
        document.OriginalFileName, document.MediaType, document.SizeBytes, document.UploadedAt, document.ExpiresAt);
}

public class DocumentUploadRequest
{
    [Required] public IFormFile File { get; set; } = default!;
    [StringLength(160)] public string? Title { get; set; }
    [Required, StringLength(40, MinimumLength = 1)] public string Category { get; set; } = "personal";
    public DateTime? ExpiresAt { get; set; }
}

public record DocumentUpdateRequest([Required, StringLength(160, MinimumLength = 1)] string Title,
    [Required, StringLength(40, MinimumLength = 1)] string Category, DateTime? ExpiresAt);
public record DocumentResponse(int Id, string Title, string Category, string FileName, string MediaType,
    long SizeBytes, DateTime UploadedAt, DateTime? ExpiresAt);
