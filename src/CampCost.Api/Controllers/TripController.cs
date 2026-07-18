using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CampCost.Api.Controllers;

[ApiController]
[Route("api/trips")]
[Authorize]
public class TripController : ControllerBase
{
    private readonly CampCostDbContext _db;
    private readonly BudgetCalculationService _budget;
    private readonly ITransactionAssignmentService _assignment;
    private readonly IPlanService _plans;
    private readonly FuelEstimateService _fuel;
    private readonly ILogger<TripController> _logger;

    public TripController(
        CampCostDbContext db,
        BudgetCalculationService budget,
        ITransactionAssignmentService assignment,
        IPlanService plans,
        FuelEstimateService fuel,
        ILogger<TripController> logger)
    {
        _db = db;
        _budget = budget;
        _assignment = assignment;
        _plans = plans;
        _fuel = fuel;
        _logger = logger;
    }

    // Free plan: reject trips longer than 7 days (exactly 7 allowed). 402 upgrade_required.
    private async Task<IActionResult?> EnforceTripDurationAsync(DateOnly? start, DateOnly? end)
    {
        var plan = await _plans.GetPlanAsync(UserId);
        if (PlanPolicy.IsTripDurationAllowed(plan, start, end)) return null;
        return StatusCode(StatusCodes.Status402PaymentRequired, new
        {
            error = PlanPolicy.TripDurationUpgradeMessage,
            code = "upgrade_required"
        });
    }

    // Free-plan cap on the NUMBER of trips. Enforced server-side (the client's
    // FREE_TRIP_LIMIT is display-only and bypassable). Returns 402 when over the limit.
    private async Task<IActionResult?> EnforceTripCountAsync()
    {
        var plan = await _plans.GetPlanAsync(UserId);
        if (PlanPolicy.IsPro(plan)) return null;
        var count = await _db.Trips.CountAsync(t => t.UserId == UserId);
        if (PlanPolicy.IsTripCountAllowed(plan, count)) return null;
        return StatusCode(StatusCodes.Status402PaymentRequired, new
        {
            error = PlanPolicy.TripCountUpgradeMessage,
            code = "upgrade_required"
        });
    }

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

    // Budget category keys must match the Postgres expense_category enum exactly
    // (case-sensitive) or the insert would fail at the DB layer with an opaque error.
    private static bool TryValidateCategories(Dictionary<string, decimal> budgets, out string? badCategory)
    {
        foreach (var key in budgets.Keys)
        {
            if (!Enum.TryParse<Infrastructure.ExpenseCategoryEnum>(key, ignoreCase: false, out _))
            {
                badCategory = key;
                return false;
            }
        }
        badCategory = null;
        return true;
    }

    // GET /api/trips
    // Returns all trips for the current user, ordered by start date
    [HttpGet]
    public async Task<IActionResult> GetTrips()
    {
        var trips = await _db.Trips
            .Where(t => t.UserId == UserId)
            .OrderBy(t => t.StartDate)
            .ToListAsync();

        return Ok(trips);
    }

    // POST /api/trips
    // Creates a trip, then auto-assigns any unassigned transactions whose date falls in
    // the trip's [start_date, end_date] window.
    [HttpPost]
    public async Task<IActionResult> CreateTrip([FromBody] TripWriteRequest req)
    {
        // Server-side plan gates: free plan is capped at 3 trips and 7 days each.
        var countGate = await EnforceTripCountAsync();
        if (countGate is not null) return countGate;

        var gate = await EnforceTripDurationAsync(req.StartDate, req.EndDate);
        if (gate is not null) return gate;

        if (req.Budgets is not null && !TryValidateCategories(req.Budgets, out var badCategory))
            return BadRequest(new { error = $"unknown budget category '{badCategory}'" });

        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = req.Name ?? "Untitled Trip",
            Destination = req.Destination,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            // per-category budgets win over total_budget so the sum can't drift from the rows
            TotalBudget = req.Budgets?.Values.Sum() ?? req.TotalBudget ?? 0,
            TotalMiles = req.TotalMiles,
            Status = req.Status ?? "planning",
            CreatedAt = DateTime.UtcNow
        };
        _db.Trips.Add(trip);

        foreach (var (category, planned) in req.Budgets ?? new Dictionary<string, decimal>())
        {
            _db.BudgetCategories.Add(new BudgetCategory
            {
                Id = Guid.NewGuid(),
                TripId = trip.Id,
                Category = category,
                PlannedAmount = planned,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _db.SaveChangesAsync();

        var assigned = await _assignment.AutoAssignForTripAsync(UserId, trip.Id);
        return Ok(new { trip, transactions_assigned = assigned.Assigned, warning = assigned.Warning });
    }

    // PUT /api/trips/{id}
    // Updates a trip. If its date range changed (or is now set), re-runs auto-assignment
    // to claim unassigned transactions that now fall inside the window.
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTrip(Guid id, [FromBody] TripWriteRequest req)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (trip is null) return NotFound();

        // Gate the RESULTING dates so a free trip can't be stretched past 7 days.
        var newStart = req.StartDate ?? trip.StartDate;
        var newEnd = req.EndDate ?? trip.EndDate;
        var gate = await EnforceTripDurationAsync(newStart, newEnd);
        if (gate is not null) return gate;

        if (req.Name is not null) trip.Name = req.Name;
        trip.Destination = req.Destination ?? trip.Destination;
        trip.StartDate = newStart;
        trip.EndDate = newEnd;
        if (req.TotalBudget.HasValue) trip.TotalBudget = req.TotalBudget.Value;
        if (req.TotalMiles.HasValue) trip.TotalMiles = req.TotalMiles;
        if (req.Status is not null) trip.Status = req.Status;

        await _db.SaveChangesAsync();

        // Strict reconcile: releases rows now outside the window, then claims the new range.
        var assigned = await _assignment.AutoAssignForTripAsync(UserId, trip.Id);
        return Ok(new
        {
            trip,
            transactions_assigned = assigned.Assigned,
            transactions_released = assigned.Released,
            warning = assigned.Warning
        });
    }

    // DELETE /api/trips/{id}
    // Deletes a trip and RELEASES its Plaid transactions back to the unassigned pool:
    // trip_id -> null AND reviewed -> false, so a new trip over the same dates will
    // auto-claim them again. (The FK alone only nulls trip_id; the transactions would
    // stay reviewed=true and auto-assign skips reviewed rows — that's the bug this fixes.)
    // Runs server-side because the browser has read-only RLS on transactions and cannot
    // write `reviewed`. Manual expenses/budgets/stops belonging to the trip are removed.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTrip(Guid id)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (trip is null) return NotFound();

        // Release the trip's transactions (query filter already excludes soft-deleted rows).
        var txns = await _db.Transactions
            .Where(t => t.UserId == UserId && t.TripId == id)
            .ToListAsync();
        var now = DateTime.UtcNow;
        foreach (var tx in txns)
        {
            tx.TripId = null;
            tx.Reviewed = false; // forget the review decision made in this trip's context
            tx.UpdatedAt = now;
        }

        // Remove the trip's dependent rows explicitly (no reliance on DB cascade).
        _db.Expenses.RemoveRange(_db.Expenses.Where(e => e.TripId == id));
        _db.BudgetCategories.RemoveRange(_db.BudgetCategories.Where(b => b.TripId == id));
        _db.TripStops.RemoveRange(_db.TripStops.Where(s => s.TripId == id));
        _db.Trips.Remove(trip);

        await _db.SaveChangesAsync();
        _logger.LogInformation(
            "Deleted trip {TripId}; released {Count} transaction(s) to unassigned",
            id, txns.Count);
        return Ok(new { released = txns.Count });
    }

    // GET /api/trips/{id}/budget
    // Returns budget summary for one trip:
    //   planned_budget, spent, money_left, daily_burn_rate, days_left_on_road
    // Spend = trip's Plaid transactions (not deleted) + manual expenses.
    // days_left_on_road uses the spending burn rate model (remaining / daily_burn).
    // Returns null when there is no spending yet (burn rate cannot be estimated).
    [HttpGet("{id:guid}/budget")]
    public async Task<IActionResult> GetBudget(Guid id)
    {
        var trip = await _db.Trips
            .Include(t => t.BudgetCategories)
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);

        if (trip is null) return NotFound();

        var spend = await GetTripSpendAsync(id);
        var summary = _budget.Calculate(trip, spend, trip.BudgetCategories);
        return Ok(summary);
    }

    // GET /api/trips/{id}/budget/by-category
    // Returns planned vs actual spending broken down by expense category
    [HttpGet("{id:guid}/budget/by-category")]
    public async Task<IActionResult> GetBudgetByCategory(Guid id)
    {
        var trip = await _db.Trips
            .Include(t => t.BudgetCategories)
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);

        if (trip is null) return NotFound();

        var spend = await GetTripSpendAsync(id);

        var plannedByCategory = trip.BudgetCategories
            .ToDictionary(bc => bc.Category, bc => bc.PlannedAmount);

        var spentByCategory = spend
            .GroupBy(e => e.Category)
            .ToDictionary(g => g.Key, g => g.Sum(e => e.Amount));

        // Union all known categories (planned or spent)
        var allCategories = plannedByCategory.Keys
            .Union(spentByCategory.Keys)
            .OrderBy(c => c);

        var breakdown = allCategories.Select(cat => new
        {
            category = cat,
            planned = plannedByCategory.GetValueOrDefault(cat, 0m),
            spent = spentByCategory.GetValueOrDefault(cat, 0m),
            remaining = plannedByCategory.GetValueOrDefault(cat, 0m)
                        - spentByCategory.GetValueOrDefault(cat, 0m)
        });

        return Ok(breakdown);
    }

    // POST /api/trips/{id}/fuel-estimate
    // Pre-trip fuel cost: miles / mpg × seeded state average price, per leg.
    // Legs default to one leg of the trip's total_miles at the national average.
    // apply=true upserts the result as the trip's `fuel` budget category.
    [HttpPost("{id:guid}/fuel-estimate")]
    public async Task<IActionResult> EstimateFuel(Guid id, [FromBody] FuelEstimateRequest req)
    {
        var trip = await _db.Trips
            .Include(t => t.BudgetCategories)
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (trip is null) return NotFound();

        var legs = req.Legs is { Count: > 0 }
            ? req.Legs.Select(l => new FuelEstimateService.LegInput(l.Miles, l.State)).ToList()
            : trip.TotalMiles is > 0
                ? new List<FuelEstimateService.LegInput> { new(trip.TotalMiles.Value, null) }
                : null;
        if (legs is null)
            return BadRequest(new { error = "provide legs, or set the trip's total_miles first" });

        FuelEstimateService.FuelEstimate estimate;
        try
        {
            estimate = _fuel.Estimate(legs, req.Mpg, req.FuelType ?? FuelEstimateService.Gas);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        var applied = false;
        if (req.Apply)
        {
            var fuelRow = trip.BudgetCategories.FirstOrDefault(bc => bc.Category == "fuel");
            if (fuelRow is not null)
            {
                fuelRow.PlannedAmount = estimate.TotalCost;
            }
            else
            {
                // First category row on a total_budget-only trip would silently shrink the
                // planned budget (category rows override total_budget when any exist), so
                // park the remainder in `misc` to keep the planned total intact.
                if (trip.BudgetCategories.Count == 0 && trip.TotalBudget > estimate.TotalCost)
                {
                    _db.BudgetCategories.Add(new BudgetCategory
                    {
                        Id = Guid.NewGuid(),
                        TripId = trip.Id,
                        Category = "misc",
                        PlannedAmount = trip.TotalBudget - estimate.TotalCost,
                        CreatedAt = DateTime.UtcNow
                    });
                }
                _db.BudgetCategories.Add(new BudgetCategory
                {
                    Id = Guid.NewGuid(),
                    TripId = trip.Id,
                    Category = "fuel",
                    PlannedAmount = estimate.TotalCost,
                    CreatedAt = DateTime.UtcNow
                });
            }
            await _db.SaveChangesAsync();
            applied = true;
        }

        return Ok(new { estimate, applied });
    }

    /// <summary>
    /// A trip's spend lines: Plaid transactions assigned to the trip (soft-deleted rows
    /// excluded by the global query filter) plus manual expenses. Both implement ISpendLine.
    /// </summary>
    private async Task<List<ISpendLine>> GetTripSpendAsync(Guid tripId)
    {
        var transactions = await _db.Transactions
            .Where(t => t.TripId == tripId && t.UserId == UserId)
            .ToListAsync();

        var manualExpenses = await _db.Expenses
            .Where(e => e.TripId == tripId && e.UserId == UserId)
            .ToListAsync();

        return transactions.Cast<ISpendLine>().Concat(manualExpenses).ToList();
    }
}

// `budgets` maps expense_category → planned amount (e.g. { "fuel": 200 }); when present on
// create, total_budget is derived from its sum so the two can't drift apart.
public record TripWriteRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("destination")] string? Destination,
    [property: JsonPropertyName("start_date")] DateOnly? StartDate,
    [property: JsonPropertyName("end_date")] DateOnly? EndDate,
    [property: JsonPropertyName("total_budget")] decimal? TotalBudget,
    [property: JsonPropertyName("total_miles")] decimal? TotalMiles,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("budgets")] Dictionary<string, decimal>? Budgets
);

// Legs are optional — omitted, the trip's total_miles becomes one national-average leg.
public record FuelEstimateRequest(
    [property: JsonPropertyName("mpg")] decimal Mpg,
    [property: JsonPropertyName("fuel_type")] string? FuelType,
    [property: JsonPropertyName("legs")] List<FuelLegRequest>? Legs,
    [property: JsonPropertyName("apply")] bool Apply
);

public record FuelLegRequest(
    [property: JsonPropertyName("miles")] decimal Miles,
    [property: JsonPropertyName("state")] string? State
);
