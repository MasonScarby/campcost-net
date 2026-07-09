using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Reads the user's plan from the `subscriptions` table.
/// Server-side source of truth — the client's localStorage plan is display-only
/// and must never be trusted for enforcement.
/// </summary>
public class PlanService : IPlanService
{
    private readonly CampCostDbContext _db;

    public PlanService(CampCostDbContext db) => _db = db;

    public async Task<string> GetPlanAsync(Guid userId)
    {
        var sub = await _db.Subscriptions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);

        // No row → free. Non-active subscription (cancelled / past_due) → free.
        if (sub is null || !string.Equals(sub.Status, "active", StringComparison.OrdinalIgnoreCase))
            return PlanPolicy.FreePlan;

        return PlanPolicy.IsPro(sub.Plan) ? PlanPolicy.ProPlan : PlanPolicy.FreePlan;
    }
}
