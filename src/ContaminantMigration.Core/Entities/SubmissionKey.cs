namespace ContaminantMigration.Core.Entities;

public sealed class SubmissionKey
{
    public Guid OwnerId { get; set; }
    public Guid Key { get; set; }
    public Guid TaskId { get; set; }
    public string RequestHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}
