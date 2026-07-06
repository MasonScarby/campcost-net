namespace CampCost.Core.Entities;

/// <summary>
/// Maps to the `expenses` table in Supabase.
/// Column names use snake_case to match Supabase exactly.
/// category must match the expense_category enum: fuel, campground, food_groceries,
/// gear, repairs, propane_utilities, activities, misc.
/// </summary>
public class Expense
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }           // NOT NULL in DB — skip tx if no trip match
    public Guid UserId { get; set; }
    public string? PlaidTransactionId { get; set; } // UNIQUE — dedup key
    public string? MerchantName { get; set; }
    public decimal Amount { get; set; }
    public string Category { get; set; } = "misc";
    public DateOnly ExpenseDate { get; set; }       // DB column: expense_date (date, not timestamp)
    public string Source { get; set; } = "plaid";   // DB enum: 'plaid' | 'manual'
    public bool Reviewed { get; set; } = false;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Soft-delete marker — set when Plaid reports the transaction as removed.
    /// Financial history is never hard-deleted. A global query filter excludes
    /// soft-deleted rows from all queries unless IgnoreQueryFilters() is used.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Trip? Trip { get; set; }
}
