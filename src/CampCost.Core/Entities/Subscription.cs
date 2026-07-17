namespace CampCost.Core.Entities;

/// <summary>
/// Maps to the `subscriptions` table. One row per user.
/// Plan: 'free' | 'pro'. Status: 'active' | 'cancelled' | 'past_due'.
/// </summary>
public class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Plan { get; set; } = "free";
    public string Status { get; set; } = "active";
    /// <summary>Stripe customer id — the join key for webhook events. Never granted to the browser.</summary>
    public string? StripeCustomerId { get; set; }
    public DateTime? UpdatedAt { get; set; }
}
