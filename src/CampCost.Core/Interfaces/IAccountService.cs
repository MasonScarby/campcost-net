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
    /// Permanently deletes the user's account and ALL their data: removes every Plaid Item,
    /// then deletes transactions, expenses, budget categories, trip stops, trips, connections,
    /// and subscription rows, and finally the Supabase auth user. Irreversible.
    /// </summary>
    Task DeleteAccountAsync(Guid userId);
}
