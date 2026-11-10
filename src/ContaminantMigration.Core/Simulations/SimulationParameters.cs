namespace ContaminantMigration.Core.Simulations;

public sealed record DomainParameters(double LengthX, double LengthY, int NodesX, int NodesY);

public sealed record TransportParameters(double VelocityX, double VelocityY, double Diffusion);

public sealed record TimeParameters(double DurationSeconds, double? TimeStepSeconds = null);

public sealed record GaussianInitialCondition(double Peak, double CenterX, double CenterY, double Sigma);

public enum BoundaryCondition
{
    ZeroDirichlet
}

public sealed record SimulationParameters(
    DomainParameters Domain,
    TransportParameters Transport,
    TimeParameters Time,
    GaussianInitialCondition InitialCondition,
    int RequestedFrames = 16,
    BoundaryCondition BoundaryCondition = BoundaryCondition.ZeroDirichlet);
