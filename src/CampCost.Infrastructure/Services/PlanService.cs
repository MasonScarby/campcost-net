using CampCost.Core.Entities;
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

    public async Task SetPlanAsync(Guid userId, string plan)
    {
        // Normalize to the two known plans so a bad string can't land in the DB.
        var normalized = PlanPolicy.IsPro(plan) ? PlanPolicy.ProPlan : PlanPolicy.FreePlan;

        var sub = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (sub is null)
        {
            _db.Subscriptions.Add(new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Plan = normalized,
                Status = "active",
                UpdatedAt = DateTime.UtcNow
            });
        }
        else
        {
            sub.Plan = normalized;
            sub.Status = "active";
            sub.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }
}
