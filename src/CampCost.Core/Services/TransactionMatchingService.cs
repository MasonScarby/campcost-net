using CampCost.Core.Entities;

namespace CampCost.Core.Services;

/// <summary>
/// Matches a transaction date to a trip. Ported exactly from the original JS logic:
///
///   1. Date window match — tx date within [start - BUFFER_DAYS, min(end + BUFFER_DAYS, today)]
///   2. Fallback to a trip with no start_date set (catch-all trip)
///   3. null — unmatched transactions are skipped, not randomly assigned
///
/// The today cap prevents future-dated buffer windows from matching past transactions.
/// </summary>
public class TransactionMatchingService
{
    private const int BufferDays = 2;

    public Trip? FindMatchingTrip(DateOnly transactionDate, IEnumerable<Trip> trips)
    {
        // Deterministic regardless of caller ordering: earliest StartDate wins when
        // windows overlap (null StartDate sorts first but never matches the window step).
        var tripList = trips.OrderBy(t => t.StartDate).ToList();
        if (tripList.Count == 0) return null;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 1. Find first trip whose date window (+ buffer) contains the transaction
        var match = tripList.FirstOrDefault(t =>
        {
            if (t.StartDate is null) return false;

            var windowStart = t.StartDate.Value.AddDays(-BufferDays);

            // If end_date set, buffer it and cap at today; otherwise use today as end
            DateOnly windowEnd;
            if (t.EndDate.HasValue)
            {
                var buffered = t.EndDate.Value.AddDays(BufferDays);
                windowEnd = buffered < today ? buffered : today;
            }
            else
            {
                windowEnd = today;
            }

            return transactionDate >= windowStart && transactionDate <= windowEnd;
        });

        if (match is not null) return match;

        // 2. Fall back to a trip with no start_date (user hasn't set dates yet)
        var noDateTrip = tripList.FirstOrDefault(t => t.StartDate is null);
        if (noDateTrip is not null) return noDateTrip;

        // 3. No match — return null so the caller skips this transaction
        return null;
    }
}
