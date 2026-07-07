using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    public TripController(
        CampCostDbContext db,
        BudgetCalculationService budget,
        ITransactionAssignmentService assignment)
    {
        _db = db;
        _budget = budget;
        _assignment = assignment;
    }

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

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
        var trip = new Trip
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = req.Name ?? "Untitled Trip",
            Destination = req.Destination,
            StartDate = req.StartDate,
            EndDate = req.EndDate,
            TotalBudget = req.TotalBudget ?? 0,
            Status = req.Status ?? "planning",
            CreatedAt = DateTime.UtcNow
        };
        _db.Trips.Add(trip);
        await _db.SaveChangesAsync();

        var assigned = await _assignment.AutoAssignForTripAsync(UserId, trip.Id);
        return Ok(new { trip, transactions_assigned = assigned });
    }

    // PUT /api/trips/{id}
    // Updates a trip. If its date range changed (or is now set), re-runs auto-assignment
    // to claim unassigned transactions that now fall inside the window.
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> UpdateTrip(Guid id, [FromBody] TripWriteRequest req)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == id && t.UserId == UserId);
        if (trip is null) return NotFound();

        if (req.Name is not null) trip.Name = req.Name;
        trip.Destination = req.Destination ?? trip.Destination;
        trip.StartDate = req.StartDate ?? trip.StartDate;
        trip.EndDate = req.EndDate ?? trip.EndDate;
        if (req.TotalBudget.HasValue) trip.TotalBudget = req.TotalBudget.Value;
        if (req.Status is not null) trip.Status = req.Status;

        await _db.SaveChangesAsync();

        var assigned = await _assignment.AutoAssignForTripAsync(UserId, trip.Id);
        return Ok(new { trip, transactions_assigned = assigned });
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

public record TripWriteRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("destination")] string? Destination,
    [property: JsonPropertyName("start_date")] DateOnly? StartDate,
    [property: JsonPropertyName("end_date")] DateOnly? EndDate,
    [property: JsonPropertyName("total_budget")] decimal? TotalBudget,
    [property: JsonPropertyName("status")] string? Status
);
