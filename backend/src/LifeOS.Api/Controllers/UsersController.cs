using LifeOS.Api.Data;
using LifeOS.Api.Models;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.ComponentModel.DataAnnotations;

namespace LifeOS.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly LifeOSDbContext _context;
    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public UsersController(LifeOSDbContext context)
    {
        _context = context;
    }

    [HttpGet("me")]
    public async Task<ActionResult<User>> GetCurrentUser()
    {
        var user = await _context.Users.FindAsync(CurrentUserId);
        return user is null ? NotFound() : user;
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateCurrentUser(ProfileUpdateRequest request)
    {
        var user = await _context.Users.FindAsync(CurrentUserId);
        if (user is null) return NotFound();
        var email = request.Email.Trim().ToLowerInvariant();
        if (await _context.Users.AnyAsync(x => x.Id != CurrentUserId && x.Email.ToLower() == email))
            return Conflict(new { message = "An account with this email already exists." });
        user.Name = request.Name.Trim();
        user.Email = email;
        await _context.SaveChangesAsync();
        return Ok(new { user.Id, user.Name, user.Email, user.CreatedAt });
    }

    // GET: api/users
    [HttpGet]
    public async Task<ActionResult<IEnumerable<User>>> GetUsers()
    {
        var user = await _context.Users.FindAsync(CurrentUserId);
        return user is null ? NotFound() : new[] { user };
    }

    // GET: api/users/5
    [HttpGet("{id}")]
    public async Task<ActionResult<User>> GetUser(int id)
    {
        if (id != CurrentUserId) return NotFound();
        var user = await _context.Users.FindAsync(id);

        if (user == null)
        {
            return NotFound();
        }

        return user;
    }

    // PUT: api/users/5
[HttpPut("{id}")]
public async Task<IActionResult> UpdateUser(int id, User user)
{
    if (id != CurrentUserId) return NotFound();
    if (id != user.Id)
    {
        return BadRequest();
    }

    var existingUser = await _context.Users.FindAsync(id);

    if (existingUser == null)
    {
        return NotFound();
    }

    existingUser.Name = user.Name;
    existingUser.Email = user.Email;

    await _context.SaveChangesAsync();

    return NoContent();
}

public record ProfileUpdateRequest([Required, StringLength(100, MinimumLength = 1)] string Name,
    [Required, EmailAddress, StringLength(255)] string Email);
// DELETE: api/users/3
[HttpDelete("{id}")]
public async Task<IActionResult> DeleteUser(int id)
{
    if (id != CurrentUserId) return NotFound();
    var user = await _context.Users.FindAsync(id);

    if (user == null)
    {
        return NotFound();
    }

    _context.Users.Remove(user);
    await _context.SaveChangesAsync();

    return NoContent();
}
}
