using System.Security.Claims;
using ContaminantMigration.Contracts.Auth;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Infrastructure.Persistence;
using ContaminantMigration.WebApi.Auth;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ContaminantMigration.WebApi.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(AppDbContext db, PasswordService passwords, JwtTokenService tokens) : ControllerBase
{
    [AllowAnonymous, HttpPost("register"), EnableRateLimiting("auth")]
    public async Task<ActionResult<UserResponse>> Register(RegisterRequest request,
        [FromServices] IValidator<RegisterRequest> validator, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid) return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var email = request.Email.Trim();
        var user = new AppUser { Email = email, NormalizedEmail = email.ToUpperInvariant() };
        user.PasswordHash = passwords.Hash(user, request.Password);
        db.Users.Add(user);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Problem(statusCode: 409, title: "Email is already registered.", extensions: new Dictionary<string, object?> { ["code"] = "EmailAlreadyRegistered" });
        }
        return Created("/api/v1/auth/me", ToResponse(user));
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("auth")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request,
        [FromServices] IValidator<LoginRequest> validator, CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid) return ValidationProblem(new ValidationProblemDetails(validation.ToDictionary()));
        var email = request.Email.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(x => x.NormalizedEmail == email, cancellationToken);
        if (user is null)
        {
            passwords.VerifyMissingUser(request.Password);
            return InvalidCredentials();
        }
        var verified = passwords.Verify(user, request.Password);
        if (verified == PasswordVerificationResult.Failed) return InvalidCredentials();
        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.Hash(user, request.Password);
            await db.SaveChangesAsync(cancellationToken);
        }
        Response.Headers.CacheControl = "no-store";
        return Ok(tokens.Issue(user));
    }

    [Authorize, HttpGet("me")]
    public async Task<ActionResult<UserResponse>> Me(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var id)) return Unauthorized();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return user is null ? Unauthorized() : Ok(ToResponse(user));
    }

    private ObjectResult InvalidCredentials() => Problem(statusCode: 401, title: "Invalid email or password.",
        extensions: new Dictionary<string, object?> { ["code"] = "InvalidCredentials" });
    private static UserResponse ToResponse(AppUser user) => new(user.Id, user.Email, user.Role.ToString(), user.CreatedAt);
}
