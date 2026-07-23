using CampCost.Api.Contracts;
using CampCost.Api.Controllers;
using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CampCost.Tests.Controllers;

/// <summary>
/// POST /api/plaid/update-link-token mints a Link token in UPDATE mode for an existing Item.
/// It is keyed off the connection row id + the JWT user, so a user can only re-auth their own
/// bank — never someone else's — and the (server-only) access_token is what's handed to Plaid.
/// </summary>
public class PlaidControllerUpdateModeTests
{
    private readonly CampCostDbContext _db = TestHelpers.NewDb();
    private readonly Mock<IPlaidService> _plaid = new();

    private PlaidController NewController(Guid userId)
    {
        _plaid.Setup(p => p.CreateUpdateLinkTokenAsync(It.IsAny<string>(), It.IsAny<string>()))
              .ReturnsAsync("update-link-token");

        var controller = new PlaidController(
            _plaid.Object,
            Mock.Of<ITransactionSyncService>(),
            Mock.Of<IPlaidWebhookVerifier>(),
            Mock.Of<IAccountService>(),
            _db,
            NullLogger<PlaidController>.Instance);
        controller.SetUser(userId, aal: "aal2");
        return controller;
    }

    private Guid SeedConnection(Guid userId)
    {
        var id = Guid.NewGuid();
        _db.PlaidConnections.Add(new PlaidConnection
        {
            Id = id, UserId = userId, AccessToken = "access-token",
            ItemId = "item-" + id, InstitutionName = "Bank", CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task Returns_update_token_for_the_users_own_connection()
    {
        var userId = Guid.NewGuid();
        var connId = SeedConnection(userId);
        var controller = NewController(userId);

        var result = await controller.CreateUpdateLinkToken(new UpdateLinkTokenRequest(connId));

        result.Should().BeOfType<OkObjectResult>();
        // The connection's server-only access_token is what's handed to Plaid for update mode.
        _plaid.Verify(p => p.CreateUpdateLinkTokenAsync(userId.ToString(), "access-token"), Times.Once);
    }

    [Fact]
    public async Task Cannot_mint_an_update_token_for_another_users_connection()
    {
        var owner = Guid.NewGuid();
        var connId = SeedConnection(owner);
        var attacker = Guid.NewGuid();
        var controller = NewController(attacker);

        var result = await controller.CreateUpdateLinkToken(new UpdateLinkTokenRequest(connId));

        result.Should().BeOfType<NotFoundObjectResult>();
        _plaid.Verify(p => p.CreateUpdateLinkTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Empty_id_is_rejected_with_400()
    {
        var controller = NewController(Guid.NewGuid());

        var result = await controller.CreateUpdateLinkToken(new UpdateLinkTokenRequest(Guid.Empty));

        result.Should().BeOfType<BadRequestObjectResult>();
        _plaid.Verify(p => p.CreateUpdateLinkTokenAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }
}
