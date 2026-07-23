namespace CampCost.Core.Interfaces;

/// <summary>
/// Account-lifecycle operations that span Plaid + the database: disconnecting a bank and
/// full account/data deletion. Both are Plaid production + privacy-compliance requirements.
/// </summary>
public interface IAccountService
{
    /// <summary>
    /// Disconnects one bank connection: calls Plaid /item/remove to invalidate the token, then
    /// deletes the local connection row. Its imported transactions are removed with it via the
    /// FK ON DELETE CASCADE (migration 004). Returns false if the user has no connection with
    /// that item_id.
    /// </summary>
    Task<bool> DisconnectBankAsync(Guid userId, string itemId);

    /// <summary>
    /// Disconnects one bank connection identified by its row <c>id</c> — the only identifier the
    /// browser can read (item_id/access_token are withheld from clients by RLS, migration 003).
    /// Scoped to the caller: a connection id that isn't this user's returns false and touches
    /// nothing. Calls Plaid /item/remove to revoke the live token, then deletes the local row
    /// (transactions detach and are kept, per migration 008). Returns false if not found.
    /// This is the path the disconnect UI uses — a direct client-side delete cannot revoke the
    /// Plaid token and is blocked by RLS anyway.
    /// </summary>
    Task<bool> DisconnectBankByIdAsync(Guid userId, Guid connectionId);

    /// <summary>
    /// Hard-deletes ALL of the user's synced bank <c>transactions</c> (the Plaid mirror,
    /// including soft-deleted rows) and resets every connection's sync cursor, so the next
    /// sync re-imports a clean set. Bank connections stay linked and manual <c>expenses</c>
    /// are left intact. User-initiated escape hatch for duplicate/stale imported data (e.g.
    /// after a disconnect/reconnect creates duplicates). Returns the number removed.
    /// </summary>
    Task<int> ClearTransactionsAsync(Guid userId);

    /// <summary>
    /// Permanently deletes the user's account and ALL their data: removes every Plaid Item,
    /// then deletes transactions, expenses, budget categories, trip stops, trips, connections,
    /// and subscription rows, and finally the Supabase auth user. Irreversible.
    /// </summary>
    Task DeleteAccountAsync(Guid userId);
}
