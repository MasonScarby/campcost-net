namespace CampCost.Infrastructure;

/// <summary>
/// Mirrors the PostgreSQL expense_category enum exactly.
/// Used only for Npgsql type registration — entities keep string Category for simplicity.
/// </summary>
public enum ExpenseCategoryEnum
{
    fuel,
    campground,
    food_groceries,
    dump_station,
    gear,
    repairs,
    propane_utilities,
    activities,
    misc
}
