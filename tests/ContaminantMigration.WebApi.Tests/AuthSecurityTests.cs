using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using ContaminantMigration.Contracts.Auth;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.WebApi.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace ContaminantMigration.WebApi.Tests;

public sealed class AuthSecurityTests
{
    private static JwtOptions Settings() => new() { SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64)) };
    private static JwtSecurityTokenHandler Handler() => new() { MapInboundClaims = false };

    [Fact]
    public void TokenContainsUserIdentityAndRoleAndCanBeValidated()
    {
        var settings = Settings();
        var user = new AppUser { Email = "test@example.com", PasswordHash = "sensitive-password-hash" };
        var response = new JwtTokenService(Options.Create(settings), TimeProvider.System).Issue(user);
        var principal = Handler().ValidateToken(response.AccessToken, settings.ValidationParameters(), out _);
        Assert.Equal(user.Id.ToString(), principal.Identity!.Name);
        Assert.True(principal.IsInRole("User"));
        Assert.False(principal.IsInRole("Admin"));
        Assert.DoesNotContain(user.PasswordHash, Handler().ReadJwtToken(response.AccessToken).Payload.SerializeToJson());
    }

    [Theory]
    [InlineData("key")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public void TokensWithUntrustedSignatureOrOriginAreRejected(string changed)
    {
        var trusted = Settings();
        var other = new JwtOptions
        {
            SigningKey = changed == "key" ? Settings().SigningKey : trusted.SigningKey,
            Issuer = changed == "issuer" ? "other" : trusted.Issuer,
            Audience = changed == "audience" ? "other" : trusted.Audience
        };
        var token = new JwtTokenService(Options.Create(other), TimeProvider.System).Issue(new AppUser());
        Assert.ThrowsAny<SecurityTokenException>(() => Handler().ValidateToken(token.AccessToken, trusted.ValidationParameters(), out _));
    }

    [Fact]
    public void ExpiredTokensAreRejected()
    {
        var settings = Settings();
        var token = new JwtTokenService(Options.Create(settings), new PastClock()).Issue(new AppUser());
        Assert.Throws<SecurityTokenExpiredException>(() => Handler().ValidateToken(token.AccessToken, settings.ValidationParameters(), out _));
    }

    [Fact]
    public void UnsignedTokensAreRejected()
    {
        var settings = Settings();
        var unsigned = Handler().WriteToken(new JwtSecurityToken(settings.Issuer, settings.Audience,
            expires: DateTime.UtcNow.AddMinutes(5)));
        Assert.ThrowsAny<SecurityTokenException>(() => Handler().ValidateToken(unsigned, settings.ValidationParameters(), out _));
    }

    [Fact]
    public void HashesAreSaltedAndWrongPasswordsFailVerification()
    {
        var passwords = new PasswordService();
        var user = new AppUser();
        user.PasswordHash = passwords.Hash(user, "correct-password-123");
        Assert.NotEqual(user.PasswordHash, passwords.Hash(user, "correct-password-123"));
        Assert.Equal(PasswordVerificationResult.Success, passwords.Verify(user, "correct-password-123"));
        Assert.Equal(PasswordVerificationResult.Failed, passwords.Verify(user, "incorrect-password-123"));
    }

    [Fact]
    public void WeakOrMalformedSigningKeysFailConfigurationValidation()
    {
        Assert.False(new JwtOptions().IsValid());
        Assert.False(new JwtOptions { SigningKey = "not base64" }.IsValid());
        Assert.False(new JwtOptions { SigningKey = Convert.ToBase64String(new byte[16]) }.IsValid());
        Assert.True(Settings().IsValid());
    }

    [Theory]
    [InlineData("not-an-email", "long-enough-password")]
    [InlineData("valid@example.com", "short")]
    [InlineData(null, null)]
    public void MalformedRegistrationIsRejected(string? email, string? password)
    {
        Assert.False(new RegisterValidator().Validate(new RegisterRequest(email!, password!)).IsValid);
    }

    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(-2);
    }
}
