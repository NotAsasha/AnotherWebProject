using System.Text.Json.Serialization;

namespace ContaminantMigration.Contracts.Simulations;

public sealed record DomainRequest([property: JsonRequired] double LengthX, [property: JsonRequired] double LengthY,
    [property: JsonRequired] int NodesX, [property: JsonRequired] int NodesY);
public sealed record TransportRequest([property: JsonRequired] double VelocityX, [property: JsonRequired] double VelocityY,
    [property: JsonRequired] double Diffusion);
public sealed record TimeRequest([property: JsonRequired] double DurationSeconds, double? TimeStepSeconds = null);
public sealed record InitialConditionRequest([property: JsonRequired] double Peak, [property: JsonRequired] double CenterX,
    [property: JsonRequired] double CenterY, [property: JsonRequired] double Sigma);
public enum BoundaryConditionRequest { ZeroDirichlet }
public sealed record CreateSimulationRequest([property: JsonRequired] string Name, [property: JsonRequired] DomainRequest Domain,
    [property: JsonRequired] TransportRequest Transport, [property: JsonRequired] TimeRequest Time,
    [property: JsonRequired] InitialConditionRequest InitialCondition, [property: JsonRequired] int RequestedFrames = 16,
    [property: JsonRequired] BoundaryConditionRequest BoundaryCondition = BoundaryConditionRequest.ZeroDirichlet);

public sealed record RunSimulationCommand(Guid TaskId);
public sealed record SimulationStateChangedEvent(Guid TaskId);
public sealed record SimulationUpdated(Guid TaskId, long Version, string Status, double ProgressPercent,
    int CompletedSteps, int TotalSteps, int Attempt, string? ErrorCode);
public sealed record SimulationProgressEvent(Guid TaskId, long Version, int Attempt, int CompletedSteps,
    int TotalSteps, double ProgressPercent, DateTimeOffset OccurredAtUtc);
public sealed record SimulationFinishedEvent(Guid TaskId, long Version, int Attempt, string Status,
    string? ErrorCode, DateTimeOffset OccurredAtUtc);
