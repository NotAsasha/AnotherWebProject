using ContaminantMigration.Core.Simulations;

namespace ContaminantMigration.Core.Tests;

public sealed class SimulationPlannerTests
{
    private readonly SimulationPlanner planner = new();

    [Fact]
    public void DemoHasExpectedStablePlanAndMemoryEstimate()
    {
        var plan = planner.CreatePlan(SimulationCases.Demo);
        Assert.Equal(1, plan.Dx);
        Assert.Equal(1, plan.Dy);
        Assert.Equal(67, plan.TotalSteps);
        Assert.Equal(60.0 / 67, plan.EffectiveTimeStepSeconds);
        Assert.InRange(plan.StabilityNumber, 0, 0.9);
        Assert.Equal(683_467, plan.EstimatedCellUpdates);
        Assert.Equal(652_864, plan.EstimatedResultBytes);
        Assert.Equal(816_080, plan.EstimatedWorkingBytes);
        Assert.Equal(0, plan.FrameSteps[0]);
        Assert.Equal(67, plan.FrameSteps[^1]);
        Assert.Equal(16, plan.FrameSteps.Distinct().Count());
    }

    [Fact]
    public void StationaryFieldNeedsOnlyOneAutomaticStep()
    {
        var plan = planner.CreatePlan(SimulationCases.Demo with { Transport = new(0, 0, 0) });
        Assert.Equal(1, plan.TotalSteps);
        Assert.Equal(60, plan.EffectiveTimeStepSeconds);
        Assert.Equal(new[] { 0, 1 }, plan.FrameSteps);
    }

    [Fact]
    public void ExplicitStepIsNormalizedToReachTheExactEndTime()
    {
        var plan = planner.CreatePlan(SimulationCases.Demo with { Time = new(1, 0.3) });
        Assert.Equal(4, plan.TotalSteps);
        Assert.Equal(0.25, plan.EffectiveTimeStepSeconds);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, plan.FrameSteps);
    }

    [Fact]
    public void CombinedStabilityIsCheckedEvenWhenIndividualTermsAreBelowOne()
    {
        // Advective number 0.66 and diffusive number 0.44: their sum is unstable.
        var error = Assert.Throws<SimulationValidationException>(() =>
            planner.CreatePlan(SimulationCases.Demo with { Time = new(0.1, 1.1) }));
        Assert.Contains(error.Errors, x => x.Code == "UnstableTimeStep" && x.Field == "time.timeStepSeconds");
    }

    [Fact]
    public void ExactStabilityBoundaryIsAccepted()
    {
        var plan = planner.CreatePlan(SimulationCases.Demo with { Time = new(2, 1) });
        Assert.Equal(1, plan.StabilityNumber);
        Assert.Equal(2, plan.TotalSteps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InvalidDurationIsRejected(double duration)
    {
        var error = Assert.Throws<SimulationValidationException>(() =>
            planner.CreatePlan(SimulationCases.Demo with { Time = new(duration) }));
        Assert.Contains(error.Errors, x => x.Field == "time.durationSeconds");
    }

    [Theory]
    [InlineData("grid")]
    [InlineData("velocity")]
    [InlineData("diffusion")]
    [InlineData("peak")]
    [InlineData("center")]
    [InlineData("sigma")]
    [InlineData("frames")]
    [InlineData("boundary")]
    [InlineData("missing")]
    public void InvalidPhysicalParametersAreRejected(string field)
    {
        var demo = SimulationCases.Demo;
        var parameters = field switch
        {
            "grid" => demo with { Domain = demo.Domain with { NodesX = 513 } },
            "velocity" => demo with { Transport = demo.Transport with { VelocityY = double.NaN } },
            "diffusion" => demo with { Transport = demo.Transport with { Diffusion = -0.1 } },
            "peak" => demo with { InitialCondition = demo.InitialCondition with { Peak = double.PositiveInfinity } },
            "center" => demo with { InitialCondition = demo.InitialCondition with { CenterX = 0 } },
            "sigma" => demo with { InitialCondition = demo.InitialCondition with { Sigma = 0.5 } },
            "frames" => demo with { RequestedFrames = 33 },
            "boundary" => demo with { BoundaryCondition = (BoundaryCondition)99 },
            "missing" => demo with { Domain = null! },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
        Assert.Throws<SimulationValidationException>(() => planner.CreatePlan(parameters));
    }

    [Theory]
    [InlineData("steps", "StepLimitExceeded")]
    [InlineData("work", "WorkLimitExceeded")]
    [InlineData("result", "ResultLimitExceeded")]
    [InlineData("memory", "MemoryLimitExceeded")]
    public void ConfiguredBudgetsAreEnforced(string budget, string code)
    {
        var limits = budget switch
        {
            "steps" => SimulationLimits.Default with { MaxTimeSteps = 1 },
            "work" => SimulationLimits.Default with { MaxCellUpdates = 1 },
            "result" => SimulationLimits.Default with { MaxResultBytes = 1 },
            "memory" => SimulationLimits.Default with { MaxWorkingBytes = 1 },
            _ => throw new ArgumentOutOfRangeException(nameof(budget))
        };
        var error = Assert.Throws<SimulationValidationException>(() =>
            new SimulationPlanner(limits).CreatePlan(SimulationCases.Demo));
        Assert.Contains(error.Errors, x => x.Code == code);
    }

    [Fact]
    public void ExtremelySmallExplicitStepIsRejectedWithoutIntegerOverflow()
    {
        var error = Assert.Throws<SimulationValidationException>(() =>
            planner.CreatePlan(SimulationCases.Demo with { Time = new(60, double.Epsilon) }));
        Assert.Contains(error.Errors, x => x.Code == "StepLimitExceeded");
    }

    [Fact]
    public void UnrepresentableStabilityRateIsRejected()
    {
        var parameters = SimulationCases.Demo with
        {
            Domain = new(1e-200, 1e-200, 101, 101),
            InitialCondition = new(1, 5e-201, 5e-201, 1e-201)
        };
        var error = Assert.Throws<SimulationValidationException>(() => planner.CreatePlan(parameters));
        Assert.Contains(error.Errors, x => x.Code == "NumericRange");
    }

    [Fact]
    public void MemoryEstimateOverflowIsRejectedEvenWithRelaxedLimits()
    {
        var limits = SimulationLimits.Default with
        {
            MaxNodesPerAxis = int.MaxValue, MaxCellUpdates = long.MaxValue,
            MaxResultBytes = long.MaxValue, MaxWorkingBytes = long.MaxValue
        };
        var parameters = SimulationCases.Demo with
        {
            Domain = new(100, 100, int.MaxValue, int.MaxValue),
            Transport = new(0, 0, 0)
        };
        var error = Assert.Throws<SimulationValidationException>(() => new SimulationPlanner(limits).CreatePlan(parameters));
        Assert.Contains(error.Errors, x => x.Code == "NumericRange");
    }

    [Fact]
    public void InvalidConfigurationCannotDisableLimitsAccidentally()
    {
        Assert.Throws<ArgumentException>(() => new SimulationPlanner(SimulationLimits.Default with { MaxTimeSteps = 0 }));
        Assert.Throws<ArgumentException>(() => new SimulationPlanner(SimulationLimits.Default with { MaxPeak = double.MaxValue }));
    }

    [Fact]
    public void GridSpacingUnderflowIsRejectedBeforeDividingByZero()
    {
        var parameters = SimulationCases.Demo with
        {
            Domain = new(double.Epsilon * 4, double.Epsilon * 4, 512, 512),
            InitialCondition = new(1, double.Epsilon * 2, double.Epsilon * 2, double.Epsilon)
        };
        var error = Assert.Throws<SimulationValidationException>(() => planner.CreatePlan(parameters));
        Assert.Contains(error.Errors, x => x.Code == "NumericRange");
    }
}
