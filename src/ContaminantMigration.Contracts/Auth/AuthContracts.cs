namespace ContaminantMigration.Contracts.Auth;

public sealed record RegisterRequest(string Email, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record UserResponse(Guid Id, string Email, string Role, DateTimeOffset CreatedAt);
public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserResponse User);
