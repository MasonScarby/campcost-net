namespace CampCost.Core.Services;

/// <summary>
/// Numeric limits for the free plan, isolated in one place so a pricing change is a
/// one-line edit. Pro plan is uncapped everywhere these values apply.
/// </summary>
public static class FreePlanLimits
{
    /// <summary>
    /// Free plan: a trip may have at most this many ASSIGNED transactions. This gates the
    /// trip-assigned count (trip_id set, not soft-deleted) — NOT the raw stored count, so
    /// unassigned Plaid rows never burn the quota. Pro = unlimited.
    /// </summary>
    public const int MaxAssignedTransactionsPerTrip = 50;

    /// <summary>
    /// Free plan: trips may span at most this many days (inclusive of start and end —
    /// a same-day trip is 1 day). Pro = unlimited.
    /// </summary>
    public const int MaxTripDays = 7;

    /// <summary>
    /// Free plan: a user may have at most this many trips. Pro = unlimited.
    /// </summary>
    public const int MaxTripsFree = 3;

    /// <summary>Warning surfaced when the assignment cap is hit on the free plan.</summary>
    public const string TransactionCapWarning =
        "Free plan limited to 50 transactions per trip — upgrade for unlimited.";
}
