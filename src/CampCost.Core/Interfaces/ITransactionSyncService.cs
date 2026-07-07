namespace CampCost.Core.Interfaces;

/// <summary>
/// Orchestrates a full transaction sync for a user.
/// Called from the controller for manual syncs and from the webhook handler for automated ones.
/// There is deliberately NO sync-all-users operation: the webhook is unauthenticated,
/// and an all-users sync reachable from it was a launch-blocking security hole.
/// </summary>
public interface ITransactionSyncService
{
    /// <summary>Sync all Plaid connections for a specific user.</summary>
    Task<SyncResult> SyncForUserAsync(Guid userId);

    /// <summary>
    /// Sync transactions for one specific trip. Uses the trip's own date range as
    /// the Plaid window and assigns all matching transactions directly to that trip,
    /// reassigning any that were previously routed to a different trip.
    /// </summary>
    Task<SyncResult> SyncForTripAsync(Guid userId, Guid tripId);
}

/// <summary>
/// Summary of what happened during a sync run.
/// `synced` matches the original Node backend response shape: number of new expenses inserted.
/// ExpensesRemoved = soft-deleted because Plaid reported the transaction as removed.
/// Warning is non-null when the sync could not fully run (e.g. no active trips to match against).
/// </summary>
public record SyncResult(
    int ConnectionsProcessed,
    int TransactionsFetched,
    int ExpensesUpserted,
    int ExpensesSkipped,
    int ExpensesRemoved = 0,
    string? Warning = null
)
{
    /// <summary>Alias for ExpensesUpserted — matches original { synced: N } response the frontend reads.</summary>
    public int synced => ExpensesUpserted;
};
