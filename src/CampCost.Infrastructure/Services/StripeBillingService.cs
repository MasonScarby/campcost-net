using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using Stripe.Checkout;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Stripe billing. Plan changes ONLY flow through the verified webhook — Checkout redirects
/// the browser back with a success flag, but the flag is cosmetic; the webhook is the write.
/// Handlers are idempotent (upserts keyed by user / customer id) so Stripe replays are safe,
/// per the financial rules in CLAUDE.md.
/// </summary>
public class StripeBillingService : IBillingService
{
    private readonly CampCostDbContext _db;
    private readonly ILogger<StripeBillingService> _logger;
    private readonly string _secretKey;
    private readonly string _webhookSecret;
    private readonly string _priceId;
    private readonly string _clientUrl;

    public StripeBillingService(CampCostDbContext db, IConfiguration config, ILogger<StripeBillingService> logger)
    {
        _db = db;
        _logger = logger;
        _secretKey = config["Stripe:SecretKey"] ?? "";
        _webhookSecret = config["Stripe:WebhookSecret"] ?? "";
        _priceId = config["Stripe:PriceId"] ?? "";
        // Where Checkout sends the browser afterwards (the account page shows the result).
        _clientUrl = config["Stripe:ClientUrl"] ?? "http://localhost:5173";
    }

    public async Task<string> CreateCheckoutUrlAsync(Guid userId, string? email)
    {
        if (string.IsNullOrEmpty(_secretKey) || string.IsNullOrEmpty(_priceId))
            throw new InvalidOperationException("Stripe is not configured (Stripe:SecretKey / Stripe:PriceId).");

        var options = new SessionCreateOptions
        {
            Mode = "subscription",
            LineItems = [new SessionLineItemOptions { Price = _priceId, Quantity = 1 }],
            // The join key back to a Supabase user when the webhook fires.
            ClientReferenceId = userId.ToString(),
            CustomerEmail = email,
            SuccessUrl = $"{_clientUrl}/account?checkout=success",
            CancelUrl = $"{_clientUrl}/account?checkout=cancelled",
        };
        var session = await new SessionService(new StripeClient(_secretKey)).CreateAsync(options);
        _logger.LogInformation("Created Stripe checkout session for user {UserId}", userId);
        return session.Url;
    }

    public async Task<string> HandleWebhookAsync(string json, string signatureHeader)
    {
        // Throws StripeException on a bad signature — the controller maps that to 400.
        // Tolerance is Stripe's default (5 min) to block replayed payloads.
        var stripeEvent = EventUtility.ConstructEvent(json, signatureHeader, _webhookSecret);

        switch (stripeEvent.Type)
        {
            case "checkout.session.completed":
            {
                var session = (Session)stripeEvent.Data.Object;
                if (!Guid.TryParse(session.ClientReferenceId, out var userId))
                {
                    _logger.LogWarning("Checkout completed without a valid client_reference_id — ignoring");
                    return "ignored: no client_reference_id";
                }
                await UpsertAsync(userId, PlanPolicy.ProPlan, "active", session.CustomerId);
                return $"pro activated for user {userId}";
            }

            case "customer.subscription.updated":
            case "customer.subscription.deleted":
            {
                var sub = (Stripe.Subscription)stripeEvent.Data.Object;
                var row = await _db.Subscriptions.FirstOrDefaultAsync(s => s.StripeCustomerId == sub.CustomerId);
                if (row is null)
                {
                    // Unknown customer: log + 200 so Stripe doesn't retry forever (same
                    // pattern as the Plaid webhook's unknown item_id).
                    _logger.LogWarning("Stripe subscription event for unknown customer — ignoring");
                    return "ignored: unknown customer";
                }
                // active/trialing keep Pro; anything else (canceled, past_due, unpaid…)
                // drops to free via PlanService's status check.
                var active = sub.Status is "active" or "trialing";
                row.Plan = active ? PlanPolicy.ProPlan : PlanPolicy.FreePlan;
                row.Status = active ? "active" : (sub.Status == "past_due" ? "past_due" : "cancelled");
                row.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return $"subscription {sub.Status} → plan {row.Plan}";
            }

            default:
                return $"ignored: {stripeEvent.Type}";
        }
    }

    public async Task<bool> CancelAtPeriodEndAsync(Guid userId)
    {
        var row = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (row?.StripeCustomerId is null) return false; // no Stripe-backed subscription

        var client = new StripeClient(_secretKey);
        var subService = new SubscriptionService(client);
        var subs = await subService.ListAsync(new SubscriptionListOptions
        {
            Customer = row.StripeCustomerId,
            Status = "active",
            Limit = 5,
        });
        var found = false;
        foreach (var sub in subs)
        {
            // Period-end cancel: user keeps what they paid for; the subscription.deleted
            // webhook flips the plan to free when the period actually lapses.
            await subService.UpdateAsync(sub.Id, new SubscriptionUpdateOptions { CancelAtPeriodEnd = true });
            found = true;
        }
        if (found)
            _logger.LogInformation("Scheduled period-end cancellation for user {UserId}", userId);
        return found;
    }

    private async Task UpsertAsync(Guid userId, string plan, string status, string? customerId)
    {
        var row = await _db.Subscriptions.FirstOrDefaultAsync(s => s.UserId == userId);
        if (row is null)
        {
            row = new Core.Entities.Subscription { Id = Guid.NewGuid(), UserId = userId };
            _db.Subscriptions.Add(row);
        }
        row.Plan = plan;
        row.Status = status;
        if (customerId is not null) row.StripeCustomerId = customerId;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }
}
