using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using CampCost.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CampCost.Tests.Services;

public class AccountServiceTests
{
    private readonly CampCostDbContext _db = TestHelpers.NewDb();
    private readonly Mock<IPlaidService> _plaid = new();

    public AccountServiceTests()
    {
        _plaid.Setup(p => p.RemoveItemAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    private AccountService Service() =>
        new(_db, _plaid.Object,
            new ConfigurationBuilder().Build(),          // no Supabase keys → auth-user delete skipped
            Mock.Of<IHttpClientFactory>(),
            NullLogger<AccountService>.Instance);

    // Seeds one row in every user-owned table. Returns (itemId, accessToken) of the connection.
    private (string ItemId, string Token) SeedUser(Guid userId, string suffix)
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = userId, Name = "Trip " + suffix, Status = "active" };
        var conn = new PlaidConnection
        {
            Id = Guid.NewGuid(), UserId = userId,
            AccessToken = "token-" + suffix, ItemId = "item-" + suffix, InstitutionName = "Bank"
        };
        _db.Trips.Add(trip);
        _db.PlaidConnections.Add(conn);
        _db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(), UserId = userId, PlaidConnectionId = conn.Id,
            PlaidTransactionId = "ptx-" + suffix, TripId = trip.Id, Amount = 10m,
            TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        });
        _db.Expenses.Add(new Expense
        {
            Id = Guid.NewGuid(), TripId = trip.Id, UserId = userId, Amount = 5m,
            Category = "misc", ExpenseDate = DateOnly.FromDateTime(DateTime.UtcNow),
            Source = "manual", CreatedAt = DateTime.UtcNow
        });
        _db.BudgetCategories.Add(new BudgetCategory
        {
            Id = Guid.NewGuid(), TripId = trip.Id, Category = "fuel", PlannedAmount = 100m, CreatedAt = DateTime.UtcNow
        });
        _db.TripStops.Add(new TripStop
        {
            Id = Guid.NewGuid(), TripId = trip.Id, Location = "Moab", CreatedAt = DateTime.UtcNow
        });
        _db.Subscriptions.Add(new Subscription { Id = Guid.NewGuid(), UserId = userId, Plan = "pro", Status = "active" });
        _db.SaveChanges();
        return (conn.ItemId, conn.AccessToken);
    }

    private int RowsFor(Guid userId)
    {
        var tripIds = _db.Trips.Where(t => t.UserId == userId).Select(t => t.Id).ToList();
        return _db.Trips.Count(t => t.UserId == userId)
             + _db.Transactions.IgnoreQueryFilters().Count(t => t.UserId == userId)
             + _db.Expenses.IgnoreQueryFilters().Count(e => e.UserId == userId)
             + _db.BudgetCategories.Count(b => tripIds.Contains(b.TripId))
             + _db.TripStops.Count(s => tripIds.Contains(s.TripId))
             + _db.PlaidConnections.Count(c => c.UserId == userId)
             + _db.Subscriptions.Count(s => s.UserId == userId);
    }

    [Fact]
    public async Task DeleteAccount_purges_all_of_the_users_data()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        SeedUser(userA, "a");
        SeedUser(userB, "b");
        RowsFor(userA).Should().Be(7);

        await Service().DeleteAccountAsync(userA);

        RowsFor(userA).Should().Be(0);   // everything gone
        RowsFor(userB).Should().Be(7);   // other user untouched
    }

    [Fact]
    public async Task DeleteAccount_removes_the_plaid_item()
    {
        var userA = Guid.NewGuid();
        var (_, token) = SeedUser(userA, "a");

        await Service().DeleteAccountAsync(userA);

        _plaid.Verify(p => p.RemoveItemAsync(token), Times.Once);
    }

    [Fact]
    public async Task Disconnect_removes_connection_and_invalidates_token()
    {
        var userA = Guid.NewGuid();
        var (itemId, token) = SeedUser(userA, "a");

        var result = await Service().DisconnectBankAsync(userA, itemId);

        result.Should().BeTrue();
        _plaid.Verify(p => p.RemoveItemAsync(token), Times.Once);
        _db.PlaidConnections.Any(c => c.UserId == userA).Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_returns_false_for_unknown_item()
    {
        var userA = Guid.NewGuid();
        SeedUser(userA, "a");

        (await Service().DisconnectBankAsync(userA, "does-not-exist")).Should().BeFalse();
    }

    [Fact]
    public async Task Disconnect_cannot_touch_another_users_connection()
    {
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        SeedUser(userA, "a");
        var (itemB, _) = SeedUser(userB, "b");

        // userA tries to disconnect userB's item → not found, userB's connection intact.
        (await Service().DisconnectBankAsync(userA, itemB)).Should().BeFalse();
        _db.PlaidConnections.Any(c => c.UserId == userB).Should().BeTrue();
    }
}
