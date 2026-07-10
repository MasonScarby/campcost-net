using CampCost.Core.Entities;

namespace CampCost.Core.Interfaces;

/// <summary>
/// Assigns raw Plaid transactions to trips. Assignment is decoupled from ingestion:
/// transactions are stored first (trip_id NULL) and claimed later, either automatically
/// when a trip's date range covers them or manually by the user.
///
/// Free-plan trips are capped at <see cref="Services.FreePlanLimits.MaxAssignedTransactionsPerTrip"/>
/// ASSIGNED transactions. The cap is enforced here (auto-assign and manual assign) — never
/// by deleting or unassigning existing data to make room. Pro plans are uncapped.
/// </summary>
public interface ITransactionAssignmentService
{
    /// <summary>
    /// Auto-assign the user's UNASSIGNED, non-reviewed transactions whose transaction_date
    /// falls in [trip.StartDate, trip.EndDate] to the given trip. Never touches transactions
    /// already assigned to another trip, and never moves a reviewed (user-confirmed) row.
    /// On a free plan, assigns only up to the cap (oldest first) and returns a warning if the
    /// cap truncated the batch. No-op when the trip has no start/end dates.
    /// Called when a trip is created or its dates change.
    /// </summary>
    Task<AutoAssignResult> AutoAssignForTripAsync(Guid userId, Guid tripId);

    /// <summary>
    /// Manual override: set a transaction's trip (or null to unassign) and mark it reviewed
    /// so auto-assign won't move it again. Returns <see cref="AssignOutcome.NotFound"/> if the
    /// transaction (or target trip) isn't owned by the user, <see cref="AssignOutcome.CapReached"/>
    /// when a free-plan trip is already at the assignment cap, otherwise
    /// <see cref="AssignOutcome.Success"/>. Passing a null tripId unassigns (never capped).
    /// An optional category corrects the auto-derived one during review — it's OUR derived
    /// field, not immutable Plaid data (amount/date/merchant stay untouchable). An unknown
    /// category returns <see cref="AssignOutcome.InvalidCategory"/> and changes nothing.
    /// </summary>
    Task<AssignOutcome> AssignAsync(Guid userId, Guid transactionId, Guid? tripId, string? category = null);

    /// <summary>The user's stored, non-deleted transactions with no trip assigned (review queue).</summary>
    Task<IReadOnlyList<Transaction>> GetUnassignedAsync(Guid userId);

    /// <summary>
    /// Set (or clear, with null) a transaction's adjusted amount — the split/reimbursed
    /// overlay. The raw Plaid Amount is never modified. Negative amounts are rejected
    /// with <see cref="AssignOutcome.InvalidAdjustment"/>.
    /// </summary>
    Task<AssignOutcome> AdjustAsync(Guid userId, Guid transactionId, decimal? adjustedAmount, string? note);

    /// <summary>
    /// User-initiated SOFT delete (sets deleted_at) — financial history is never hard-
    /// deleted, and the plaid_transaction_id dedup (which ignores the soft-delete filter)
    /// guarantees the row is never re-imported by a later sync. Hidden everywhere by the
    /// global query filter afterwards.
    /// </summary>
    Task<AssignOutcome> SoftDeleteAsync(Guid userId, Guid transactionId);
}

/// <summary>Result of an auto-assign run: how many were claimed and an optional cap warning.</summary>
public record AutoAssignResult(int Assigned, string? Warning);

/// <summary>Outcome of a manual assign call.</summary>
public enum AssignOutcome
{
    Success,
    NotFound,
    CapReached,
    InvalidCategory,
    InvalidAdjustment
}
