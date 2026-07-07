using CampCost.Core.Entities;
using CampCost.Core.Services;
using FluentAssertions;

namespace CampCost.Tests.Services;

public class BudgetCalculationServiceTests
{
    private readonly BudgetCalculationService _sut = new();

    private static Trip MakeTrip(decimal totalBudget, DateOnly? startDate = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Trip",
        UserId = Guid.NewGuid(),
        TotalBudget = totalBudget,
        StartDate = startDate,
        Status = "active"
    };

    private static Expense MakeExpense(decimal amount, string category = "fuel") => new()
    {
        Id = Guid.NewGuid(),
        Amount = amount,
        Category = category,
        UserId = Guid.NewGuid(),
        ExpenseDate = DateOnly.FromDateTime(DateTime.UtcNow)
    };

    private static Transaction MakeTransaction(decimal amount, string category = "campground") => new()
    {
        Id = Guid.NewGuid(),
        Amount = amount,
        Category = category,
        UserId = Guid.NewGuid(),
        TransactionDate = DateOnly.FromDateTime(DateTime.UtcNow)
    };

    private static BudgetCategory MakeBudgetCategory(string category, decimal planned) => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        PlannedAmount = planned
    };

    // --- Money left ---

    [Fact]
    public void MoneyLeft_EqualsTotalBudgetMinusSpent()
    {
        var trip = MakeTrip(1000m);
        var expenses = new[] { MakeExpense(200m), MakeExpense(150m) };

        var result = _sut.Calculate(trip, expenses);

        result.Spent.Should().Be(350m);
        result.MoneyLeft.Should().Be(650m);
    }

    [Fact]
    public void Spent_SumsPlaidTransactionsAndManualExpenses()
    {
        // A trip's spend = Plaid transactions (source of truth) + manual expenses.
        var trip = MakeTrip(1000m);
        var spend = new ISpendLine[]
        {
            MakeTransaction(200m),   // Plaid
            MakeTransaction(50m),    // Plaid
            MakeExpense(75m),        // manual
        };

        var result = _sut.Calculate(trip, spend);

        result.Spent.Should().Be(325m);
        result.MoneyLeft.Should().Be(675m);
    }

    [Fact]
    public void MoneyLeft_UsesPlannedBudgetCategories_WhenProvided()
    {
        // budget_categories sum = 800, trip.total_budget = 1000 — categories win
        var trip = MakeTrip(1000m);
        var expenses = new[] { MakeExpense(100m) };
        var categories = new[] { MakeBudgetCategory("fuel", 300m), MakeBudgetCategory("food_groceries", 500m) };

        var result = _sut.Calculate(trip, expenses, categories);

        result.PlannedBudget.Should().Be(800m);
        result.MoneyLeft.Should().Be(700m);
    }

    [Fact]
    public void MoneyLeft_FallsBackToTotalBudget_WhenNoCategoriesProvided()
    {
        var trip = MakeTrip(500m);
        var result = _sut.Calculate(trip, Enumerable.Empty<Expense>(), null);
        result.PlannedBudget.Should().Be(500m);
    }

    [Fact]
    public void MoneyLeft_CanBeNegative_WhenOverBudget()
    {
        var trip = MakeTrip(100m);
        var result = _sut.Calculate(trip, new[] { MakeExpense(150m) });
        result.MoneyLeft.Should().Be(-50m);
    }

    // --- Days left (burn rate model) ---

    [Fact]
    public void DaysLeftOnRoad_IsNull_WhenNoSpendingYet()
    {
        var trip = MakeTrip(1000m, new DateOnly(2024, 1, 1));
        var result = _sut.Calculate(trip, Enumerable.Empty<Expense>());
        result.DaysLeftOnRoad.Should().BeNull();
    }

    [Fact]
    public void DaysLeftOnRoad_ComputesBurnRate()
    {
        // Trip started 10 days ago, $100 spent → daily burn = $10/day
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-10));
        var trip = MakeTrip(500m, startDate);

        // $100 spent over 10 days
        var expenses = new[] { MakeExpense(100m) };
        var result = _sut.Calculate(trip, expenses);

        // money_left = 400, daily_burn = 10, days_left = 40
        result.DailyBurnRate.Should().Be(10m);
        result.DaysLeftOnRoad.Should().Be(40m);
    }

    [Fact]
    public void DaysLeftOnRoad_IsZero_WhenOverBudget()
    {
        // Trip started 5 days ago, spent more than budget
        var startDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-5));
        var trip = MakeTrip(100m, startDate);

        var expenses = new[] { MakeExpense(200m) };
        var result = _sut.Calculate(trip, expenses);

        // money_left is negative → clamped to 0
        result.DaysLeftOnRoad.Should().Be(0m);
    }

    [Fact]
    public void DaysLeftOnRoad_NoStartDate_UsesElapsedDaysOf1()
    {
        // No start_date set → daysElapsed defaults to 1 (avoids division by zero)
        var trip = MakeTrip(200m, null);
        var expenses = new[] { MakeExpense(50m) };
        var result = _sut.Calculate(trip, expenses);

        // daily_burn = 50/1 = 50, money_left = 150, days_left = 3
        result.DailyBurnRate.Should().Be(50m);
        result.DaysLeftOnRoad.Should().Be(3m);
    }

    [Fact]
    public void TripBudgetSummary_IncludesTripName()
    {
        var trip = MakeTrip(100m);
        trip.Name = "Grand Canyon Loop";
        var result = _sut.Calculate(trip, Enumerable.Empty<Expense>());
        result.TripName.Should().Be("Grand Canyon Loop");
    }
}
