using ContaminantMigration.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace ContaminantMigration.WebApi.Auth;

public sealed class PasswordService
{
    private readonly PasswordHasher<AppUser> hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = 210_000 }));
    private readonly AppUser dummyUser = new();
    private readonly string dummyHash;

    public PasswordService() => dummyHash = hasher.HashPassword(dummyUser, Guid.NewGuid().ToString());
    public string Hash(AppUser user, string password) => hasher.HashPassword(user, password);
    public PasswordVerificationResult Verify(AppUser user, string password) => hasher.VerifyHashedPassword(user, user.PasswordHash, password);
    public void VerifyMissingUser(string password) => hasher.VerifyHashedPassword(dummyUser, dummyHash, password);
}
