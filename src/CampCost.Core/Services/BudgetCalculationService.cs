using CampCost.Core.Entities;

namespace CampCost.Core.Services;

/// <summary>
/// Calculates "money left" and "days left on the road" for a trip.
///
/// Money left:
///   planned_budget - sum(spend)
///   spend = SUM(transactions where trip_id = trip AND not deleted) + manual expenses.
///           Both sources implement ISpendLine so they sum uniformly here.
///   planned_budget = sum(budget_categories.planned_amount) if rows exist,
///                    otherwise falls back to trip.total_budget.
///
/// Days left — spending burn rate model:
///   daily_burn = total_spent / days_elapsed_since_start
///   days_left  = money_left / daily_burn
///
/// Returns null for DaysLeftOnRoad when there is no spending yet (burn rate = 0)
/// because it is not possible to estimate a runway without a data point.
/// </summary>
public class BudgetCalculationService
{
    public TripBudgetSummary Calculate(
        Trip trip,
        IEnumerable<ISpendLine> spendLines,
        IEnumerable<BudgetCategory>? budgetCategories = null)
    {
        var spendList = spendLines.ToList();
        var spent = spendList.Sum(e => e.Amount);

        // Planned budget: prefer sum of per-category planned amounts if available
        var categoryList = budgetCategories?.ToList();
        var plannedBudget = (categoryList is { Count: > 0 })
            ? categoryList.Sum(bc => bc.PlannedAmount)
            : trip.TotalBudget;

        var moneyLeft = plannedBudget - spent;

        // Days elapsed since trip start (minimum 1 to avoid division by zero)
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var daysElapsed = trip.StartDate.HasValue
            ? Math.Max(today.DayNumber - trip.StartDate.Value.DayNumber, 1)
            : 1;

        var dailyBurnRate = Math.Round(spent / daysElapsed, 2);

        // Runway only makes sense when money is being spent
        decimal? daysLeftOnRoad = dailyBurnRate > 0
            ? Math.Round(Math.Max(moneyLeft / dailyBurnRate, 0m), 1)
            : null;

        return new TripBudgetSummary(
            TripId: trip.Id,
            TripName: trip.Name,
            PlannedBudget: plannedBudget,
            Spent: spent,
            MoneyLeft: moneyLeft,
            DailyBurnRate: dailyBurnRate,
            DaysLeftOnRoad: daysLeftOnRoad
        );
    }
}

public record TripBudgetSummary(
    Guid TripId,
    string TripName,
    decimal PlannedBudget,
    decimal Spent,
    decimal MoneyLeft,
    decimal DailyBurnRate,
    decimal? DaysLeftOnRoad   // null = not enough spending data yet
);
