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
/// Tests the cursor-based /transactions/sync ingestion: added/modified/removed delta
/// handling, dedup on plaid_transaction_id, pending→settled reconciliation, soft-delete,
/// cursor persistence, and the no-active-trips warning.
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

    private Expense SeedExpense(Trip trip, string plaidId, decimal amount = 25m)
    {
        var expense = new Expense
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            TripId = trip.Id,
            PlaidTransactionId = plaidId,
            MerchantName = "Seeded Merchant",
            Amount = amount,
            ExpenseDate = Today.AddDays(-1),
            Source = "plaid",
            CreatedAt = DateTime.UtcNow
        };
        _db.Expenses.Add(expense);
        _db.SaveChanges();
        return expense;
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

    // ── no active trips ──────────────────────────────────────────────────────

    [Fact]
    public async Task NoActiveTrips_ReturnsWarning_AndDoesNotAdvanceCursor()
    {
        var connection = SeedConnection(cursor: null);
        // no trips seeded

        var result = await _sut.SyncForUserAsync(_userId);

        result.Warning.Should().Be(TransactionSyncService.NoActiveTripsWarning);
        result.ExpensesUpserted.Should().Be(0);

        // Plaid must not be called: advancing the cursor with nothing to match
        // against would permanently drop those transactions.
        _plaid.Verify(p => p.SyncTransactionsAsync(It.IsAny<string>(), It.IsAny<string?>()), Times.Never);
        connection.SyncCursor.Should().BeNull();
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
    public async Task Added_InsertsExpense_AndPersistsCursor()
    {
        var connection = SeedConnection(cursor: null);
        var trip = SeedTrip();
        SetupDelta(added: new[] { Tx("tx-1", amount: 54.30m) }, nextCursor: "cursor-1");

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        var expense = await _db.Expenses.SingleAsync();
        expense.PlaidTransactionId.Should().Be("tx-1");
        expense.Amount.Should().Be(54.30m);
        expense.TripId.Should().Be(trip.Id);
        expense.Source.Should().Be("plaid");

        connection.SyncCursor.Should().Be("cursor-1");
        connection.LastSyncAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Added_DuplicatePlaidTransactionId_IsSkippedNotDuplicated()
    {
        SeedConnection();
        var trip = SeedTrip();
        SeedExpense(trip, "tx-1", amount: 25m);
        SetupDelta(added: new[] { Tx("tx-1", amount: 25m) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        result.ExpensesSkipped.Should().Be(1);
        (await _db.Expenses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Added_PendingTransaction_IsImported()
    {
        // The skip-all-pending workaround is gone: /transactions/sync reconciles the
        // pending→settled ID swap itself, so pending transactions are imported.
        SeedConnection();
        SeedTrip();
        SetupDelta(added: new[] { Tx("pending-1", pending: true) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        (await _db.Expenses.SingleAsync()).PlaidTransactionId.Should().Be("pending-1");
    }

    [Fact]
    public async Task Added_CreditTransaction_IsSkipped()
    {
        SeedConnection();
        SeedTrip();
        SetupDelta(added: new[] { Tx("tx-credit", amount: -50m) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        (await _db.Expenses.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Added_NoMatchingTripWindow_IsSkipped()
    {
        SeedConnection();
        SeedTrip(startDaysAgo: 5, endDaysFromNow: 5);
        // 30 days ago — far outside the trip window (+2-day buffer); must be skipped,
        // never assigned to an arbitrary trip (the old trips[0] bug).
        SetupDelta(added: new[] { Tx("tx-old", daysAgo: 30) });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        (await _db.Expenses.CountAsync()).Should().Be(0);
    }

    // ── pending → settled reconciliation ─────────────────────────────────────

    [Fact]
    public async Task Added_SettledVersionOfImportedPending_UpdatesRowInPlace_NeverBothVersions()
    {
        SeedConnection();
        var trip = SeedTrip();
        SeedExpense(trip, "pending-1", amount: 20.00m);

        // Plaid settles the pending tx: `added` carries a NEW id + pending_transaction_id,
        // and the old pending id arrives in `removed` — all in the same delta set.
        SetupDelta(
            added: new[] { Tx("settled-1", amount: 21.37m, pendingId: "pending-1") },
            removed: new[] { "pending-1" });

        var result = await _sut.SyncForUserAsync(_userId);

        // Exactly one row: the original expense updated in place with the settled ID
        var expense = await _db.Expenses.IgnoreQueryFilters().SingleAsync();
        expense.PlaidTransactionId.Should().Be("settled-1");
        expense.Amount.Should().Be(21.37m);
        expense.DeletedAt.Should().BeNull("the settled row must not be soft-deleted by the removed delta");
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
        (await _db.Expenses.SingleAsync()).PlaidTransactionId.Should().Be("settled-1");
    }

    // ── modified deltas ──────────────────────────────────────────────────────

    [Fact]
    public async Task Modified_UpdatesExistingRowInPlace()
    {
        SeedConnection();
        var trip = SeedTrip();
        var seeded = SeedExpense(trip, "tx-1", amount: 10.00m);

        SetupDelta(modified: new[] { Tx("tx-1", amount: 12.50m, merchant: "Pilot") });

        var result = await _sut.SyncForUserAsync(_userId);

        (await _db.Expenses.CountAsync()).Should().Be(1, "modified must update, never insert a second copy");
        var expense = await _db.Expenses.SingleAsync();
        expense.Id.Should().Be(seeded.Id);
        expense.Amount.Should().Be(12.50m);
        expense.MerchantName.Should().Be("Pilot");
        result.ExpensesUpserted.Should().Be(1);
    }

    [Fact]
    public async Task Modified_UnknownTransaction_IsInsertedAsNew()
    {
        // A modified delta for a tx we never imported (e.g. skipped as pending under
        // the old /transactions/get flow) is treated as added.
        SeedConnection();
        SeedTrip();
        SetupDelta(modified: new[] { Tx("tx-never-seen") });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(1);
        (await _db.Expenses.SingleAsync()).PlaidTransactionId.Should().Be("tx-never-seen");
    }

    // ── removed deltas ───────────────────────────────────────────────────────

    [Fact]
    public async Task Removed_SoftDeletes_NeverHardDeletes()
    {
        SeedConnection();
        var trip = SeedTrip();
        SeedExpense(trip, "tx-1");

        SetupDelta(removed: new[] { "tx-1" });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesRemoved.Should().Be(1);

        // Row still exists (financial history is never hard-deleted) …
        var row = await _db.Expenses.IgnoreQueryFilters().SingleAsync();
        row.DeletedAt.Should().NotBeNull();

        // … but is hidden from all normal queries (budgets, trip views)
        (await _db.Expenses.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Removed_ThenReAdded_IsNotReimported()
    {
        // A soft-deleted row must still block re-import: dedup lookups ignore the
        // query filter, so the removed transaction can't sneak back in.
        SeedConnection();
        var trip = SeedTrip();
        var seeded = SeedExpense(trip, "tx-1");
        seeded.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        SetupDelta(added: new[] { Tx("tx-1") });

        var result = await _sut.SyncForUserAsync(_userId);

        result.ExpensesUpserted.Should().Be(0);
        (await _db.Expenses.IgnoreQueryFilters().CountAsync()).Should().Be(1);
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
}
