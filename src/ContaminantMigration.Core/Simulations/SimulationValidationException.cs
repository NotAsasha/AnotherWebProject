namespace ContaminantMigration.Core.Simulations;

public sealed record SimulationValidationError(string Field, string Code, string Message);

public sealed class SimulationValidationException(IEnumerable<SimulationValidationError> errors)
    : ArgumentException("Simulation parameters are invalid.")
{
    public IReadOnlyList<SimulationValidationError> Errors { get; } = Array.AsReadOnly(errors.ToArray());
}
