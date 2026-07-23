namespace CampCost.Core.Interfaces;

public interface IPlaidService
{
    /// <summary>
    /// Creates a Plaid Link token. <paramref name="transactionDays"/> sets Plaid's
    /// transactions.days_requested — the maximum days of history Plaid fetches for this
    /// item on first sync. Callers MUST pass a validated value (the controller whitelists
    /// it); Plaid never sees an unbounded/injected number.
    /// </summary>
    Task<string> CreateLinkTokenAsync(string userId, int transactionDays);

    /// <summary>
    /// Creates a Link token in UPDATE mode for an existing Item, identified by its
    /// <paramref name="accessToken"/>. Update mode re-authenticates the SAME Item — item_id and
    /// transaction_ids stay stable — so a user fixing a broken connection never mints a new Item
    /// and never gets the duplicate history a disconnect+reconnect causes. No products/history
    /// window are specified (they belong to the original create). Used for ITEM_LOGIN_REQUIRED /
    /// PENDING_EXPIRATION re-auth.
    /// </summary>
    Task<string> CreateUpdateLinkTokenAsync(string userId, string accessToken);

    Task<(string AccessToken, string ItemId)> ExchangePublicTokenAsync(string publicToken);

    /// <summary>
    /// Pulls transaction deltas via Plaid /transactions/sync, looping on has_more
    /// until the full update set is fetched (fixes the old 500-transaction truncation).
    /// Pass a null cursor for the first sync of a connection — Plaid returns the full
    /// available history as `added`. The returned NextCursor must be persisted only
    /// after the deltas have been successfully applied.
    /// </summary>
    Task<PlaidSyncDelta> SyncTransactionsAsync(string accessToken, string? cursor);

    /// <summary>
    /// Calls Plaid /item/remove to invalidate the access token and stop all billing/access
    /// for this Item. Required when a user disconnects a bank or deletes their account.
    /// Best-effort: throws only on unexpected transport errors — callers should still remove
    /// local data even if Plaid reports the item already gone.
    /// </summary>
    Task RemoveItemAsync(string accessToken);
}

/// <summary>
/// Lightweight DTO — Plaid SDK types never leak into Core.
/// Name = raw transaction name, MerchantName = cleaned merchant name (may be null).
/// Amount is positive for debits (money out), negative for credits — same as Plaid convention.
/// PendingTransactionId links a settled transaction back to its earlier pending version
/// (Plaid assigns a NEW transaction_id when a pending transaction settles).
/// </summary>
public record PlaidTransaction(
    string TransactionId,
    string Name,
    string? MerchantName,
    decimal Amount,
    DateOnly Date,
    bool Pending,
    string? PendingTransactionId = null
);

/// <summary>
/// Aggregated added/modified/removed deltas from /transactions/sync plus the cursor
/// to persist once the deltas have been applied to the database.
/// </summary>
public record PlaidSyncDelta(
    IReadOnlyList<PlaidTransaction> Added,
    IReadOnlyList<PlaidTransaction> Modified,
    IReadOnlyList<string> RemovedTransactionIds,
    string NextCursor
);
