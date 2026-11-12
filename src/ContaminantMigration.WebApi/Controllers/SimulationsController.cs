using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContaminantMigration.Contracts.Simulations;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Core.Simulations;
using ContaminantMigration.Infrastructure.Persistence;
using ContaminantMigration.Infrastructure.Simulations;
using ContaminantMigration.WebApi.Simulations;
using FluentValidation;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.WebApi.Controllers;

[ApiController, Authorize, Route("api/v1/simulations")]
public sealed class SimulationsController(AppDbContext db, ISendEndpointProvider sender, IPublishEndpoint publisher,
    IValidator<CreateSimulationRequest> validator, SimulationPlanner planner) : ControllerBase
{
    private Guid OwnerId => Guid.Parse(User.FindFirstValue("sub")!);
    private IQueryable<SimulationTask> Owned => db.SimulationTasks.Where(x => x.OwnerId == OwnerId);

    [HttpGet("/api/v1/simulation-limits")]
    public IActionResult Limits() => Ok(new { numerical = planner.Limits, SimulationStorage.MaxActivePerUser,
        SimulationStorage.MaxTasksPerUser, SimulationStorage.MaxActiveSystem });

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(CreateSimulationRequest request, CancellationToken ct)
    {
        var (plan, error) = await Plan(request, ct);
        return error ?? Ok(new { plan!.Dx, plan.Dy, plan.EffectiveTimeStepSeconds, plan.TotalSteps,
            plan.EstimatedCellUpdates, frameCount = plan.FrameSteps.Count, plan.EstimatedResultBytes,
            plan.EstimatedWorkingBytes, warnings = Array.Empty<string>() });
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateSimulationRequest request, CancellationToken ct)
    {
        var (plan, error) = await Plan(request, ct);
        if (error is not null) return error;
        var keyHeader = Request.Headers["Idempotency-Key"].ToString();
        Guid? key = null;
        if (keyHeader.Length > 0)
        {
            if (!Guid.TryParse(keyHeader, out var parsed)) return Problem(statusCode: 400, title: "Idempotency-Key must be a UUID.");
            key = parsed;
        }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(request with { Name = request.Name.Trim() }, SimulationStorage.Json))));
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize admissions across API replicas; status transitions only release quota.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(71420931)", ct);
        if (!await db.Users.AnyAsync(x => x.Id == OwnerId, ct)) return Unauthorized();
        if (key.HasValue)
        {
            var previous = await db.SubmissionKeys.FindAsync([OwnerId, key.Value], ct);
            if (previous is not null && previous.ExpiresAt > DateTimeOffset.UtcNow)
            {
                if (previous.RequestHash != hash) return Problem(statusCode: 409, title: "Idempotency key belongs to a different request.");
                var existing = await Owned.AsNoTracking().SingleOrDefaultAsync(x => x.Id == previous.TaskId, ct);
                return existing is null ? Problem(statusCode: 409, title: "The original task has been deleted.") : AcceptedTask(existing);
            }
            if (previous is not null)
            {
                db.SubmissionKeys.Remove(previous);
                await db.SaveChangesAsync(ct);
            }
        }
        if (await Owned.CountAsync(ct) >= SimulationStorage.MaxTasksPerUser
            || await Owned.CountAsync(x => SimulationStorage.Active.Contains(x.Status), ct) >= SimulationStorage.MaxActivePerUser
            || await db.SimulationTasks.CountAsync(x => SimulationStorage.Active.Contains(x.Status), ct) >= SimulationStorage.MaxActiveSystem)
            return Problem(statusCode: 429, title: "Task quota exceeded.");
        var task = new SimulationTask
        {
            OwnerId = OwnerId, Name = request.Name.Trim(), TotalSteps = plan!.TotalSteps,
            ParametersJson = JsonSerializer.Serialize(request.ToCore(), SimulationStorage.Json), SolverVersion = FdmSimulationSolver.Version
        };
        db.SimulationTasks.Add(task);
        if (key.HasValue) db.SubmissionKeys.Add(new SubmissionKey { OwnerId = OwnerId, Key = key.Value,
            TaskId = task.Id, RequestHash = hash, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) });
        var endpoint = await sender.GetSendEndpoint(new Uri("queue:simulation"));
        await endpoint.Send(new RunSimulationCommand(task.Id), ct);
        await publisher.Publish(new SimulationStateChangedEvent(task.Id), ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return AcceptedTask(task);
    }

    [HttpGet]
    public async Task<IActionResult> List(int page = 1, int pageSize = 20, SimulationStatus? status = null, CancellationToken ct = default)
    {
        if (page < 1 || pageSize is < 1 or > 50 || (long)(page - 1) * pageSize > int.MaxValue)
            return Problem(statusCode: 400, title: "Invalid pagination.");
        var query = Owned.AsNoTracking();
        if (status.HasValue) query = query.Where(x => x.Status == status);
        var totalCount = await query.CountAsync(ct);
        var tasks = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return Ok(new { items = tasks.Select(View), page, pageSize, totalCount });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var task = await Owned.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return task is null ? NotFound() : Ok(View(task));
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await Owned.Where(x => x.Id == id && x.Status == SimulationStatus.Queued).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.Status, SimulationStatus.Cancelled).SetProperty(x => x.FinishedAt, now)
            .SetProperty(x => x.CancellationRequestedAt, now).SetProperty(x => x.Version, x => x.Version + 1), ct);
        await Owned.Where(x => x.Id == id && x.Status == SimulationStatus.Running).ExecuteUpdateAsync(set => set
            .SetProperty(x => x.Status, SimulationStatus.Cancelling).SetProperty(x => x.CancellationRequestedAt, now)
            .SetProperty(x => x.Version, x => x.Version + 1), ct);
        var task = await Owned.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (task is not null)
        {
            await publisher.Publish(new SimulationStateChangedEvent(id), ct);
            await db.SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return task is null ? NotFound() : task.Status == SimulationStatus.Cancelling ? Accepted(View(task)) : Ok(View(task));
    }

    [HttpGet("{id:guid}/result")]
    public async Task<IActionResult> Result(Guid id, CancellationToken ct)
    {
        var task = await Owned.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (task is null) return NotFound();
        if (task.Status != SimulationStatus.Completed) return Problem(statusCode: 409, title: "Result is not ready.");
        var metadata = await db.SimulationResults.AsNoTracking().SingleAsync(x => x.TaskId == id, ct);
        var frames = await db.SimulationFrames.AsNoTracking().Where(x => x.TaskId == id).OrderBy(x => x.Index)
            .Select(x => new { x.Index, x.Step, x.TimeSeconds, x.MinimumConcentration, x.MaximumConcentration }).ToListAsync(ct);
        return Ok(new { metadata.NodesX, metadata.NodesY, metadata.Format, frames });
    }

    [HttpGet("{id:guid}/frames/{index:int}")]
    public async Task<IActionResult> Frame(Guid id, int index, CancellationToken ct)
    {
        var task = await Owned.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (task is null) return NotFound();
        if (task.Status != SimulationStatus.Completed) return Problem(statusCode: 409, title: "Result is not ready.");
        var bytes = await db.SimulationFrames.Where(x => x.TaskId == id && x.Index == index).Select(x => x.Data).SingleOrDefaultAsync(ct);
        return bytes is null ? NotFound() : File(bytes, "application/octet-stream");
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var removed = await Owned.Where(x => x.Id == id && !SimulationStorage.Active.Contains(x.Status)).ExecuteDeleteAsync(ct);
        if (removed > 0) return NoContent();
        return await Owned.AnyAsync(x => x.Id == id, ct) ? Problem(statusCode: 409, title: "Cancel the active task first.") : NotFound();
    }

    private IActionResult AcceptedTask(SimulationTask task) => Accepted($"/api/v1/simulations/{task.Id}",
        new { task.Id, status = task.Status.ToString(), statusUrl = $"/api/v1/simulations/{task.Id}" });
    private static object View(SimulationTask task) => new
    {
        task.Id, task.Name, status = task.Status.ToString(), task.ProgressPercent, task.CompletedSteps, task.TotalSteps,
        task.CreatedAt, task.StartedAt, task.FinishedAt, task.CancellationRequestedAt, task.WorkerId, task.Attempt,
        task.Version, task.ErrorCode, parameters = JsonSerializer.Deserialize<JsonElement>(task.ParametersJson), estimatedWaitSeconds = (double?)null
    };
    private async Task<(SimulationPlan? Plan, IActionResult? Error)> Plan(CreateSimulationRequest request, CancellationToken ct)
    {
        var validation = await validator.ValidateAsync(request, ct);
        if (!validation.IsValid) return (null, ValidationProblem(new ValidationProblemDetails(validation.ToDictionary())));
        try { return (planner.CreatePlan(request.ToCore()), null); }
        catch (SimulationValidationException error)
        {
            return (null, ValidationProblem(new ValidationProblemDetails(error.Errors.GroupBy(x => x.Field)
                .ToDictionary(x => x.Key, x => x.Select(e => e.Message).ToArray()))));
        }
    }
}
