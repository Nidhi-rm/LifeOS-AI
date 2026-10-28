using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;
using LifeOS.Api.Data;
using LifeOS.Api.Models;
using LifeOS.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LifeOS.Api.Controllers;

[ApiController, Route("api/auth")]
public class AuthController(LifeOSDbContext db, ILogger<AuthController> logger, PasswordResetEmailSender emailSender) : ControllerBase
{
    [AllowAnonymous, HttpPost("register")]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email.ToLower() == email))
        {
            logger.LogWarning("Registration rejected because an account already exists");
            return Conflict(new { message = "An account with this email already exists." });
        }

        var user = new User { Name = request.Name.Trim(), Email = email, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        db.UserCredentials.Add(new UserCredential { UserId = user.Id, PasswordHash = HashPassword(request.Password) });
        await db.SaveChangesAsync();
        logger.LogInformation("User account registered with user ID {UserId}", user.Id);
        return Ok(await IssueSession(user));
    }

    [AllowAnonymous, HttpPost("login")]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var result = await (from user in db.Users
                            join credential in db.UserCredentials on user.Id equals credential.UserId
                            where user.Email.ToLower() == email
                            select new { User = user, credential.PasswordHash }).FirstOrDefaultAsync();
        if (result is null || !VerifyPassword(request.Password, result.PasswordHash))
        {
            logger.LogWarning("Login rejected because the supplied credentials were invalid");
            return Unauthorized(new { message = "Email or password is incorrect." });
        }
        logger.LogInformation("User {UserId} authenticated", result.User.Id);
        return Ok(await IssueSession(result.User));
    }

    [AllowAnonymous, EnableRateLimiting("password-reset"), HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken cancellationToken)
    {
        const string responseMessage = "If an account matches that email, a reset code will be sent shortly.";
        if (!emailSender.IsConfigured)
        {
            logger.LogWarning("Password reset requested while outbound email is not configured");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { message = "Password reset email is not configured yet. Set the SMTP settings and restart LifeOS." });
        }

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == email, cancellationToken);
        if (user is null) return Accepted(new { message = responseMessage });

        var now = DateTime.UtcNow;
        var reset = await db.PasswordResetCodes.FirstOrDefaultAsync(x => x.UserId == user.Id, cancellationToken);
        if (reset is not null && reset.CreatedAt > now.AddMinutes(-1))
            return Accepted(new { message = responseMessage });

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        if (reset is null)
        {
            reset = new PasswordResetCode { UserId = user.Id };
            db.PasswordResetCodes.Add(reset);
        }

        reset.CodeHash = HashPassword(code);
        reset.CreatedAt = now;
        reset.ExpiresAt = now.AddMinutes(15);
        reset.FailedAttempts = 0;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await emailSender.SendCodeAsync(user.Email, code, cancellationToken);
            logger.LogInformation("Password reset code sent for user {UserId}", user.Id);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Password reset email delivery failed for user {UserId}", user.Id);
            db.PasswordResetCodes.Remove(reset);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Accepted(new { message = responseMessage });
    }

    [AllowAnonymous, EnableRateLimiting("password-reset"), HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email.ToLower() == email, cancellationToken);
        var reset = user is null
            ? null
            : await db.PasswordResetCodes.FirstOrDefaultAsync(x => x.UserId == user.Id, cancellationToken);

        if (user is null || reset is null)
            return BadRequest(new { message = "That code is invalid or expired. Request a new code and try again." });

        var now = DateTime.UtcNow;
        if (reset.ExpiresAt <= now || reset.FailedAttempts >= 5)
        {
            db.PasswordResetCodes.Remove(reset);
            await db.SaveChangesAsync(cancellationToken);
            return BadRequest(new { message = "That code is invalid or expired. Request a new code and try again." });
        }

        reset.FailedAttempts++;
        if (!VerifyPassword(request.Code, reset.CodeHash))
        {
            await db.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Invalid password reset code submitted for user {UserId}", user.Id);
            return BadRequest(new { message = "That code is invalid or expired. Request a new code and try again." });
        }

        var credential = await db.UserCredentials.FirstAsync(x => x.UserId == user.Id, cancellationToken);
        credential.PasswordHash = HashPassword(request.NewPassword);
        db.PasswordResetCodes.Remove(reset);
        var sessions = await db.Sessions.Where(x => x.UserId == user.Id).ToListAsync(cancellationToken);
        db.Sessions.RemoveRange(sessions);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Password reset completed for user {UserId}; active sessions revoked", user.Id);
        return Ok(new { message = "Your password has been changed. Sign in with your new password." });
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var token = Request.Headers.Authorization.ToString()[7..].Trim();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var session = await db.Sessions.FirstOrDefaultAsync(x => x.TokenHash == hash);
        if (session is not null) { db.Sessions.Remove(session); await db.SaveChangesAsync(); }
        logger.LogInformation("User {UserId} logged out", User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
        return NoContent();
    }

    [Authorize, HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value);
        var credential = await db.UserCredentials.FirstOrDefaultAsync(x => x.UserId == userId, cancellationToken);
        if (credential is null || !VerifyPassword(request.CurrentPassword, credential.PasswordHash))
            return BadRequest(new { message = "Current password is incorrect." });

        credential.PasswordHash = HashPassword(request.NewPassword);
        var bearer = Request.Headers.Authorization.ToString()[7..].Trim();
        var currentTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));
        var otherSessions = await db.Sessions
            .Where(x => x.UserId == userId && x.TokenHash != currentTokenHash)
            .ToListAsync(cancellationToken);
        db.Sessions.RemoveRange(otherSessions);
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new { message = "Password changed. Other signed-in sessions have been signed out." });
    }

    private async Task<object> IssueSession(User user)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        db.Sessions.Add(new ApiSession { UserId = user.Id, TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))), ExpiresAt = DateTime.UtcNow.AddDays(7) });
        await db.SaveChangesAsync();
        return new { token, expiresAt = DateTime.UtcNow.AddDays(7), user = new { user.Id, user.Name, user.Email } };
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, 32);
        return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        var parts = stored.Split(':');
        if (parts.Length != 2) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

public record RegisterRequest([Required, StringLength(100, MinimumLength = 1)] string Name,
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, StringLength(128, MinimumLength = 10)] string Password);
public record LoginRequest([Required, EmailAddress] string Email, [Required] string Password);
public record ForgotPasswordRequest([Required, EmailAddress, StringLength(255)] string Email);
public record ResetPasswordRequest([Required, EmailAddress, StringLength(255)] string Email,
    [Required, RegularExpression("^[0-9]{6}$")] string Code,
    [Required, StringLength(128, MinimumLength = 10)] string NewPassword);
public record ChangePasswordRequest([Required] string CurrentPassword,
    [Required, StringLength(128, MinimumLength = 10)] string NewPassword);
