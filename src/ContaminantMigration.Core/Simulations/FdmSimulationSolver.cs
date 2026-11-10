using System.Buffers.Binary;

namespace ContaminantMigration.Core.Simulations;

public sealed class FdmSimulationSolver : ISimulationSolver
{
    public const string Version = "fdm-upwind-v1";
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(500);
    private readonly SimulationPlanner planner;
    private readonly TimeProvider clock;

    public FdmSimulationSolver(SimulationPlanner? planner = null, TimeProvider? timeProvider = null)
    {
        this.planner = planner ?? new SimulationPlanner();
        clock = timeProvider ?? TimeProvider.System;
    }

    public SimulationResult Solve(SimulationParameters parameters,
        Action<SimulationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = planner.CreatePlan(parameters);
        var guard = new ExecutionGuard(clock, planner.Limits.MaxExecutionTime, cancellationToken);
        guard.Check();

        var nx = parameters.Domain.NodesX;
        var ny = parameters.Domain.NodesY;
        var cellCount = checked(nx * ny);
        var current = new double[cellCount];
        var next = new double[cellCount];
        var frames = new SimulationFrame[plan.FrameSteps.Count];
        var initial = parameters.InitialCondition;

        for (var j = 1; j < ny - 1; j++)
        {
            if ((j & 31) == 1) guard.Check();
            var relativeY = (j * plan.Dy - initial.CenterY) / initial.Sigma;
            for (var i = 1; i < nx - 1; i++)
            {
                var relativeX = (i * plan.Dx - initial.CenterX) / initial.Sigma;
                current[j * nx + i] = initial.Peak * Math.Exp(-0.5 * (relativeX * relativeX + relativeY * relativeY));
            }
        }

        frames[0] = CaptureFrame(current, plan, 0, guard);
        progress?.Invoke(new SimulationProgress(0, plan.TotalSteps, 0));
        var lastProgress = clock.GetTimestamp();
        var frameIndex = 1;

        var transport = parameters.Transport;
        var dt = plan.EffectiveTimeStepSeconds;
        var advX = dt * (Math.Abs(transport.VelocityX) / plan.Dx);
        var advY = dt * (Math.Abs(transport.VelocityY) / plan.Dy);
        var diffX = dt * (transport.Diffusion / plan.Dx / plan.Dx);
        var diffY = dt * (transport.Diffusion / plan.Dy / plan.Dy);
        var west = diffX + (transport.VelocityX >= 0 ? advX : 0);
        var east = diffX + (transport.VelocityX < 0 ? advX : 0);
        var south = diffY + (transport.VelocityY >= 0 ? advY : 0);
        var north = diffY + (transport.VelocityY < 0 ? advY : 0);
        var center = 1 - plan.StabilityNumber;
        var negativeTolerance = 1e-12 * Math.Max(1, initial.Peak);

        for (var step = 1; step <= plan.TotalSteps; step++)
        {
            guard.Check();
            for (var j = 1; j < ny - 1; j++)
            {
                if ((j & 31) == 1) guard.Check();
                var row = j * nx;
                for (var i = 1; i < nx - 1; i++)
                {
                    var index = row + i;
                    // Nonnegative stencil weights: Euler + first-order Upwind + central diffusion.
                    var value = center * current[index]
                        + west * current[index - 1] + east * current[index + 1]
                        + south * current[index - nx] + north * current[index + nx];
                    if (!double.IsFinite(value) || value < -negativeTolerance)
                        throw new SimulationNumericalException(step, index, value);
                    next[index] = value;
                }
            }

            // Boundary nodes in both buffers remain zero throughout the run.
            (current, next) = (next, current);
            if (frameIndex < frames.Length && step == plan.FrameSteps[frameIndex])
                frames[frameIndex++] = CaptureFrame(current, plan, step, guard);

            if (progress is not null)
            {
                var now = clock.GetTimestamp();
                if (step == plan.TotalSteps || clock.GetElapsedTime(lastProgress, now) >= ProgressInterval)
                {
                    var simulationTime = step == plan.TotalSteps ? parameters.Time.DurationSeconds : step * dt;
                    progress(new SimulationProgress(step, plan.TotalSteps, simulationTime));
                    lastProgress = now;
                }
            }
        }

        guard.Check();
        return new SimulationResult(plan, frames, guard.Elapsed, Version);
    }

    private static SimulationFrame CaptureFrame(double[] field, SimulationPlan plan, int step, ExecutionGuard guard)
    {
        var data = new byte[checked(field.Length * sizeof(float))];
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        var nx = plan.Parameters.Domain.NodesX;
        for (var j = 0; j < plan.Parameters.Domain.NodesY; j++)
        {
            if ((j & 31) == 0) guard.Check();
            for (var i = 0; i < nx; i++)
            {
                var index = j * nx + i;
                var value = (float)field[index];
                BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(index * sizeof(float), sizeof(float)), value);
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }
        var time = step == plan.TotalSteps ? plan.Parameters.Time.DurationSeconds : step * plan.EffectiveTimeStepSeconds;
        return new SimulationFrame(step, time, data, minimum, maximum);
    }

    private sealed class ExecutionGuard(TimeProvider clock, TimeSpan limit, CancellationToken cancellationToken)
    {
        private readonly long started = clock.GetTimestamp();
        public TimeSpan Elapsed => clock.GetElapsedTime(started, clock.GetTimestamp());

        public void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Elapsed >= limit)
                throw new SimulationTimeoutException(limit);
        }
    }
}
