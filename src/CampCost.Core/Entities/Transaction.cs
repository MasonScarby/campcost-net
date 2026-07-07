namespace CampCost.Core.Entities;

/// <summary>
/// Maps to the `transactions` table (migration 004) — the raw Plaid mirror and source
/// of truth for bank-synced spend. Every synced Plaid delta lands here regardless of
/// whether a trip claims it, so no transaction is ever lost to trip gating.
///
/// Column names use snake_case to match Supabase exactly (see CampCostDbContext).
/// category must match the expense_category enum: fuel, campground, food_groceries,
/// gear, repairs, propane_utilities, activities, misc.
///
/// Relationship to `expenses`: going forward `expenses` holds MANUAL entries only.
/// A trip's Plaid spend = SUM(transactions WHERE trip_id = trip AND deleted_at IS NULL);
/// total trip spend adds the manual expenses on top.
/// </summary>
public class Transaction : ISpendLine
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PlaidConnectionId { get; set; }

    /// <summary>Plaid's transaction id — settled id after pending→settled reconciliation. UNIQUE (dedup key).</summary>
    public string PlaidTransactionId { get; set; } = string.Empty;

    /// <summary>Links a settled transaction back to its earlier pending row (Plaid assigns a new id on settle).</summary>
    public string? PendingTransactionId { get; set; }

    /// <summary>NULL = unassigned. Nullable by design: ingestion no longer requires a matching trip.</summary>
    public Guid? TripId { get; set; }

    public decimal Amount { get; set; }
    public DateOnly TransactionDate { get; set; }
    public string? MerchantName { get; set; }

    /// <summary>Plaid raw description.</summary>
    public string? Name { get; set; }

    public string Category { get; set; } = "misc";
    public bool Pending { get; set; } = false;

    /// <summary>True once the user confirms the trip assignment — auto-assign must not move a reviewed row.</summary>
    public bool Reviewed { get; set; } = false;

    /// <summary>
    /// Soft-delete marker — set when Plaid reports the transaction as `removed`.
    /// Financial history is never hard-deleted. A global query filter excludes
    /// soft-deleted rows from all queries unless IgnoreQueryFilters() is used.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
