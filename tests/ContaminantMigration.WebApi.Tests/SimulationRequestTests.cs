using ContaminantMigration.Contracts.Simulations;
using ContaminantMigration.Core.Simulations;
using ContaminantMigration.WebApi.Simulations;
using System.Text.Json;

namespace ContaminantMigration.WebApi.Tests;

public sealed class SimulationRequestTests
{
    private static CreateSimulationRequest Demo => new("Demo", new(100,100,101,101),
        new(0.5,0.1,0.1), new(60), new(1,25,50,5));

    [Fact]
    public void WireRequestMapsToTheExpectedNumericalPlan()
    {
        var request = Demo;
        Assert.True(new CreateSimulationValidator().Validate(request).IsValid);
        var plan = new SimulationPlanner().CreatePlan(request.ToCore());
        Assert.Equal(67, plan.TotalSteps);
        Assert.Equal(16, plan.FrameSteps.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTaskNamesAreRejected(string name) =>
        Assert.False(new CreateSimulationValidator().Validate(Demo with { Name = name }).IsValid);

    [Fact]
    public void MissingNestedParametersAreRejectedBeforeMapping() =>
        Assert.False(new CreateSimulationValidator().Validate(Demo with { Domain = null! }).IsValid);

    [Fact]
    public void MissingPhysicalFieldsCannotSilentlyBecomeZero() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TransportRequest>("{}"));
}
