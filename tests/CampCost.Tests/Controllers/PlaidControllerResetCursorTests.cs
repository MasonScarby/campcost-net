using System.Security.Claims;
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
/// POST /api/plaid/reset-cursor clears the stored Plaid sync cursor so the next sync
/// re-pulls full history. It must:
///   - only touch the authenticated user's connections (never another user's)
///   - never delete stored transactions (it only nulls the cursor)
/// </summary>
public class PlaidControllerResetCursorTests
{
    private readonly CampCostDbContext _db;
    private readonly PlaidController _controller;

    public PlaidControllerResetCursorTests()
    {
        _db = new CampCostDbContext(new DbContextOptionsBuilder<CampCostDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _controller = new PlaidController(
            Mock.Of<IPlaidService>(),
            Mock.Of<ITransactionSyncService>(),
            Mock.Of<IPlaidWebhookVerifier>(),
            _db,
            NullLogger<PlaidController>.Instance);
    }

    private void AuthenticateAs(Guid userId)
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        }, "test");
        _controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
    }

    private Guid SeedConnection(Guid userId, string cursor)
    {
        var id = Guid.NewGuid();
        _db.PlaidConnections.Add(new PlaidConnection
        {
            Id = id,
            UserId = userId,
            AccessToken = "access-token",
            ItemId = $"item-{id}",
            InstitutionName = "Test Bank",
            SyncCursor = cursor,
            CreatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
        return id;
    }

    [Fact]
    public async Task ResetCursor_clears_cursor_only_for_the_authenticated_user()
    {
        var me = Guid.NewGuid();
        var other = Guid.NewGuid();
        var myConnId = SeedConnection(me, "my-cursor");
        var otherConnId = SeedConnection(other, "other-cursor");
        AuthenticateAs(me);

        var result = await _controller.ResetCursor();

        result.Should().BeOfType<OkObjectResult>();
        var mine = await _db.PlaidConnections.AsNoTracking().FirstAsync(c => c.Id == myConnId);
        var theirs = await _db.PlaidConnections.AsNoTracking().FirstAsync(c => c.Id == otherConnId);
        mine.SyncCursor.Should().BeNull();
        theirs.SyncCursor.Should().Be("other-cursor"); // untouched
    }

    [Fact]
    public async Task ResetCursor_does_not_delete_stored_transactions()
    {
        var me = Guid.NewGuid();
        var connId = SeedConnection(me, "my-cursor");
        _db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = me,
            PlaidConnectionId = connId,
            PlaidTransactionId = "txn-1",
            Amount = 42m,
            TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Category = "misc",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
        AuthenticateAs(me);

        await _controller.ResetCursor();

        var count = await _db.Transactions.CountAsync(t => t.UserId == me);
        count.Should().Be(1); // source of truth preserved
    }
}
