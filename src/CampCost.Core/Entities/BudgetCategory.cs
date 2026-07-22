namespace CampCost.Core.Entities;

/// <summary>
/// Maps to the `budget_categories` table. Stores the planned (budgeted) amount
/// per category for a trip. Ownership is via the parent trip — no user_id column;
/// RLS uses a join to trips (Pattern B).
/// </summary>
public class BudgetCategory
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }

    /// <summary>
    /// Must match the expense_category enum: fuel, campground, food_groceries,
    /// gear, repairs, propane_utilities, activities, misc.
    /// </summary>
    public string Category { get; set; } = string.Empty;
    public decimal PlannedAmount { get; set; }

    /// <summary>Optional "why" note explaining the most recent edit to PlannedAmount.
    /// Latest note only — overwritten each time the category is edited.</summary>
    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }

    public Trip? Trip { get; set; }
}
