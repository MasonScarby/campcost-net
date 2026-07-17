namespace CampCost.Core.Interfaces;

/// <summary>
/// Subscription billing (Stripe). The plan write path is: Stripe Checkout → webhook →
/// <see cref="IPlanService.SetPlanAsync"/>. The browser NEVER writes the plan directly
/// (subscriptions table is locked down by migration 009; the dev toggle 404s in prod).
/// </summary>
public interface IBillingService
{
    /// <summary>
    /// Creates a Stripe Checkout session (subscription mode) for the user and returns its
    /// redirect URL. The user id rides along as client_reference_id so the webhook can map
    /// the completed session back to a Supabase user.
    /// </summary>
    Task<string> CreateCheckoutUrlAsync(Guid userId, string? email);

    /// <summary>
    /// Verifies the webhook signature and applies the event. Idempotent — replays are safe
    /// (plan writes are upserts keyed by user). Throws on an invalid signature; the caller
    /// maps that to 400. Returns a short human-readable description of what was applied
    /// (for structured logging — never contains tokens or PII).
    /// </summary>
    Task<string> HandleWebhookAsync(string json, string signatureHeader);
}
