using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ContaminantMigration.Contracts.Auth;
using ContaminantMigration.Core.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ContaminantMigration.WebApi.Auth;

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    public LoginResponse Issue(AppUser user)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var expires = now.AddMinutes(settings.LifetimeMinutes);
        var token = new JwtSecurityToken(settings.Issuer, settings.Audience,
        [
            new Claim("sub", user.Id.ToString()), new Claim("role", user.Role.ToString()),
            new Claim("jti", Guid.NewGuid().ToString())
        ], now.UtcDateTime, expires.UtcDateTime,
        new SigningCredentials(new SymmetricSecurityKey(Convert.FromBase64String(settings.SigningKey)), SecurityAlgorithms.HmacSha256));
        return new(new JwtSecurityTokenHandler().WriteToken(token), expires,
            new(user.Id, user.Email, user.Role.ToString(), user.CreatedAt));
    }
}
