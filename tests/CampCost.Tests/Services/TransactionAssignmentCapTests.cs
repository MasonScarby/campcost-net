using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using CampCost.Infrastructure.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampCost.Tests.Services;

/// <summary>
/// Free-plan cap: at most <see cref="FreePlanLimits.MaxAssignedTransactionsPerTrip"/> (50)
/// transactions assigned per trip. The cap fills remaining slots and STOPS — it must never
/// unassign or delete anything to make room. Pro is uncapped. Enforced on both paths:
/// auto-assign (trip create/date change) and manual PATCH assign.
/// </summary>
public class TransactionAssignmentCapTests
{
    private const int Cap = FreePlanLimits.MaxAssignedTransactionsPerTrip;
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _userId = Guid.NewGuid();
    private readonly CampCostDbContext _db;

    public TransactionAssignmentCapTests()
    {
        _db = new CampCostDbContext(new DbContextOptionsBuilder<CampCostDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
    }

    private TransactionAssignmentService Sut(string plan) =>
        new(_db, new FixedPlanService(plan), NullLogger<TransactionAssignmentService>.Instance);

    private Trip SeedTrip()
    {
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            Name = "Trip",
            StartDate = Today.AddDays(-10),
            EndDate = Today,
            Status = "active"
        };
        _db.Trips.Add(trip);
        _db.SaveChanges();
        return trip;
    }

    private void SeedUnassigned(int count, string prefix = "tx")
    {
        for (var i = 0; i < count; i++)
        {
            _db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                PlaidConnectionId = Guid.NewGuid(),
                PlaidTransactionId = $"{prefix}-{i}",
                Amount = 10m,
                TransactionDate = Today.AddDays(-5),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        _db.SaveChanges();
    }

    private void SeedAssigned(Guid tripId, int count, string prefix = "assigned")
    {
        for (var i = 0; i < count; i++)
        {
            _db.Transactions.Add(new Transaction
            {
                Id = Guid.NewGuid(),
                UserId = _userId,
                PlaidConnectionId = Guid.NewGuid(),
                PlaidTransactionId = $"{prefix}-{i}",
                TripId = tripId,
                Amount = 10m,
                TransactionDate = Today.AddDays(-5),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }
        _db.SaveChanges();
    }

    // ── Auto-assign path ────────────────────────────────────────────────────

    [Fact]
    public async Task Free_plan_autoassign_stops_at_50_and_warns()
    {
        var trip = SeedTrip();
        SeedUnassigned(Cap + 10);

        var result = await Sut("free").AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(Cap);
        result.Warning.Should().Be(FreePlanLimits.TransactionCapWarning);
        (await _db.Transactions.CountAsync(t => t.TripId == trip.Id)).Should().Be(Cap);
        // The overflow stays unassigned — nothing is ever dropped or deleted
        (await _db.Transactions.CountAsync(t => t.TripId == null)).Should().Be(10);
    }

    [Fact]
    public async Task Free_plan_autoassign_under_cap_has_no_warning()
    {
        var trip = SeedTrip();
        SeedUnassigned(Cap); // exactly at cap — allowed, no truncation happened

        var result = await Sut("free").AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(Cap);
        result.Warning.Should().BeNull();
    }

    [Fact]
    public async Task Free_plan_autoassign_counts_existing_assignments_toward_cap()
    {
        var trip = SeedTrip();
        SeedAssigned(trip.Id, Cap - 5); // 45 already on the trip → only 5 slots left
        SeedUnassigned(20);

        var result = await Sut("free").AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(5);
        result.Warning.Should().NotBeNull();
        // Existing assignments are untouched — never unassigned to make room
        (await _db.Transactions.CountAsync(t => t.TripId == trip.Id)).Should().Be(Cap);
    }

    [Fact]
    public async Task Free_plan_autoassign_at_cap_assigns_nothing_but_keeps_existing()
    {
        var trip = SeedTrip();
        SeedAssigned(trip.Id, Cap);
        SeedUnassigned(3);

        var result = await Sut("free").AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(0);
        result.Warning.Should().NotBeNull();
        (await _db.Transactions.CountAsync(t => t.TripId == trip.Id)).Should().Be(Cap);
    }

    [Fact]
    public async Task Pro_plan_autoassign_is_uncapped()
    {
        var trip = SeedTrip();
        SeedUnassigned(Cap + 25);

        var result = await Sut("pro").AutoAssignForTripAsync(_userId, trip.Id);

        result.Assigned.Should().Be(Cap + 25);
        result.Warning.Should().BeNull();
    }

    // ── Manual assign path (PATCH /api/transactions/{id}/assign) ───────────

    [Fact]
    public async Task Free_plan_manual_assign_into_full_trip_is_CapReached()
    {
        var trip = SeedTrip();
        SeedAssigned(trip.Id, Cap);
        SeedUnassigned(1, "extra");
        var extra = await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "extra-0");

        var outcome = await Sut("free").AssignAsync(_userId, extra.Id, trip.Id);

        outcome.Should().Be(AssignOutcome.CapReached);
        (await _db.Transactions.SingleAsync(t => t.Id == extra.Id)).TripId.Should().BeNull();
    }

    [Fact]
    public async Task Free_plan_reconfirming_transaction_already_on_full_trip_is_allowed()
    {
        var trip = SeedTrip();
        SeedAssigned(trip.Id, Cap);
        var existing = await _db.Transactions.FirstAsync(t => t.TripId == trip.Id);

        // Re-confirming doesn't grow the count, so the cap must not block it
        var outcome = await Sut("free").AssignAsync(_userId, existing.Id, trip.Id);

        outcome.Should().Be(AssignOutcome.Success);
        (await _db.Transactions.SingleAsync(t => t.Id == existing.Id)).Reviewed.Should().BeTrue();
    }

    [Fact]
    public async Task Free_plan_unassign_is_never_capped()
    {
        var trip = SeedTrip();
        SeedAssigned(trip.Id, Cap);
        var existing = await _db.Transactions.FirstAsync(t => t.TripId == trip.Id);

        var outcome = await Sut("free").AssignAsync(_userId, existing.Id, null);

        outcome.Should().Be(AssignOutcome.Success);
        (await _db.Transactions.SingleAsync(t => t.Id == existing.Id)).TripId.Should().BeNull();
    }

    [Fact]
    public async Task Pro_plan_manual_assign_into_full_trip_succeeds()
    {
        var trip = SeedTrip();
        SeedAssigned(trip.Id, Cap);
        SeedUnassigned(1, "extra");
        var extra = await _db.Transactions.SingleAsync(t => t.PlaidTransactionId == "extra-0");

        var outcome = await Sut("pro").AssignAsync(_userId, extra.Id, trip.Id);

        outcome.Should().Be(AssignOutcome.Success);
        (await _db.Transactions.SingleAsync(t => t.Id == extra.Id)).TripId.Should().Be(trip.Id);
    }
}
