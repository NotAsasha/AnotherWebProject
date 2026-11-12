using System.Security.Claims;
using ContaminantMigration.Contracts.Admin;
using ContaminantMigration.Core.Entities;
using ContaminantMigration.Infrastructure.Monitoring;
using ContaminantMigration.Infrastructure.Persistence;
using ContaminantMigration.Infrastructure.Simulations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ContaminantMigration.WebApi.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin")]
public sealed class AdminController(AppDbContext db, MonitoringStore monitoring,
    IConfiguration configuration, TimeProvider clock) : ControllerBase
{
    [HttpGet("overview")]
    public async Task<ActionResult<AdminOverview>> Overview(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue("sub"), out var id)
            || !await db.Users.AnyAsync(x => x.Id == id && x.Role == UserRole.Admin, ct)) return Forbid();
        var counts = await db.SimulationTasks.AsNoTracking().GroupBy(x => x.Status)
            .Select(group => new { Status = group.Key, Count = group.Count() }).ToListAsync(ct);
        var summary = Enum.GetValues<SimulationStatus>().ToDictionary(x => x.ToString(),
            status => counts.SingleOrDefault(x => x.Status == status)?.Count ?? 0);
        var active = await db.SimulationTasks.AsNoTracking().Where(x => SimulationStorage.Active.Contains(x.Status))
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(SimulationStorage.MaxActiveSystem)
            .Select(x => new AdminTask(x.Id, x.Name, x.Status.ToString(), x.ProgressPercent, x.CreatedAt, x.WorkerId)).ToListAsync(ct);
        var snapshot = await monitoring.Read(configuration["Monitoring:Project"] ?? "contaminantmigration");
        var now = clock.GetUtcNow();
        return Ok(new AdminOverview(now, snapshot is null || now - snapshot.CollectedAtUtc > TimeSpan.FromSeconds(15),
            snapshot, summary, active));
    }
}
