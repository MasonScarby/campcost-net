using CampCost.Api.Contracts;
using CampCost.Api.Controllers;
using CampCost.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CampCost.Tests.Controllers;

/// <summary>
/// The import-history window (transactions.days_requested) sent to Plaid must be
/// whitelisted server-side. The client picks 14/30/60/90, but the browser can POST
/// anything — a tampered request must never widen (or narrow) the Plaid fetch beyond
/// the presets. Anything not on the whitelist falls back to the 30-day default.
/// </summary>
public class PlaidControllerImportWindowTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private const int DefaultDays = 30;

    private static (PlaidController controller, Mock<IPlaidService> plaid) NewController()
    {
        var plaid = new Mock<IPlaidService>();
        plaid.Setup(p => p.CreateLinkTokenAsync(It.IsAny<string>(), It.IsAny<int>()))
             .ReturnsAsync("link-token");

        var controller = new PlaidController(
            plaid.Object,
            new Mock<ITransactionSyncService>().Object,
            new Mock<IPlaidWebhookVerifier>().Object,
            new Mock<IAccountService>().Object,
            TestHelpers.NewDb(),
            NullLogger<PlaidController>.Instance);
        controller.SetUser(UserId, aal: "aal2");
        return (controller, plaid);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    public async Task Whitelisted_days_pass_through_unchanged(int days)
    {
        var (controller, plaid) = NewController();

        var result = await controller.CreateLinkToken(new LinkTokenRequest(days));

        result.Should().BeOfType<OkObjectResult>();
        plaid.Verify(p => p.CreateLinkTokenAsync(It.IsAny<string>(), days), Times.Once);
    }

    [Theory]
    [InlineData(730)]   // classic injection: try to pull the max history
    [InlineData(365)]
    [InlineData(45)]    // plausible-but-not-a-preset
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task Non_whitelisted_days_fall_back_to_default(int injected)
    {
        var (controller, plaid) = NewController();

        var result = await controller.CreateLinkToken(new LinkTokenRequest(injected));

        result.Should().BeOfType<OkObjectResult>();
        // The injected value is NEVER forwarded to Plaid — only the safe default is.
        plaid.Verify(p => p.CreateLinkTokenAsync(It.IsAny<string>(), injected), Times.Never);
        plaid.Verify(p => p.CreateLinkTokenAsync(It.IsAny<string>(), DefaultDays), Times.Once);
    }

    [Fact]
    public async Task Null_days_falls_back_to_default()
    {
        var (controller, plaid) = NewController();

        var result = await controller.CreateLinkToken(new LinkTokenRequest(null));

        result.Should().BeOfType<OkObjectResult>();
        plaid.Verify(p => p.CreateLinkTokenAsync(It.IsAny<string>(), DefaultDays), Times.Once);
    }

    [Fact]
    public async Task Missing_body_falls_back_to_default()
    {
        var (controller, plaid) = NewController();

        var result = await controller.CreateLinkToken(null);

        result.Should().BeOfType<OkObjectResult>();
        plaid.Verify(p => p.CreateLinkTokenAsync(It.IsAny<string>(), DefaultDays), Times.Once);
    }
}
