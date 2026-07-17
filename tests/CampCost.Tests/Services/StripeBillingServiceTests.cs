using CampCost.Core.Entities;
using CampCost.Infrastructure.Data;
using CampCost.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Stripe;

namespace CampCost.Tests.Services;

/// <summary>
/// The plan write path is webhook-only, so the webhook must (1) reject anything without a
/// valid Stripe signature and (2) refuse checkout when Stripe isn't configured.
/// Full event-application paths are covered by the Stripe CLI smoke test (signatures can't
/// be forged in unit tests by design — that's the point of them).
/// </summary>
public class StripeBillingServiceTests
{
    private static StripeBillingService NewSut(out CampCostDbContext db, string webhookSecret = "whsec_test")
    {
        db = TestHelpers.NewDb();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Stripe:SecretKey"] = "", // deliberately unconfigured for checkout tests
            ["Stripe:WebhookSecret"] = webhookSecret,
            ["Stripe:PriceId"] = "",
        }).Build();
        return new StripeBillingService(db, config, NullLogger<StripeBillingService>.Instance);
    }

    [Fact]
    public async Task Webhook_InvalidSignature_Throws()
    {
        var sut = NewSut(out var db);
        using (db)
        {
            var act = () => sut.HandleWebhookAsync("""{"type":"checkout.session.completed"}""", "t=1,v1=forged");
            await act.Should().ThrowAsync<StripeException>("unsigned/forged payloads must never mutate the plan");
        }
    }

    [Fact]
    public async Task Webhook_MissingSignatureHeader_Throws()
    {
        var sut = NewSut(out var db);
        using (db)
        {
            var act = () => sut.HandleWebhookAsync("""{"type":"checkout.session.completed"}""", "");
            await act.Should().ThrowAsync<StripeException>();
        }
    }

    [Fact]
    public async Task Checkout_WithoutConfiguration_ThrowsInvalidOperation()
    {
        var sut = NewSut(out var db);
        using (db)
        {
            var act = () => sut.CreateCheckoutUrlAsync(Guid.NewGuid(), "user@example.com");
            await act.Should().ThrowAsync<InvalidOperationException>("the controller maps this to a clean 503");
        }
    }
}
