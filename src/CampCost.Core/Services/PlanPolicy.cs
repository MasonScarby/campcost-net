namespace CampCost.Core.Services;

/// <summary>
/// Pure plan-limit rules for free vs pro. No I/O — fully deterministic and unit-testable.
/// The server is the source of truth for the plan (subscriptions table, read by
/// <see cref="Interfaces.IPlanService"/>); anything in client localStorage is display-only.
/// Numeric thresholds live in <see cref="FreePlanLimits"/>.
/// </summary>
public static class PlanPolicy
{
    public const string FreePlan = "free";
    public const string ProPlan = "pro";

    public const string TripDurationUpgradeMessage =
        "Free plan trips are limited to 12 days. Upgrade to Pro for unlimited trip length.";

    public const string TripCountUpgradeMessage =
        "Free plan is limited to 3 trips. Upgrade to Pro for unlimited trips.";

    public static bool IsPro(string? plan) =>
        string.Equals(plan, ProPlan, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inclusive trip length in days. A trip starting and ending the same day is 1 day.
    /// Null when either date is missing (no duration to enforce).
    /// </summary>
    public static int? TripDurationDays(DateOnly? start, DateOnly? end) =>
        start.HasValue && end.HasValue
            ? end.Value.DayNumber - start.Value.DayNumber + 1
            : null;

    /// <summary>
    /// Free plan: reject trips longer than <see cref="FreePlanLimits.MaxTripDays"/> days
    /// (exactly 12 is allowed, 13 is rejected). Pro: always allowed. Missing dates: allowed
    /// (nothing to measure).
    /// </summary>
    public static bool IsTripDurationAllowed(string? plan, DateOnly? start, DateOnly? end)
    {
        if (IsPro(plan)) return true;
        var days = TripDurationDays(start, end);
        return days is null || days <= FreePlanLimits.MaxTripDays;
    }

    /// <summary>
    /// Free plan: reject creating a new trip once the user already has
    /// <see cref="FreePlanLimits.MaxTripsFree"/> trips. Pro: always allowed.
    /// <paramref name="currentTripCount"/> is the user's existing trip count.
    /// </summary>
    public static bool IsTripCountAllowed(string? plan, int currentTripCount)
    {
        if (IsPro(plan)) return true;
        return currentTripCount < FreePlanLimits.MaxTripsFree;
    }
}
