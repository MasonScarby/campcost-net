namespace CampCost.Core.Interfaces;

/// <summary>
/// Orchestrates a full transaction sync for one or all users.
/// Called from the controller for manual syncs and from the webhook handler for automated ones.
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

    /// <summary>Sync all Plaid connections across all users (used by webhook).</summary>
    Task<SyncResult> SyncAllAsync();
}

/// <summary>
/// Summary of what happened during a sync run.
/// `synced` matches the original Node backend response shape: number of new expenses inserted.
/// </summary>
public record SyncResult(
    int ConnectionsProcessed,
    int TransactionsFetched,
    int ExpensesUpserted,
    int ExpensesSkipped
)
{
    /// <summary>Alias for ExpensesUpserted — matches original { synced: N } response the frontend reads.</summary>
    public int synced => ExpensesUpserted;
};
