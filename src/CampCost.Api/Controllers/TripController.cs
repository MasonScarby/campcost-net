using CampCost.Api.Contracts;
using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

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

    public TripController(
        CampCostDbContext db,
        BudgetCalculationService budget,
        ITransactionAssignmentService assignment,
        IPlanService plans)
    {
        _db = db;
        _budget = budget;
        _assignment = assignment;
        _plans = plans;
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
        // Server-side plan gate: free-plan trips are capped at 7 days.
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

        var assigned = await _assignment.AutoAssignForTripAsync(UserId, trip.Id);
        return Ok(new { trip, transactions_assigned = assigned.Assigned, warning = assigned.Warning });
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
