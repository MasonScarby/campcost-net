using CampCost.Api.Controllers;
using CampCost.Core.Entities;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;

namespace CampCost.Tests.Controllers;

/// <summary>
/// Server-side 7-day trip gate on POST /api/trips and PUT /api/trips/{id}.
/// Free plan trips longer than 7 days must be rejected with 402 Payment Required
/// and code "upgrade_required" — and nothing may be written to the DB when rejected.
/// </summary>
public class TripControllerPlanGateTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 7, 1);

    private static TripController NewController(string plan, out CampCostDbContext db)
    {
        db = TestHelpers.NewDb();
        var controller = new TripController(
            db, new BudgetCalculationService(), new NoopAssignmentService(), new FixedPlanService(plan));
        controller.SetUser(UserId);
        return controller;
    }

    private static TripWriteRequest Request(DateOnly? start, DateOnly? end) =>
        new("Test trip", null, start, end, null, null, null, null);

    [Fact]
    public async Task Free_plan_trip_over_7_days_returns_402_with_upgrade_code()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            // 8 days inclusive
            var result = await controller.CreateTrip(Request(Start, Start.AddDays(7)));

            var status = result.Should().BeOfType<ObjectResult>().Subject;
            status.StatusCode.Should().Be(402);
            status.Value!.ToString().Should().Contain("upgrade_required");
            db.Trips.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Free_plan_trip_of_exactly_7_days_is_allowed()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            // 7 days inclusive — boundary allowed
            var result = await controller.CreateTrip(Request(Start, Start.AddDays(6)));

            result.Should().BeOfType<OkObjectResult>();
            db.Trips.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task Pro_plan_long_trip_is_allowed()
    {
        var controller = NewController("pro", out var db);
        using (db)
        {
            var result = await controller.CreateTrip(Request(Start, Start.AddDays(729)));

            result.Should().BeOfType<OkObjectResult>();
            db.Trips.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task Free_plan_cannot_stretch_existing_trip_past_7_days_via_update()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            var trip = new Trip
            {
                Id = Guid.NewGuid(),
                UserId = UserId,
                Name = "Short trip",
                StartDate = Start,
                EndDate = Start.AddDays(3),
                Status = "planning",
                CreatedAt = DateTime.UtcNow
            };
            db.Trips.Add(trip);
            await db.SaveChangesAsync();

            // Only end_date sent — the gate must evaluate the RESULTING range,
            // not just the fields present in the request body.
            var result = await controller.UpdateTrip(trip.Id,
                new TripWriteRequest(null, null, null, Start.AddDays(30), null, null, null, null));

            var status = result.Should().BeOfType<ObjectResult>().Subject;
            status.StatusCode.Should().Be(402);

            // Unchanged in DB
            (await db.Trips.FindAsync(trip.Id))!.EndDate.Should().Be(Start.AddDays(3));
        }
    }

    [Fact]
    public async Task Create_with_budgets_persists_categories_and_sums_total()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            var req = new TripWriteRequest(
                "Budgeted trip", null, Start, Start.AddDays(4), null, 120m, null,
                new Dictionary<string, decimal> { ["fuel"] = 200m, ["dump_station"] = 20m });

            var result = await controller.CreateTrip(req);

            result.Should().BeOfType<OkObjectResult>();
            var trip = db.Trips.Single();
            trip.TotalBudget.Should().Be(220m); // derived from budgets, not sent separately
            trip.TotalMiles.Should().Be(120m);
            db.BudgetCategories.Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task Create_with_unknown_budget_category_is_rejected_400()
    {
        var controller = NewController("free", out var db);
        using (db)
        {
            var req = new TripWriteRequest(
                "Bad category", null, Start, Start.AddDays(1), null, null, null,
                new Dictionary<string, decimal> { ["jetski"] = 500m });

            var result = await controller.CreateTrip(req);

            result.Should().BeOfType<BadRequestObjectResult>();
            db.Trips.Should().BeEmpty();
        }
    }
}
