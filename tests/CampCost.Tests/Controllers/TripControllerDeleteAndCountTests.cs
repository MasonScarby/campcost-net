using CampCost.Api.Controllers;
using CampCost.Core.Entities;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace CampCost.Tests.Controllers;

/// <summary>
/// DELETE /api/trips/{id} must RELEASE the trip's transactions back to the pool
/// (trip_id -> null, reviewed -> false) so a new same-range trip auto-claims them,
/// and the free-plan trip-COUNT cap must be enforced server-side on create.
/// </summary>
public class TripControllerDeleteAndCountTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 7, 1);

    private static TripController NewController(string plan, out CampCostDbContext db)
    {
        db = TestHelpers.NewDb();
        var controller = new TripController(
            db, new BudgetCalculationService(), new NoopAssignmentService(), new FixedPlanService(plan),
            NullLogger<TripController>.Instance);
        controller.SetUser(UserId);
        return controller;
    }

    private static TripWriteRequest Request(DateOnly? start, DateOnly? end) =>
        new("Test trip", null, start, end, null, null, null, null);

    [Fact]
    public async Task Deleting_a_trip_releases_its_transactions_unassigned_and_unreviewed()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            var tripId = Guid.NewGuid();
            db.Trips.Add(new Trip { Id = tripId, UserId = UserId, Name = "T", StartDate = Start, EndDate = Start.AddDays(3), CreatedAt = DateTime.UtcNow });
            var txId = Guid.NewGuid();
            db.Transactions.Add(new Transaction
            {
                Id = txId, UserId = UserId, PlaidTransactionId = "ptx-1", TripId = tripId,
                Reviewed = true, Amount = 10m, TransactionDate = Start.AddDays(1),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var result = await controller.DeleteTrip(tripId);

            result.Should().BeOfType<OkObjectResult>();
            (await db.Trips.AnyAsync(t => t.Id == tripId)).Should().BeFalse();
            var tx = await db.Transactions.SingleAsync(t => t.Id == txId);
            tx.TripId.Should().BeNull("delete must unassign the transaction");
            tx.Reviewed.Should().BeFalse("delete must clear the review flag so a new trip re-claims it");
        }
    }

    [Fact]
    public async Task Deleting_someone_elses_trip_returns_404_and_touches_nothing()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            var otherTrip = Guid.NewGuid();
            db.Trips.Add(new Trip { Id = otherTrip, UserId = Guid.NewGuid(), Name = "X", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var result = await controller.DeleteTrip(otherTrip);

            result.Should().BeOfType<NotFoundResult>();
            (await db.Trips.AnyAsync(t => t.Id == otherTrip)).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Free_plan_blocks_creating_a_4th_trip_with_402_upgrade_required()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            for (var i = 0; i < FreePlanLimits.MaxTripsFree; i++)
                db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = UserId, Name = $"T{i}", StartDate = Start, EndDate = Start, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var result = await controller.CreateTrip(Request(Start, Start)); // 4th, 1-day (duration ok)

            result.Should().BeOfType<ObjectResult>()
                .Which.StatusCode.Should().Be(StatusCodes.Status402PaymentRequired);
            (await db.Trips.CountAsync(t => t.UserId == UserId)).Should().Be(FreePlanLimits.MaxTripsFree, "nothing new is written when rejected");
        }
    }

    [Fact]
    public async Task Pro_plan_can_create_beyond_the_free_trip_cap()
    {
        var controller = NewController("pro", out var db);
        using (db)
        {
            for (var i = 0; i < FreePlanLimits.MaxTripsFree; i++)
                db.Trips.Add(new Trip { Id = Guid.NewGuid(), UserId = UserId, Name = $"T{i}", StartDate = Start, EndDate = Start, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            var result = await controller.CreateTrip(Request(Start, Start.AddDays(30)));

            result.Should().BeOfType<OkObjectResult>();
            (await db.Trips.CountAsync(t => t.UserId == UserId)).Should().Be(FreePlanLimits.MaxTripsFree + 1);
        }
    }
}
