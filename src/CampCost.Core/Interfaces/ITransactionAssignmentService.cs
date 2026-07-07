using CampCost.Core.Entities;

namespace CampCost.Core.Interfaces;

/// <summary>
/// Assigns raw Plaid transactions to trips. Assignment is decoupled from ingestion:
/// transactions are stored first (trip_id NULL) and claimed later, either automatically
/// when a trip's date range covers them or manually by the user.
/// </summary>
public interface ITransactionAssignmentService
{
    /// <summary>
    /// Auto-assign the user's UNASSIGNED, non-reviewed transactions whose transaction_date
    /// falls in [trip.StartDate, trip.EndDate] to the given trip. Never touches transactions
    /// already assigned to another trip, and never moves a reviewed (user-confirmed) row.
    /// No-op (returns 0) when the trip has no start/end dates. Returns the number claimed.
    /// Called when a trip is created or its dates change.
    /// </summary>
    Task<int> AutoAssignForTripAsync(Guid userId, Guid tripId);

    /// <summary>
    /// Manual override: set a transaction's trip (or null to unassign) and mark it reviewed
    /// so auto-assign won't move it again. Returns false if the transaction (or target trip)
    /// isn't owned by the user. Passing a null tripId unassigns the transaction.
    /// </summary>
    Task<bool> AssignAsync(Guid userId, Guid transactionId, Guid? tripId);

    /// <summary>The user's stored, non-deleted transactions with no trip assigned (review queue).</summary>
    Task<IReadOnlyList<Transaction>> GetUnassignedAsync(Guid userId);
}
