using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using LifeOS.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace LifeOS.Api.Services;

public class BearerTokenHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly LifeOSDbContext _db;

    public BearerTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
        UrlEncoder encoder, LifeOSDbContext db) : base(options, logger, encoder) => _db = db;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var token = header[7..].Trim();
        if (token.Length is < 32 or > 256) return AuthenticateResult.Fail("Invalid bearer token.");
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var session = await _db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenHash == tokenHash && s.ExpiresAt > DateTime.UtcNow, Context.RequestAborted);
        if (session is null) return AuthenticateResult.Fail("Invalid or expired bearer token.");

        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, session.UserId.ToString()) };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }
}
