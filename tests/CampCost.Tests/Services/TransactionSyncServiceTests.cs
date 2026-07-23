using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using CampCost.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CampCost.Tests.Services;

/// <summary>
/// Tests the cursor-based /transactions/sync ingestion into the `transactions` table:
/// added/modified/removed delta handling, dedup on plaid_transaction_id, pending→settled
/// reconciliation, soft-delete, cursor persistence, and — critically — that ingestion is
/// decoupled from trips: every transaction is stored even with no trip, so nothing is lost.
/// IPlaidService is mocked; the DB is EF Core InMemory.
/// </summary>
public class TransactionSyncServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly CampCostDbContext _db;
    private readonly Mock<IPlaidService> _plaid = new();
    private readonly TransactionSyncService _sut;

    public TransactionSyncServiceTests()
    {
        _db = new CampCostDbContext(new DbContextOptionsBuilder<CampCostDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        _sut = new TransactionSyncService(
            _db,
            _plaid.Object,
            new TransactionCategorizationService(),
            new TransactionMatchingService(),
            NullLogger<TransactionSyncService>.Instance);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private PlaidConnection SeedConnection(string? cursor = null)
    {
        var connection = new PlaidConnection
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            AccessToken = "access-token",
            ItemId = "item-1",
            InstitutionName = "Test Bank",
            SyncCursor = cursor,
            CreatedAt = DateTime.UtcNow
        };
        _db.PlaidConnections.Add(connection);
        _db.SaveChanges();
        return connection;
    }

    private Trip SeedTrip(string name = "Active Trip", int startDaysAgo = 10, int endDaysFromNow = 5)
    {
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = name,
            StartDate = Today.AddDays(-startDaysAgo),
            EndDate = Today.AddDays(endDaysFromNow),
            Status = "active"
        };
        _db.Trips.Add(trip);
        _db.SaveChanges();
        return trip;
    }

    private Transaction SeedTransaction(
        Guid connectionId, string plaidId, Guid? tripId, decimal amount = 25m,
        int daysAgo = 1, bool reviewed = false)
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            PlaidConnectionId = connectionId,
            PlaidTransactionId = plaidId,
            TripId = tripId,
            MerchantName = "Seeded Merchant",
            Amount = amount,
            TransactionDate = Today.AddDays(-daysAgo),
            Reviewed = reviewed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Transactions.Add(tx);
        _db.SaveChanges();
        return tx;
    }

    private void SetupDelta(
        IReadOnlyList<PlaidTransaction>? added = null,
        IReadOnlyList<PlaidTransaction>? modified = null,
        IReadOnlyList<string>? removed = null,
        string nextCursor = "cursor-next")
    {
        _plaid
            .Setup(p => p.SyncTransactionsAsync(It.IsAny<string>(), It.IsAny<string?>()))
            .ReturnsAsync(new PlaidSyncDelta(
                added ?? Array.Empty<PlaidTransaction>(),
                modified ?? Array.Empty<PlaidTransaction>(),
                removed ?? Array.Empty<string>(),
                nextCursor));
    }

    private static PlaidTransaction Tx(
        string id, decimal amount = 42m, int daysAgo = 1,
        bool pending = false, string? pendingId = null, string merchant = "Shell") =>
        new(id, merchant, merchant, amount, Today.AddDays(-daysAgo), pending, pendingId);

    // ── no active trips: still stores (no black hole) ─────────────────────────

    [Fact]
    public async Task NoActiveTrips_StillStoresTransactionUnassigned_AndAdvancesCursor()
    {
        var connection = SeedConnection(cursor: null);
        // no trips seeded
        SetupDelta(added: new[] { Tx("tx-1") }, nextCursor: "cursor-1");

        var result = await _sut.SyncForUserAsync(_userId);

        // The data-loss bug is gone: the transaction is stored even with no trip.
        var tx = await _db.Transactions.SingleAsync();
        tx.PlaidTransactionId.Should().Be("tx-1");
        tx.TripId.Should().BeNull("no trip exists to claim it, but it must still be stored");

        // Warning notes nothing-to-assign, NOT nothing-stored.
        result.Warning.Should().Be(TransactionSyncService.NoActiveTripsWarning);
        result.ExpensesUpserted.Should().Be(1);

        // Cursor advances (Plaid was called) — no more permanent skip-past.
        _plaid.Verify(p => p.SyncTransactionsAsync("access-token", null), Times.Once);
        connection.SyncCursor.Should().Be("cursor-1");
    }

    [Fact]
    public async Task ActiveTripsExist_NoWarning()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(added: new[] { Tx("tx-1") });

        var result = await _sut.SyncForUserAsync(_userId);

        result.Warning.Should().BeNull();
    }

    // ── added deltas ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Added_InsertsTransaction_AssignsInWindowTrip_AndPersistsCursor()
    {
        var connection = SeedConnection(cursor: null);
        var trip = SeedTrip();
        SetupDelta(added: new[] { Tx("tx-1", amount: 54.30m) }, nextCursor: "cursor-1");

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        var tx = await _db.Transactions.SingleAsync();
        tx.PlaidTransactionId.Should().Be("tx-1");
        tx.Amount.Should().Be(54.30m);
        tx.TripId.Should().Be(trip.Id, "the date falls in the active trip's window");
        tx.PlaidConnectionId.Should().Be(connection.Id);

        connection.SyncCursor.Should().Be("cursor-1");
        connection.LastSyncAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Added_OutOfWindow_IsStoredUnassigned_NotLost()
    {
        var connection = SeedConnection();
        SeedTrip(startDaysAgo: 5, endDaysFromNow: 5);
        // 30 days ago — outside the trip window. Under the OLD model this was skipped and
        // lost past the cursor. Now it must be stored, just unassigned.
        SetupDelta(added: new[] { Tx("tx-old", daysAgo: 30) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        var tx = await _db.Transactions.SingleAsync();
        tx.PlaidTransactionId.Should().Be("tx-old");
        tx.TripId.Should().BeNull();
    }

    [Fact]
    public async Task Added_DuplicatePlaidTransactionId_IsSkippedNotDuplicated()
    {
        var connection = SeedConnection();
        var trip = SeedTrip();
        SeedTransaction(connection.Id, "tx-1", trip.Id, amount: 25m);
        SetupDelta(added: new[] { Tx("tx-1", amount: 25m) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        result.ExpensesSkipped.Should().Be(1);
        (await _db.Transactions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Added_PendingTransaction_IsImported()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(added: new[] { Tx("pending-1", pending: true) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        var tx = await _db.Transactions.SingleAsync();
        tx.PlaidTransactionId.Should().Be("pending-1");
        tx.Pending.Should().BeTrue();
    }

    [Fact]
    public async Task Added_CreditTransaction_IsSkipped()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(added: new[] { Tx("tx-credit", amount: -50m) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        (await _db.Transactions.CountAsync()).Should().Be(0);
    }

    // ── pending → settled reconciliation ─────────────────────────────────────

    [Fact]
    public async Task Added_SettledVersionOfImportedPending_UpdatesRowInPlace_NeverBothVersions()
    {
        var connection = SeedConnection();
        var trip = SeedTrip();
        SeedTransaction(connection.Id, "pending-1", trip.Id, amount: 20.00m);

        // Plaid settles the pending tx: `added` carries a NEW id + pending_transaction_id,
        // and the old pending id arrives in `removed` — all in the same delta set.
        SetupDelta(
            added: new[] { Tx("settled-1", amount: 21.37m, pendingId: "pending-1") },
            removed: new[] { "pending-1" });

        var result = await _sut.SyncForUserAsync(_userId);

        // Exactly one row: the original transaction updated in place with the settled ID
        var tx = await _db.Transactions.IgnoreQueryFilters().SingleAsync();
        tx.PlaidTransactionId.Should().Be("settled-1");
        tx.Amount.Should().Be(21.37m);
        tx.TripId.Should().Be(trip.Id, "the trip assignment must survive the ID swap");
        tx.DeletedAt.Should().BeNull("the settled row must not be soft-deleted by the removed delta");
        result.ExpensesUpserted.Should().Be(1);
    }

    [Fact]
    public async Task Added_SettledVersion_WhenPendingWasNeverImported_InsertsNormally()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(
            added: new[] { Tx("settled-1", pendingId: "pending-unknown") },
            removed: new[] { "pending-unknown" });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        (await _db.Transactions.SingleAsync()).PlaidTransactionId.Should().Be("settled-1");
    }

    // ── modified deltas ──────────────────────────────────────────────────────

    [Fact]
    public async Task Modified_UpdatesExistingRowInPlace()
    {
        var connection = SeedConnection();
        var trip = SeedTrip();
        var seeded = SeedTransaction(connection.Id, "tx-1", trip.Id, amount: 10.00m);

        SetupDelta(modified: new[] { Tx("tx-1", amount: 12.50m, merchant: "Pilot") });

        var result = await _sut.SyncForUserAsync(_userId);

        (await _db.Transactions.CountAsync()).Should().Be(1, "modified must update, never insert a second copy");
        var tx = await _db.Transactions.SingleAsync();
        tx.Id.Should().Be(seeded.Id);
        tx.Amount.Should().Be(12.50m);
        tx.MerchantName.Should().Be("Pilot");
        result.ExpensesUpserted.Should().Be(1);
    }

    [Fact]
    public async Task Modified_UnknownTransaction_IsInsertedAsNew()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(modified: new[] { Tx("tx-never-seen") });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        (await _db.Transactions.SingleAsync()).PlaidTransactionId.Should().Be("tx-never-seen");
    }

    // ── removed deltas ───────────────────────────────────────────────────────

    [Fact]
    public async Task Removed_SoftDeletes_NeverHardDeletes()
    {
        var connection = SeedConnection();
        var trip = SeedTrip();
        SeedTransaction(connection.Id, "tx-1", trip.Id);

        SetupDelta(removed: new[] { "tx-1" });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesRemoved.Should().Be(1);

        // Row still exists (financial history is never hard-deleted) …
        var row = await _db.Transactions.IgnoreQueryFilters().SingleAsync();
        row.DeletedAt.Should().NotBeNull();

        // … but is hidden from all normal queries (budgets, trip views)
        (await _db.Transactions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Removed_ThenReAdded_IsNotReimported()
    {
        // A soft-deleted row must still block re-import: dedup lookups ignore the
        // query filter, so the removed transaction can't sneak back in.
        var connection = SeedConnection();
        var trip = SeedTrip();
        var seeded = SeedTransaction(connection.Id, "tx-1", trip.Id);
        seeded.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        SetupDelta(added: new[] { Tx("tx-1") });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        (await _db.Transactions.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Removed_UnknownTransaction_IsIgnored()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(removed: new[] { "tx-unknown" });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesRemoved.Should().Be(0);
    }

    // ── cursor handling ──────────────────────────────────────────────────────

    [Fact]
    public async Task Sync_PassesStoredCursorToPlaid()
    {
        SeedConnection(cursor: "cursor-previous");
        SeedTrip();
        SetupDelta(nextCursor: "cursor-new");

        await _sut.SyncForUserAsync(_userId);

        _plaid.Verify(p => p.SyncTransactionsAsync("access-token", "cursor-previous"), Times.Once);
    }

    // ── trip-specific sync claims existing rows, respects reviewed ────────────

    [Fact]
    public async Task SyncForTrip_ClaimsUnassignedInWindow_ButNotReviewedRows()
    {
        var connection = SeedConnection(cursor: "c");
        var trip = SeedTrip();
        // Unassigned, in-window → should be claimed
        SeedTransaction(connection.Id, "unassigned-1", tripId: null, daysAgo: 1);
        // Reviewed, on a different (null) assignment but user-confirmed → must NOT move
        SeedTransaction(connection.Id, "reviewed-1", tripId: null, daysAgo: 1, reviewed: true);
        SetupDelta();

        await _sut.SyncForTripAsync(_userId, trip.Id);

        var claimed = await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "unassigned-1");
        claimed.TripId.Should().Be(trip.Id);

        var reviewed = await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "reviewed-1");
        reviewed.TripId.Should().BeNull("reviewed rows are user-confirmed and never auto-moved");
    }

    // ── cross-reconnect dedup (disconnect → reconnect issues new Item + new IDs) ──

    /// <summary>Seeds a DETACHED transaction (PlaidConnectionId == null) — the orphaned
    /// history left behind when a bank is disconnected. Name == MerchantName == merchant so
    /// it lines up with the Tx() delta helper's content.</summary>
    private void SeedDetached(string plaidId, decimal amount, string merchant, int daysAgo,
        Guid? tripId = null, bool reviewed = false)
    {
        _db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            PlaidConnectionId = null, // detached: the disconnected bank's orphaned history
            PlaidTransactionId = plaidId,
            TripId = tripId,
            Name = merchant,
            MerchantName = merchant,
            Amount = amount,
            TransactionDate = Today.AddDays(-daysAgo),
            Reviewed = reviewed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Reconnect_ReclaimsOrphanedTwin_NoDuplicate_PreservesTripAndReview()
    {
        var trip = SeedTrip();
        // Orphan from the OLD (disconnected) connection: assigned to a trip + user-reviewed.
        SeedDetached("old-id", amount: 42m, merchant: "Shell", daysAgo: 1,
            tripId: trip.Id, reviewed: true);

        // Reconnect: a NEW connection re-imports the same purchase under a NEW Plaid ID.
        var connection = SeedConnection(cursor: null);
        SetupDelta(added: new[] { Tx("new-id", amount: 42m, daysAgo: 1, merchant: "Shell") });

        await _sut.SyncForUserAsync(_userId);

        // Exactly ONE row survives — no duplicate.
        (await _db.Transactions.CountAsync()).Should().Be(1);
        var row = await _db.Transactions.SingleAsync();
        row.PlaidTransactionId.Should().Be("new-id", "orphan adopts the reconnect's Plaid ID");
        row.PlaidConnectionId.Should().Be(connection.Id, "orphan is re-attached to the live connection");
        row.TripId.Should().Be(trip.Id, "trip assignment is preserved across reconnect");
        row.Reviewed.Should().BeTrue("user-confirmed review state is preserved");
    }

    [Fact]
    public async Task SameDayMultiples_WithinOneConnection_AreNeverMerged()
    {
        var connection = SeedConnection(cursor: null);
        // Two genuinely distinct purchases: same place, same day, same price, different IDs.
        SetupDelta(added: new[]
        {
            Tx("coffee-1", amount: 6m, daysAgo: 1, merchant: "Coffee Hut"),
            Tx("coffee-2", amount: 6m, daysAgo: 1, merchant: "Coffee Hut"),
        });

        await _sut.SyncForUserAsync(_userId);

        // Both kept — reclaim only targets DETACHED rows, never same-connection siblings.
        (await _db.Transactions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Reconnect_WithSameDayMultiples_ReclaimsCountForCount()
    {
        // Old connection had TWO identical same-day purchases (both real).
        SeedDetached("old-1", amount: 6m, merchant: "Coffee Hut", daysAgo: 1);
        SeedDetached("old-2", amount: 6m, merchant: "Coffee Hut", daysAgo: 1);

        var connection = SeedConnection(cursor: null);
        // Reconnect re-imports BOTH under new IDs.
        SetupDelta(added: new[]
        {
            Tx("new-1", amount: 6m, daysAgo: 1, merchant: "Coffee Hut"),
            Tx("new-2", amount: 6m, daysAgo: 1, merchant: "Coffee Hut"),
        });

        await _sut.SyncForUserAsync(_userId);

        // Exactly TWO survive (not 4, not 1): each orphan reclaimed once, multiplicity intact.
        (await _db.Transactions.CountAsync()).Should().Be(2);
        (await _db.Transactions.Where(t => t.PlaidConnectionId == connection.Id).CountAsync())
            .Should().Be(2, "both reclaimed rows now belong to the live connection");
    }

    [Fact]
    public async Task Reconnect_ExtraNewPurchase_BeyondOrphans_InsertsNormally()
    {
        // One orphan, but the reconnect window has TWO same-content purchases (one is new).
        SeedDetached("old-1", amount: 6m, merchant: "Coffee Hut", daysAgo: 1);

        var connection = SeedConnection(cursor: null);
        SetupDelta(added: new[]
        {
            Tx("new-1", amount: 6m, daysAgo: 1, merchant: "Coffee Hut"),
            Tx("new-2", amount: 6m, daysAgo: 1, merchant: "Coffee Hut"),
        });

        await _sut.SyncForUserAsync(_userId);

        // Orphan reclaimed once; the extra genuinely-new purchase inserts → TWO rows, no loss.
        (await _db.Transactions.CountAsync()).Should().Be(2);
    }
}
