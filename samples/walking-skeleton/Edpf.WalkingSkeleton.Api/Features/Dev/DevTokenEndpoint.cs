using System.Security.Claims;
using Edpf.WalkingSkeleton.Api.Infrastructure.Auth;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace Edpf.WalkingSkeleton.Api.Features.Dev;

/// <summary>
/// Development-only JWT minting for the gate demonstration and local
/// exploration. Mapped exclusively when the environment is Development —
/// production composition never registers this endpoint.
/// </summary>
public static class DevTokenEndpoint
{
    /// <summary>Maps <c>/dev/token</c> (Development only).</summary>
    public static IEndpointRouteBuilder MapDevTokenEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapGet("/dev/token", (
            IOptions<JwtOptions> jwtOptions,
            Edpf.Abstractions.Primitives.IClock clock,
            string roles = "clinician",
            string subject = "dev-user") =>
        {
            JwtOptions options = jwtOptions.Value;
            var handler = new JsonWebTokenHandler();

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, subject) };
            claims.AddRange(
                roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                     .Select(role => new Claim(ClaimTypes.Role, role)));

            string token = handler.CreateToken(new SecurityTokenDescriptor
            {
                Issuer = options.Issuer,
                Audience = options.Audience,
                Subject = new ClaimsIdentity(claims),
                Expires = clock.UtcNow.UtcDateTime.AddHours(8),
                SigningCredentials = new SigningCredentials(
                    new SymmetricSecurityKey(options.ResolveSigningKey()),
                    SecurityAlgorithms.HmacSha256),
            });

            return Results.Ok(new { token });
        });

        return app;
    }
}
