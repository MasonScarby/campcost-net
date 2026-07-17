using CampCost.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;

namespace CampCost.Api.Controllers;

[ApiController]
[Route("api/billing")]
public class BillingController : ControllerBase
{
    private readonly IBillingService _billing;
    private readonly IPlanService _plans;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<BillingController> _logger;

    public BillingController(
        IBillingService billing, IPlanService plans, IWebHostEnvironment env, ILogger<BillingController> logger)
    {
        _billing = billing;
        _plans = plans;
        _env = env;
        _logger = logger;
    }

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

    // POST /api/billing/checkout
    // Creates a Stripe Checkout session for CampCost Pro and returns its URL.
    // The browser redirects there; the PLAN WRITE happens later via the webhook only.
    [HttpPost("checkout")]
    [Authorize]
    public async Task<IActionResult> CreateCheckout()
    {
        try
        {
            var email = User.FindFirstValue(ClaimTypes.Email) ?? User.FindFirstValue("email");
            var url = await _billing.CreateCheckoutUrlAsync(UserId, email);
            return Ok(new { url });
        }
        catch (InvalidOperationException ex)
        {
            // Stripe not configured — surface a clear message instead of a 500.
            _logger.LogWarning(ex, "Checkout requested but Stripe is not configured");
            return StatusCode(503, new { error = "Payments are not available yet." });
        }
    }

    // POST /api/billing/cancel
    // Period-end cancellation: the user keeps Pro until the paid period lapses, then the
    // subscription.deleted webhook drops the plan to free. There is deliberately NO
    // instant self-serve downgrade — the plan write path stays webhook-only in prod.
    // Dev fallback: a dev-toggled Pro (no Stripe subscription) just flips back to free.
    [HttpPost("cancel")]
    [Authorize]
    public async Task<IActionResult> Cancel()
    {
        var cancelled = await _billing.CancelAtPeriodEndAsync(UserId);
        if (cancelled)
            return Ok(new { status = "cancel_at_period_end" });

        if (_env.IsDevelopment())
        {
            // No Stripe subscription behind this Pro (dev toggle) — drop it directly.
            await _plans.SetPlanAsync(UserId, "free");
            return Ok(new { status = "dev_downgraded" });
        }

        return NotFound(new { error = "No active subscription found." });
    }

    // POST /api/billing/webhook
    // Stripe calls this; signature verified inside the service (400 on mismatch).
    // Same rate-limit policy as the Plaid webhook (per-IP).
    [HttpPost("webhook")]
    [AllowAnonymous]
    [EnableRateLimiting("webhook-per-ip")]
    public async Task<IActionResult> Webhook()
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();
        var signature = Request.Headers["Stripe-Signature"].ToString();

        try
        {
            var applied = await _billing.HandleWebhookAsync(json, signature);
            _logger.LogInformation("Stripe webhook applied: {Applied}", applied);
            return Ok();
        }
        catch (Stripe.StripeException ex)
        {
            _logger.LogWarning("Stripe webhook rejected: {Message}", ex.Message);
            return BadRequest();
        }
    }
}
