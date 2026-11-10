namespace ContaminantMigration.Core.Simulations;

public sealed class SimulationPlanner
{
    public SimulationPlanner(SimulationLimits? limits = null)
    {
        Limits = limits ?? SimulationLimits.Default;
        Limits.Validate();
    }

    public SimulationLimits Limits { get; }

    public SimulationPlan CreatePlan(SimulationParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var errors = new List<SimulationValidationError>();
        var domain = parameters.Domain;
        var transport = parameters.Transport;
        var time = parameters.Time;
        var initial = parameters.InitialCondition;

        void Error(string field, string code, string message) => errors.Add(new(field, code, message));
        void Range(double value, double min, double max, string field, bool excludeMin = false)
        {
            if (!double.IsFinite(value) || value < min || value > max || (excludeMin && value == min))
                Error(field, "OutOfRange", $"Expected a finite value {(excludeMin ? ">" : ">=")} {min} and <= {max}.");
        }
        void ThrowIfInvalid()
        {
            if (errors.Count > 0)
                throw new SimulationValidationException(errors);
        }

        if (domain is null) Error("domain", "Required", "Domain is required.");
        if (transport is null) Error("transport", "Required", "Transport parameters are required.");
        if (time is null) Error("time", "Required", "Time parameters are required.");
        if (initial is null) Error("initialCondition", "Required", "Initial condition is required.");
        ThrowIfInvalid();

        // Null checks above produce field errors; no derived arithmetic happens before scalar validation.
        Range(domain!.LengthX, 0, Limits.MaxDomainLength, "domain.lengthX", excludeMin: true);
        Range(domain.LengthY, 0, Limits.MaxDomainLength, "domain.lengthY", excludeMin: true);
        Range(domain.NodesX, 3, Limits.MaxNodesPerAxis, "domain.nodesX");
        Range(domain.NodesY, 3, Limits.MaxNodesPerAxis, "domain.nodesY");
        Range(transport!.VelocityX, -Limits.MaxAbsoluteVelocity, Limits.MaxAbsoluteVelocity, "transport.velocityX");
        Range(transport.VelocityY, -Limits.MaxAbsoluteVelocity, Limits.MaxAbsoluteVelocity, "transport.velocityY");
        Range(transport.Diffusion, 0, Limits.MaxDiffusion, "transport.diffusion");
        Range(time!.DurationSeconds, 0, Limits.MaxDurationSeconds, "time.durationSeconds", excludeMin: true);
        if (time.TimeStepSeconds is { } requestedStep)
            Range(requestedStep, 0, double.MaxValue, "time.timeStepSeconds", excludeMin: true);
        Range(initial!.Peak, 0, Limits.MaxPeak, "initialCondition.peak");
        if (!double.IsFinite(initial.CenterX) || initial.CenterX <= 0 || initial.CenterX >= domain.LengthX)
            Error("initialCondition.centerX", "OutOfRange", "Center must be strictly inside the domain.");
        if (!double.IsFinite(initial.CenterY) || initial.CenterY <= 0 || initial.CenterY >= domain.LengthY)
            Error("initialCondition.centerY", "OutOfRange", "Center must be strictly inside the domain.");
        Range(initial.Sigma, 0, double.MaxValue, "initialCondition.sigma", excludeMin: true);
        Range(parameters.RequestedFrames, 2, Limits.MaxFrames, "requestedFrames");
        if (parameters.BoundaryCondition != BoundaryCondition.ZeroDirichlet)
            Error("boundaryCondition", "Unsupported", "Only ZeroDirichlet is supported.");
        ThrowIfInvalid();

        var dx = domain.LengthX / (domain.NodesX - 1);
        var dy = domain.LengthY / (domain.NodesY - 1);
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || dx <= 0 || dy <= 0)
            Error("domain", "NumericRange", "Grid spacing cannot be represented as a positive finite number.");
        if (initial.Sigma < Math.Max(dx, dy) || initial.Sigma > Math.Min(domain.LengthX, domain.LengthY) / 2)
            Error("initialCondition.sigma", "OutOfRange", "Sigma must be between max(dx,dy) and min(lengthX,lengthY)/2.");
        ThrowIfInvalid();

        // Positivity condition for the complete unsplit 2D explicit update.
        var rate = Math.Abs(transport.VelocityX) / dx + Math.Abs(transport.VelocityY) / dy
            + 2 * (transport.Diffusion / dx / dx + transport.Diffusion / dy / dy);
        if (!double.IsFinite(rate))
            Error("domain", "NumericRange", "Stability coefficients exceed the supported numeric range.");
        ThrowIfInvalid();

        double stepCount;
        if (time.TimeStepSeconds is { } dt)
        {
            if (!double.IsFinite(dt * rate) || dt * rate > 1)
                Error("time.timeStepSeconds", "UnstableTimeStep", "The combined advection-diffusion stability number must be <= 1.");
            stepCount = Math.Max(1, Math.Ceiling(time.DurationSeconds / dt));
        }
        else
        {
            stepCount = rate == 0 ? 1 : Math.Max(1, Math.Ceiling(time.DurationSeconds * rate / 0.9));
        }
        if (!double.IsFinite(stepCount) || stepCount > Limits.MaxTimeSteps)
            Error("time", "StepLimitExceeded", $"Simulation requires more than {Limits.MaxTimeSteps} time steps.");
        ThrowIfInvalid();

        var totalSteps = (int)stepCount;
        var effectiveStep = time.DurationSeconds / totalSteps;
        // Division near an exact CFL boundary can round upward by one ULP.
        if (effectiveStep * rate > 1)
        {
            totalSteps++;
            effectiveStep = time.DurationSeconds / totalSteps;
        }
        if (totalSteps > Limits.MaxTimeSteps)
            Error("time", "StepLimitExceeded", "The stable time-step count exceeds the limit.");
        if (!double.IsFinite(effectiveStep) || effectiveStep <= 0 || effectiveStep * rate > 1)
            Error("time", "NumericRange", "A positive stable time step cannot be represented.");
        ThrowIfInvalid();

        var cellCount = (long)domain.NodesX * domain.NodesY;
        var frameCount = Math.Min(parameters.RequestedFrames, totalSteps + 1);
        long updates = 0, resultBytes = 0, workingBytes = 0;
        try
        {
            updates = checked(cellCount * totalSteps);
            resultBytes = checked(cellCount * frameCount * sizeof(float));
            workingBytes = checked(cellCount * 2 * sizeof(double) + resultBytes);
        }
        catch (OverflowException)
        {
            Error("domain", "NumericRange", "The work or memory estimate overflowed.");
        }
        if (updates > Limits.MaxCellUpdates)
            Error("domain", "WorkLimitExceeded", $"Simulation exceeds {Limits.MaxCellUpdates} estimated cell updates.");
        if (resultBytes > Limits.MaxResultBytes)
            Error("requestedFrames", "ResultLimitExceeded", $"Frames exceed {Limits.MaxResultBytes} bytes.");
        if (workingBytes > Limits.MaxWorkingBytes || cellCount > Array.MaxLength / sizeof(float))
            Error("domain", "MemoryLimitExceeded", "Grid buffers and frames exceed the supported memory budget.");
        ThrowIfInvalid();

        var frameSteps = new int[frameCount];
        for (var k = 0; k < frameCount; k++)
            frameSteps[k] = (int)((long)k * totalSteps / (frameCount - 1));

        return new SimulationPlan(parameters, dx, dy, effectiveStep, rate, totalSteps,
            updates, resultBytes, workingBytes, frameSteps);
    }
}
