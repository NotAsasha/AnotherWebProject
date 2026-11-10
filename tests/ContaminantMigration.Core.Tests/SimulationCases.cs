using System.Buffers.Binary;
using ContaminantMigration.Core.Simulations;

namespace ContaminantMigration.Core.Tests;

internal static class SimulationCases
{
    public static SimulationParameters Demo => new(
        new DomainParameters(100, 100, 101, 101),
        new TransportParameters(0.5, 0.1, 0.1),
        new TimeParameters(60),
        new GaussianInitialCondition(1, 25, 50, 5));

    public static float ValueAt(SimulationFrame frame, int nx, int i, int j) =>
        BinaryPrimitives.ReadSingleLittleEndian(frame.Data.Span.Slice((j * nx + i) * sizeof(float), sizeof(float)));

    public static double GaussianError(SimulationResult result)
    {
        var plan = result.Plan;
        var parameters = plan.Parameters;
        var initial = parameters.InitialCondition;
        var t = parameters.Time.DurationSeconds;
        var variance = initial.Sigma * initial.Sigma + 2 * parameters.Transport.Diffusion * t;
        var peak = initial.Peak * initial.Sigma * initial.Sigma / variance;
        var centerX = initial.CenterX + parameters.Transport.VelocityX * t;
        var centerY = initial.CenterY + parameters.Transport.VelocityY * t;
        double errorSquared = 0, exactSquared = 0;

        // Independent infinite-domain Gaussian solution, with boundaries far from the plume.
        for (var j = 0; j < parameters.Domain.NodesY; j++)
        for (var i = 0; i < parameters.Domain.NodesX; i++)
        {
            var x = i * plan.Dx - centerX;
            var y = j * plan.Dy - centerY;
            var exact = peak * Math.Exp(-(x * x + y * y) / (2 * variance));
            var difference = ValueAt(result.Frames[^1], parameters.Domain.NodesX, i, j) - exact;
            errorSquared += difference * difference;
            exactSquared += exact * exact;
        }
        return Math.Sqrt(errorSquared / exactSquared);
    }
}
