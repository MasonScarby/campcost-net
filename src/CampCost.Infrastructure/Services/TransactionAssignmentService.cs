using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Assigns transactions to trips. See <see cref="ITransactionAssignmentService"/>.
/// The soft-delete query filter applies to all reads here, so removed transactions are
/// never assigned, surfaced, or counted toward the free-plan cap.
///
/// Free plan: a trip may hold at most <see cref="FreePlanLimits.MaxAssignedTransactionsPerTrip"/>
/// assigned transactions. The cap is enforced by refusing to assign MORE — the cap never
/// removes or unassigns existing data to make room. Pro plan is uncapped.
///
/// STRICT date-range invariant: auto-assign first releases rows outside the trip's window
/// (trip_id=null, reviewed=false — identical to trip-delete semantics), so a trip always
/// contains exactly its [StartDate, EndDate] range after create/date-change.
/// </summary>
public class TransactionAssignmentService : ITransactionAssignmentService
{
    private readonly CampCostDbContext _db;
    private readonly IPlanService _plans;
    private readonly ILogger<TransactionAssignmentService> _logger;

    public TransactionAssignmentService(
        CampCostDbContext db,
        IPlanService plans,
        ILogger<TransactionAssignmentService> logger)
    {
        _db = db;
        _plans = plans;
        _logger = logger;
    }

    public async Task<AutoAssignResult> AutoAssignForTripAsync(Guid userId, Guid tripId)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);
        if (trip is null || trip.StartDate is null || trip.EndDate is null)
            return new AutoAssignResult(0, null);

        var start = trip.StartDate.Value;
        var end = trip.EndDate.Value;
        var now = DateTime.UtcNow;

        // STRICT reconcile: a trip contains exactly its date range. Rows now OUTSIDE the
        // window are released the same way trip-delete releases them — trip_id=null AND
        // reviewed=false — so they're immediately re-claimable by any trip (nothing can be
        // stranded). This also closes the free-plan leak of stacking two date ranges onto
        // one trip by moving its dates back and forth.
        var outOfRange = await _db.Transactions
            .Where(t => t.UserId == userId
                        && t.TripId == tripId
                        && (t.TransactionDate < start || t.TransactionDate > end))
            .ToListAsync();
        foreach (var tx in outOfRange)
        {
            tx.TripId = null;
            tx.Reviewed = false; // forget the review decision, exactly like trip delete
            tx.UpdatedAt = now;
        }

        // Only claim UNASSIGNED, non-reviewed rows in range — never steal from another trip
        // and never move a user-confirmed (reviewed) assignment. Ordered oldest-first so the
        // free-plan cap deterministically keeps the earliest transactions.
        var candidates = await _db.Transactions
            .Where(t => t.UserId == userId
                        && t.TripId == null
                        && !t.Reviewed
                        && t.TransactionDate >= start
                        && t.TransactionDate <= end)
            .OrderBy(t => t.TransactionDate)
            .ThenBy(t => t.Id)
            .ToListAsync();

        var toAssign = candidates;
        string? warning = null;

        // Free-plan cap: only fill remaining slots, keeping whatever is already assigned.
        var plan = await _plans.GetPlanAsync(userId);
        if (!PlanPolicy.IsPro(plan))
        {
            // Query filter excludes soft-deleted rows, so this is the live assigned count.
            // Subtract the just-released rows (not yet saved, so the DB count still includes
            // them) — slots they occupied are free for the new window's claims.
            var alreadyAssigned = await _db.Transactions
                .CountAsync(t => t.UserId == userId && t.TripId == tripId)
                - outOfRange.Count;

            var slots = FreePlanLimits.MaxAssignedTransactionsPerTrip - alreadyAssigned;
            if (slots <= 0)
            {
                toAssign = new List<Transaction>();
                warning = FreePlanLimits.TransactionCapWarning;
            }
            else if (candidates.Count > slots)
            {
                toAssign = candidates.Take(slots).ToList();
                warning = FreePlanLimits.TransactionCapWarning;
            }
        }

        foreach (var tx in toAssign)
        {
            tx.TripId = tripId;
            tx.UpdatedAt = now;
        }

        if (toAssign.Count > 0 || outOfRange.Count > 0)
        {
            // Releases + claims persist as one unit: the trip is never saved half-reconciled.
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Reconciled trip {TripId} to range {Start}..{End}: released {Released} out-of-range, auto-assigned {Count}{Capped}",
                tripId, start, end, outOfRange.Count, toAssign.Count, warning is null ? "" : " — free-plan cap reached");
        }

        return new AutoAssignResult(toAssign.Count, warning, outOfRange.Count);
    }

    public async Task<AssignOutcome> AssignAsync(Guid userId, Guid transactionId, Guid? tripId, string? category = null)
    {
        // Validate the category BEFORE any other checks so a bad value can never
        // half-apply (case-sensitive: must match the Postgres expense_category enum).
        if (category is not null && !Enum.TryParse<ExpenseCategoryEnum>(category, ignoreCase: false, out _))
            return AssignOutcome.InvalidCategory;

        var tx = await _db.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.UserId == userId);
        if (tx is null) return AssignOutcome.NotFound;

        if (tripId is not null)
        {
            var ownsTrip = await _db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == userId);
            if (!ownsTrip) return AssignOutcome.NotFound;

            // Free-plan cap: block assigning a NEW transaction into a trip already at the
            // limit. Re-confirming a transaction already on this trip doesn't grow the count,
            // and unassigning (tripId == null) is never capped.
            if (tx.TripId != tripId)
            {
                var plan = await _plans.GetPlanAsync(userId);
                if (!PlanPolicy.IsPro(plan))
                {
                    var alreadyAssigned = await _db.Transactions
                        .CountAsync(t => t.UserId == userId && t.TripId == tripId);
                    if (alreadyAssigned >= FreePlanLimits.MaxAssignedTransactionsPerTrip)
                        return AssignOutcome.CapReached;
                }
            }
        }

        tx.TripId = tripId;
        tx.Reviewed = true; // user-confirmed — auto-assign must not move it again
        if (category is not null)
            tx.Category = category; // user correction of the auto-derived category
        tx.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return AssignOutcome.Success;
    }

    public async Task<AssignOutcome> AdjustAsync(Guid userId, Guid transactionId, decimal? adjustedAmount, string? note)
    {
        if (adjustedAmount is < 0) return AssignOutcome.InvalidAdjustment;

        var tx = await _db.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.UserId == userId);
        if (tx is null) return AssignOutcome.NotFound;

        // Overlay only — tx.Amount (the raw Plaid figure) is never touched, so the
        // original is always recoverable and the mirror stays a faithful mirror.
        tx.AdjustedAmount = adjustedAmount;
        tx.AdjustmentNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        tx.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return AssignOutcome.Success;
    }

    public async Task<AssignOutcome> SoftDeleteAsync(Guid userId, Guid transactionId)
    {
        // The query filter hides already-deleted rows, so double-deleting returns NotFound
        // (harmless and honest — the row is already gone from the user's view).
        var tx = await _db.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.UserId == userId);
        if (tx is null) return AssignOutcome.NotFound;

        tx.DeletedAt = DateTime.UtcNow;
        tx.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("User soft-deleted transaction {TransactionId}", transactionId);
        return AssignOutcome.Success;
    }

    public async Task<IReadOnlyList<Transaction>> GetUnassignedAsync(Guid userId)
    {
        return await _db.Transactions
            .Where(t => t.UserId == userId && t.TripId == null)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();
    }
}
