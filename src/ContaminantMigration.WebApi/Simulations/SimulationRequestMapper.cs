using ContaminantMigration.Contracts.Simulations;
using ContaminantMigration.Core.Simulations;
using FluentValidation;

namespace ContaminantMigration.WebApi.Simulations;

public sealed class CreateSimulationValidator : AbstractValidator<CreateSimulationRequest>
{
    public CreateSimulationValidator()
    {
        RuleFor(x => x.Name).NotEmpty().Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(100);
        RuleFor(x => x.Domain).NotNull();
        RuleFor(x => x.Transport).NotNull();
        RuleFor(x => x.Time).NotNull();
        RuleFor(x => x.InitialCondition).NotNull();
        RuleFor(x => x.BoundaryCondition).IsInEnum();
    }
}

public static class SimulationRequestMapper
{
    public static SimulationParameters ToCore(this CreateSimulationRequest request) => new(
        new(request.Domain.LengthX, request.Domain.LengthY, request.Domain.NodesX, request.Domain.NodesY),
        new(request.Transport.VelocityX, request.Transport.VelocityY, request.Transport.Diffusion),
        new(request.Time.DurationSeconds, request.Time.TimeStepSeconds),
        new(request.InitialCondition.Peak, request.InitialCondition.CenterX, request.InitialCondition.CenterY, request.InitialCondition.Sigma),
        request.RequestedFrames, (BoundaryCondition)request.BoundaryCondition);
}
