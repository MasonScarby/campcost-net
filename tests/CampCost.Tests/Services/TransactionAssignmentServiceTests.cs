using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using CampCost.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampCost.Tests.Services;

/// <summary>
/// Assignment is decoupled from ingestion: transactions land unassigned and are claimed
/// later — automatically when a trip's date range covers them, or manually by the user.
/// </summary>
public class TransactionAssignmentServiceTests
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly CampCostDbContext _db;
    private readonly TransactionAssignmentService _sut;

    public TransactionAssignmentServiceTests()
    {
        _db = new CampCostDbContext(new DbContextOptionsBuilder<CampCostDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        // Pro plan here so these existing (non-cap) tests aren't affected by the free-plan cap.
        _sut = new TransactionAssignmentService(
            _db, new FixedPlanService("pro"), NullLogger<TransactionAssignmentService>.Instance);
    }

    private Trip SeedTrip(int startDaysAgo = 10, int endDaysAgo = 0)
    {
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = "Trip",
            StartDate = Today.AddDays(-startDaysAgo),
            EndDate = Today.AddDays(-endDaysAgo),
            Status = "active"
        };
        _db.Trips.Add(trip);
        _db.SaveChanges();
        return trip;
    }

    private Transaction SeedTx(string plaidId, Guid? tripId, int daysAgo, bool reviewed = false)
    {
        var tx = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            PlaidConnectionId = Guid.NewGuid(),
            PlaidTransactionId = plaidId,
            TripId = tripId,
            Amount = 10m,
            TransactionDate = Today.AddDays(-daysAgo),
            Reviewed = reviewed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Transactions.Add(tx);
        _db.SaveChanges();
        return tx;
    }

    [Fact]
    public async Task AutoAssign_ClaimsUnassignedInRange_LeavesOutOfRangeAndOtherTrips()
    {
        var trip = SeedTrip(startDaysAgo: 10, endDaysAgo: 0);
        SeedTx("in-range", tripId: null, daysAgo: 5);       // claimed
        SeedTx("out-of-range", tripId: null, daysAgo: 40);  // left alone
        var otherTrip = Guid.NewGuid();
        SeedTx("other-trip", tripId: otherTrip, daysAgo: 5); // never stolen

        var result = await _sut.AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(1);
        (await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "in-range")).TripId.Should().Be(trip.Id);
        (await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "out-of-range")).TripId.Should().BeNull();
        (await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "other-trip")).TripId.Should().Be(otherTrip);
    }

    [Fact]
    public async Task AutoAssign_DoesNotReassignReviewedTransactions()
    {
        var trip = SeedTrip(startDaysAgo: 10, endDaysAgo: 0);
        SeedTx("reviewed-unassigned", tripId: null, daysAgo: 5, reviewed: true);

        var result = await _sut.AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(0);
        (await _db.Transactions.SingleAsync()).TripId.Should().BeNull("reviewed = user-confirmed, never auto-moved");
    }

    [Fact]
    public async Task AutoAssign_TripWithoutDates_IsNoOp()
    {
        var trip = new Trip { Id = Guid.NewGuid(), UserId = _userId, Name = "No dates", Status = "planning" };
        _db.Trips.Add(trip);
        await _db.SaveChangesAsync();
        SeedTx("tx", tripId: null, daysAgo: 5);

        var result = await _sut.AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(0);
    }

    [Fact]
    public async Task ManualAssign_SetsTrip_AndMarksReviewed()
    {
        var trip = SeedTrip();
        var tx = SeedTx("tx", tripId: null, daysAgo: 5);

        var outcome = await _sut.AssignAsync(_userId, tx.Id, trip.Id);

        outcome.Should().Be(AssignOutcome.Success);
        var updated = await _db.Transactions.SingleAsync();
        updated.TripId.Should().Be(trip.Id);
        updated.Reviewed.Should().BeTrue();
    }

    [Fact]
    public async Task ManualUnassign_ClearsTrip()
    {
        var trip = SeedTrip();
        var tx = SeedTx("tx", tripId: trip.Id, daysAgo: 5);

        var outcome = await _sut.AssignAsync(_userId, tx.Id, null);

        outcome.Should().Be(AssignOutcome.Success);
        (await _db.Transactions.SingleAsync()).TripId.Should().BeNull();
    }

    [Fact]
    public async Task ManualAssign_ForeignUser_Fails()
    {
        var trip = SeedTrip();
        var tx = SeedTx("tx", tripId: null, daysAgo: 5);

        var outcome = await _sut.AssignAsync(Guid.NewGuid(), tx.Id, trip.Id);

        outcome.Should().Be(AssignOutcome.NotFound);
    }

    [Fact]
    public async Task ManualAssign_WithCategory_AppliesUserCorrection()
    {
        var trip = SeedTrip();
        var tx = SeedTx("recategorize", tripId: null, daysAgo: 5);

        var outcome = await _sut.AssignAsync(_userId, tx.Id, trip.Id, category: "fuel");

        outcome.Should().Be(AssignOutcome.Success);
        var updated = await _db.Transactions.SingleAsync();
        updated.Category.Should().Be("fuel");
        updated.Reviewed.Should().BeTrue();
    }

    [Fact]
    public async Task ManualAssign_UnknownCategory_RejectsWithoutSideEffects()
    {
        var trip = SeedTrip();
        var tx = SeedTx("bad-category", tripId: null, daysAgo: 5);
        var originalCategory = tx.Category;

        var outcome = await _sut.AssignAsync(_userId, tx.Id, trip.Id, category: "not_a_category");

        outcome.Should().Be(AssignOutcome.InvalidCategory);
        // Nothing half-applied: category, assignment, and reviewed all untouched.
        var unchanged = await _db.Transactions.SingleAsync();
        unchanged.Category.Should().Be(originalCategory);
        unchanged.TripId.Should().BeNull();
        unchanged.Reviewed.Should().BeFalse();
    }

    [Fact]
    public async Task Adjust_SetsOverlay_NeverTouchesRawAmount()
    {
        var tx = SeedTx("split-me", tripId: null, daysAgo: 5); // Amount = 10m

        var outcome = await _sut.AdjustAsync(_userId, tx.Id, 5m, "Split 50/50");

        outcome.Should().Be(AssignOutcome.Success);
        var updated = await _db.Transactions.SingleAsync();
        updated.AdjustedAmount.Should().Be(5m);
        updated.AdjustmentNote.Should().Be("Split 50/50");
        updated.Amount.Should().Be(10m); // raw Plaid amount is immutable
    }

    [Fact]
    public async Task Adjust_NullAmount_ClearsOverlay()
    {
        var tx = SeedTx("clear-me", tripId: null, daysAgo: 5);
        await _sut.AdjustAsync(_userId, tx.Id, 5m, "Split 50/50");

        var outcome = await _sut.AdjustAsync(_userId, tx.Id, null, null);

        outcome.Should().Be(AssignOutcome.Success);
        var updated = await _db.Transactions.SingleAsync();
        updated.AdjustedAmount.Should().BeNull();
        updated.AdjustmentNote.Should().BeNull();
    }

    [Fact]
    public async Task Adjust_NegativeAmount_Rejected()
    {
        var tx = SeedTx("negative", tripId: null, daysAgo: 5);

        var outcome = await _sut.AdjustAsync(_userId, tx.Id, -1m, null);

        outcome.Should().Be(AssignOutcome.InvalidAdjustment);
        (await _db.Transactions.SingleAsync()).AdjustedAmount.Should().BeNull();
    }

    [Fact]
    public async Task Adjust_ForeignUser_NotFound()
    {
        var tx = SeedTx("foreign", tripId: null, daysAgo: 5);

        var outcome = await _sut.AdjustAsync(Guid.NewGuid(), tx.Id, 5m, null);

        outcome.Should().Be(AssignOutcome.NotFound);
    }

    [Fact]
    public async Task GetUnassigned_ReturnsOnlyNullTripNonDeleted()
    {
        SeedTx("unassigned", tripId: null, daysAgo: 1);
        SeedTx("assigned", tripId: Guid.NewGuid(), daysAgo: 1);
        var deleted = SeedTx("deleted", tripId: null, daysAgo: 1);
        deleted.DeletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var result = await _sut.GetUnassignedAsync(_userId);

        result.Should().ContainSingle(t => t.PlaidTransactionId == "unassigned");
    }
}
