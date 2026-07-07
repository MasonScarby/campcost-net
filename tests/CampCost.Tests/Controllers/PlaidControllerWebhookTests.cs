using System.Text;
using CampCost.Api.Controllers;
using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CampCost.Tests.Controllers;

/// <summary>
/// Webhook endpoint security behavior:
///   - unsigned / badly signed requests are rejected with 401 and never trigger a sync
///   - a verified webhook for an unknown item_id is acknowledged but syncs nothing
///     (the old SyncAllAsync fallback is gone — the method no longer even exists)
///   - a verified webhook for a known item_id syncs exactly that connection's user
/// </summary>
public class PlaidControllerWebhookTests
{
    private const string TransactionsBody =
        """{"webhook_type":"TRANSACTIONS","item_id":"item-1"}""";

    private readonly CampCostDbContext _db;
    private readonly Mock<ITransactionSyncService> _sync = new();
    private readonly Mock<IPlaidWebhookVerifier> _verifier = new();
    private readonly PlaidController _controller;

    public PlaidControllerWebhookTests()
    {
        _db = new CampCostDbContext(new DbContextOptionsBuilder<CampCostDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _controller = new PlaidController(
            Mock.Of<IPlaidService>(),
            _sync.Object,
            _verifier.Object,
            _db,
            NullLogger<PlaidController>.Instance);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private void SetRequest(string body, string? verificationHeader)
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (verificationHeader is not null)
            context.Request.Headers["Plaid-Verification"] = verificationHeader;
        _controller.ControllerContext = new ControllerContext { HttpContext = context };
    }

    private Guid SeedConnection(string itemId)
    {
        var userId = Guid.NewGuid();
        _db.PlaidConnections.Add(new PlaidConnection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AccessToken = "access-token",
            ItemId = itemId,
            InstitutionName = "Test Bank",
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
        return userId;
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Unsigned_webhook_is_rejected_with_401_and_never_syncs()
    {
        SeedConnection("item-1");
        SetRequest(TransactionsBody, verificationHeader: null);

        var result = await _controller.Webhook();

        result.Should().BeOfType<UnauthorizedResult>();
        _sync.Verify(s => s.SyncForUserAsync(It.IsAny<Guid>()), Times.Never);
        _verifier.Verify(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Webhook_with_invalid_signature_is_rejected_with_401_and_never_syncs()
    {
        SeedConnection("item-1");
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(false);
        SetRequest(TransactionsBody, verificationHeader: "forged.jwt.value");

        var result = await _controller.Webhook();

        result.Should().BeOfType<UnauthorizedResult>();
        _sync.Verify(s => s.SyncForUserAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Verified_webhook_for_unknown_item_id_is_acknowledged_but_syncs_nothing()
    {
        SeedConnection("some-other-item");
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(true);
        SetRequest("""{"webhook_type":"TRANSACTIONS","item_id":"unknown-item"}""", "valid.jwt.value");

        var result = await _controller.Webhook();

        result.Should().BeOfType<OkObjectResult>();
        _sync.Verify(s => s.SyncForUserAsync(It.IsAny<Guid>()), Times.Never);
        _sync.VerifyNoOtherCalls(); // nothing resembling a sync-all either
    }

    [Fact]
    public async Task Verified_webhook_with_missing_item_id_is_acknowledged_but_syncs_nothing()
    {
        SeedConnection("item-1");
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(true);
        SetRequest("""{"webhook_type":"TRANSACTIONS"}""", "valid.jwt.value");

        var result = await _controller.Webhook();

        result.Should().BeOfType<OkObjectResult>();
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Verified_webhook_for_known_item_id_syncs_only_that_user()
    {
        var userId = SeedConnection("item-1");
        SeedConnection("item-2"); // another user's connection must NOT be synced
        _verifier.Setup(v => v.VerifyAsync(TransactionsBody, "valid.jwt.value"))
                 .ReturnsAsync(true);
        SetRequest(TransactionsBody, "valid.jwt.value");

        var result = await _controller.Webhook();

        result.Should().BeOfType<OkObjectResult>();
        _sync.Verify(s => s.SyncForUserAsync(userId), Times.Once);
        _sync.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Verified_non_transactions_webhook_is_acknowledged_without_sync()
    {
        _verifier.Setup(v => v.VerifyAsync(It.IsAny<string>(), It.IsAny<string>()))
                 .ReturnsAsync(true);
        SetRequest("""{"webhook_type":"ITEM","item_id":"item-1"}""", "valid.jwt.value");

        var result = await _controller.Webhook();

        result.Should().BeOfType<OkObjectResult>();
        _sync.VerifyNoOtherCalls();
    }
}
