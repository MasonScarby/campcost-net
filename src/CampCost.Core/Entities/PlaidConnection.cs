namespace CampCost.Core.Entities;

/// <summary>
/// Maps to the `plaid_connections` table in Supabase.
/// Stores the encrypted access token per user so we can re-pull transactions on demand.
/// </summary>
public class PlaidConnection
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string ItemId { get; set; } = string.Empty;
    public string InstitutionName { get; set; } = string.Empty;
    public DateTime? LastSyncAt { get; set; }

    /// <summary>
    /// Plaid /transactions/sync cursor — position of the last successfully applied
    /// delta set. Null means the connection has never synced (first sync pulls full
    /// history). Only persist after deltas are applied, or updates would be lost.
    /// </summary>
    public string? SyncCursor { get; set; }

    /// <summary>
    /// Set true when Plaid signals the Item needs re-authentication (an ITEM webhook with
    /// error_code ITEM_LOGIN_REQUIRED, or PENDING_EXPIRATION). The UI surfaces a "Reconnect"
    /// action that opens Link in UPDATE mode — the SAME Item is re-authed, so item_id and
    /// transaction_ids stay stable and no duplicate history is created (unlike disconnect +
    /// reconnect, which mints a new Item). Cleared automatically on the next successful sync.
    /// </summary>
    public bool NeedsReauth { get; set; }

    public DateTime CreatedAt { get; set; }
}
