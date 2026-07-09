namespace CampCost.Core.Interfaces;

/// <summary>
/// Server-side source of truth for a user's plan.
/// Reads the `subscriptions` table — never trust a plan value sent by the client.
/// </summary>
public interface IPlanService
{
    /// <summary>
    /// Returns "free" or "pro". Defaults to "free" when the user has no subscription
    /// row or the subscription is not active (cancelled / past_due).
    /// </summary>
    Task<string> GetPlanAsync(Guid userId);
}
