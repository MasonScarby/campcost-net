using CampCost.Core.Entities;
using CampCost.Core.Services;
using FluentAssertions;

namespace CampCost.Tests.Services;

public class TransactionMatchingServiceTests
{
    private readonly TransactionMatchingService _sut = new();

    private static Trip MakeTrip(string name, DateOnly? start, DateOnly? end) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        StartDate = start,
        EndDate = end,
        UserId = Guid.NewGuid()
    };

    [Fact]
    public void ExactDateMatch_ReturnsTrip()
    {
        var trip = MakeTrip("Summer Camp", new DateOnly(2024, 7, 1), new DateOnly(2024, 7, 7));
        _sut.FindMatchingTrip(new DateOnly(2024, 7, 4), new[] { trip })
            .Should().Be(trip);
    }

    [Fact]
    public void TransactionOnStartDate_ReturnsTrip()
    {
        var trip = MakeTrip("Trip", new DateOnly(2024, 6, 10), new DateOnly(2024, 6, 15));
        _sut.FindMatchingTrip(new DateOnly(2024, 6, 10), new[] { trip })
            .Should().Be(trip);
    }

    [Fact]
    public void TransactionOnEndDate_ReturnsTrip()
    {
        var trip = MakeTrip("Trip", new DateOnly(2024, 6, 10), new DateOnly(2024, 6, 15));
        _sut.FindMatchingTrip(new DateOnly(2024, 6, 15), new[] { trip })
            .Should().Be(trip);
    }

    [Fact]
    public void TransactionWithinBufferBeforeStart_ReturnsTrip()
    {
        // 2 days before start — exactly on the BUFFER_DAYS=2 boundary (inclusive)
        var trip = MakeTrip("Trip", new DateOnly(2024, 8, 5), new DateOnly(2024, 8, 10));
        _sut.FindMatchingTrip(new DateOnly(2024, 8, 3), new[] { trip })
            .Should().Be(trip);
    }

    [Fact]
    public void TransactionWithinBufferAfterEnd_ReturnsTrip()
    {
        // 2 days after end — exactly on the BUFFER_DAYS=2 boundary (inclusive)
        var trip = MakeTrip("Trip", new DateOnly(2024, 8, 5), new DateOnly(2024, 8, 10));
        _sut.FindMatchingTrip(new DateOnly(2024, 8, 12), new[] { trip })
            .Should().Be(trip);
    }

    [Fact]
    public void TransactionOutsideBuffer_ReturnsNull()
    {
        // 3 days after end — outside BUFFER_DAYS=2; no null-start trip → null, never trips[0]
        var trip = MakeTrip("Trip", new DateOnly(2024, 8, 5), new DateOnly(2024, 8, 10));
        _sut.FindMatchingTrip(new DateOnly(2024, 8, 13), new[] { trip })
            .Should().BeNull();
    }

    [Fact]
    public void NoTrips_ReturnsNull()
    {
        _sut.FindMatchingTrip(DateOnly.FromDateTime(DateTime.UtcNow), Array.Empty<Trip>())
            .Should().BeNull();
    }

    [Fact]
    public void MultipleTripsMatch_ReturnsFirst()
    {
        // Both trips cover the tx date; FirstOrDefault returns the first one
        var first = MakeTrip("First Trip", new DateOnly(2024, 9, 1), new DateOnly(2024, 9, 30));
        var second = MakeTrip("Second Trip", new DateOnly(2024, 9, 1), new DateOnly(2024, 9, 5));
        _sut.FindMatchingTrip(new DateOnly(2024, 9, 2), new[] { first, second })
            .Should().Be(first);
    }

    [Fact]
    public void FallbackToNullStartDateTrip_WhenNoDatesMatch()
    {
        // tx date doesn't match any dated trip; should fall back to the no-dates (catch-all) trip
        var datedTrip = MakeTrip("Dated Trip", new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5));
        var catchAll  = MakeTrip("Catch-All", null, null);

        _sut.FindMatchingTrip(new DateOnly(2024, 6, 1), new[] { datedTrip, catchAll })
            .Should().Be(catchAll);
    }

    [Fact]
    public void ReturnsNull_WhenNoDatesMatchAndNoNullStartTrip()
    {
        // tx date doesn't match; no null-start trip → null (the old trips[0] fallback
        // randomly assigned transactions to an unrelated trip)
        var first  = MakeTrip("First",  new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 5));
        var second = MakeTrip("Second", new DateOnly(2024, 3, 1), new DateOnly(2024, 3, 5));

        _sut.FindMatchingTrip(new DateOnly(2024, 6, 1), new[] { first, second })
            .Should().BeNull();
    }

    [Fact]
    public void OverlappingTrips_EarliestStartDateWins_RegardlessOfInputOrder()
    {
        // Both trips cover the tx date; the earlier-starting trip must win even when
        // the caller passes trips in the "wrong" order — matching is deterministic.
        var earlier = MakeTrip("Earlier", new DateOnly(2024, 9, 1), new DateOnly(2024, 9, 30));
        var later   = MakeTrip("Later",   new DateOnly(2024, 9, 3), new DateOnly(2024, 9, 10));

        _sut.FindMatchingTrip(new DateOnly(2024, 9, 5), new[] { later, earlier })
            .Should().Be(earlier);
    }

    [Fact]
    public void TripWithNoStartDate_NeverMatchesDateWindow()
    {
        // A catch-all trip (null StartDate) should NOT match via the date-window path
        // It should only be returned as the fallback (step 2)
        var catchAll = MakeTrip("No-Date Trip", null, null);
        var datedTrip = MakeTrip("Dated", new DateOnly(2024, 7, 1), new DateOnly(2024, 7, 5));

        // tx date matches the dated trip → dated trip wins over the catch-all
        _sut.FindMatchingTrip(new DateOnly(2024, 7, 3), new[] { catchAll, datedTrip })
            .Should().Be(datedTrip);
    }
}
