using CampCost.Core.Services;
using FluentAssertions;

namespace CampCost.Tests.Services;

/// <summary>
/// Pure plan-rule boundaries. The 7-day free-plan trip limit is INCLUSIVE of both
/// endpoints: exactly 7 days is allowed, 8 is rejected. Anything that isn't "pro"
/// (null, unknown values) must be treated as free — fail closed on the cheap side.
/// </summary>
public class PlanPolicyTests
{
    private static readonly DateOnly Start = new(2026, 7, 1);

    [Fact]
    public void Free_plan_rejects_trip_longer_than_7_days()
    {
        // Jul 1 → Jul 8 inclusive = 8 days
        PlanPolicy.IsTripDurationAllowed("free", Start, Start.AddDays(7))
            .Should().BeFalse();
    }

    [Fact]
    public void Free_plan_allows_exactly_7_day_trip()
    {
        // Jul 1 → Jul 7 inclusive = exactly 7 days — boundary must be allowed
        PlanPolicy.IsTripDurationAllowed("free", Start, Start.AddDays(6))
            .Should().BeTrue();
    }

    [Fact]
    public void Pro_plan_allows_any_trip_length()
    {
        PlanPolicy.IsTripDurationAllowed("pro", Start, Start.AddDays(365))
            .Should().BeTrue();
    }

    [Fact]
    public void Free_plan_allows_trip_with_missing_dates()
    {
        // No dates = nothing to measure; the gate re-runs when dates are set (PUT)
        PlanPolicy.IsTripDurationAllowed("free", null, null).Should().BeTrue();
        PlanPolicy.IsTripDurationAllowed("free", Start, null).Should().BeTrue();
        PlanPolicy.IsTripDurationAllowed("free", null, Start).Should().BeTrue();
    }

    [Fact]
    public void Duration_is_inclusive_of_both_endpoints()
    {
        PlanPolicy.TripDurationDays(Start, Start).Should().Be(1);
        PlanPolicy.TripDurationDays(Start, Start.AddDays(6)).Should().Be(7);
    }

    [Fact]
    public void Unknown_or_null_plan_is_treated_as_free()
    {
        PlanPolicy.IsTripDurationAllowed(null, Start, Start.AddDays(30)).Should().BeFalse();
        PlanPolicy.IsTripDurationAllowed("banana", Start, Start.AddDays(30)).Should().BeFalse();
    }

    [Fact]
    public void Plan_comparison_is_case_insensitive()
    {
        PlanPolicy.IsPro("PRO").Should().BeTrue();
        PlanPolicy.IsPro("Pro").Should().BeTrue();
    }
}
