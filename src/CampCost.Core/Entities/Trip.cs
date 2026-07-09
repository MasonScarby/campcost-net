namespace CampCost.Core.Entities;

/// <summary>
/// Maps to the `trips` table in Supabase.
/// start_date and end_date are nullable (user may not have set dates yet).
/// status: 'planning' | 'active' | 'completed' — we only sync against planning/active.
/// </summary>
public class Trip
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Destination { get; set; }
    public DateOnly? StartDate { get; set; }   // nullable — user may not have set dates
    public DateOnly? EndDate { get; set; }
    public decimal TotalBudget { get; set; }
    public decimal? TotalMiles { get; set; }
    public string Status { get; set; } = "planning";
    public DateTime CreatedAt { get; set; }

    // Navigation
    public ICollection<Expense> Expenses { get; set; } = new List<Expense>();
    public ICollection<BudgetCategory> BudgetCategories { get; set; } = new List<BudgetCategory>();
    public ICollection<TripStop> TripStops { get; set; } = new List<TripStop>();
}
