using Microsoft.IdentityModel.Tokens;

namespace ContaminantMigration.WebApi.Auth;

public sealed class JwtOptions
{
    public string Issuer { get; init; } = "ContaminantMigration";
    public string Audience { get; init; } = "ContaminantMigration.Client";
    public string SigningKey { get; init; } = "";
    public int LifetimeMinutes { get; init; } = 30;

    public bool IsValid()
    {
        try
        {
            return Convert.FromBase64String(SigningKey).Length >= 32
                && !string.IsNullOrWhiteSpace(Issuer) && !string.IsNullOrWhiteSpace(Audience)
                && LifetimeMinutes is >= 1 and <= 60;
        }
        catch (FormatException) { return false; }
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true, ValidIssuer = Issuer,
        ValidateAudience = true, ValidAudience = Audience,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(Convert.FromBase64String(SigningKey)),
        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub", RoleClaimType = "role"
    };
}
