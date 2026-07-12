using CampCost.Api.Contracts;
using CampCost.Api.Controllers;
using CampCost.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CampCost.Tests.Controllers;

/// <summary>
/// Defense in depth: POST /api/plaid/exchange-token requires the JWT aal claim to be
/// "aal2" (Supabase stamps it only after a verified MFA challenge). The client-side
/// gate in ConnectBank.jsx must never be the only barrier — a forged request with a
/// valid aal1 session token has to die here, before Plaid is ever contacted.
/// </summary>
public class PlaidControllerAalTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private static (PlaidController controller, Mock<IPlaidService> plaid) NewController(string? aal)
    {
        var plaid = new Mock<IPlaidService>();
        plaid.Setup(p => p.ExchangePublicTokenAsync(It.IsAny<string>()))
             .ReturnsAsync(("access-token", "item-1"));

        var controller = new PlaidController(
            plaid.Object,
            new Mock<ITransactionSyncService>().Object,
            new Mock<IPlaidWebhookVerifier>().Object,
            TestHelpers.NewDb(),
            NullLogger<PlaidController>.Instance);
        controller.SetUser(UserId, aal);
        return (controller, plaid);
    }

    [Fact]
    public async Task Exchange_without_aal_claim_is_rejected_403()
    {
        var (controller, plaid) = NewController(aal: null);

        var result = await controller.ExchangeToken(new ExchangeRequest("public-token", "Test Bank"));

        var status = result.Should().BeOfType<ObjectResult>().Subject;
        status.StatusCode.Should().Be(403);
        status.Value!.ToString().Should().Contain("mfa_required");

        // The Plaid exchange must never even be attempted
        plaid.Verify(p => p.ExchangePublicTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Exchange_with_aal1_is_rejected_403()
    {
        var (controller, plaid) = NewController(aal: "aal1");

        var result = await controller.ExchangeToken(new ExchangeRequest("public-token", "Test Bank"));

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(403);
        plaid.Verify(p => p.ExchangePublicTokenAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Exchange_with_aal2_succeeds()
    {
        var (controller, _) = NewController(aal: "aal2");

        var result = await controller.ExchangeToken(new ExchangeRequest("public-token", "Test Bank"));

        result.Should().BeOfType<OkObjectResult>();
    }
}
