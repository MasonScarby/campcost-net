using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CampCost.Infrastructure.Services;

public class TransactionSyncService : ITransactionSyncService
{
    private readonly CampCostDbContext _db;
    private readonly IPlaidService _plaid;
    private readonly TransactionCategorizationService _categorizer;
    private readonly TransactionMatchingService _matcher;

    public TransactionSyncService(
        CampCostDbContext db,
        IPlaidService plaid,
        TransactionCategorizationService categorizer,
        TransactionMatchingService matcher)
    {
        _db = db;
        _plaid = plaid;
        _categorizer = categorizer;
        _matcher = matcher;
    }

    public async Task<SyncResult> SyncForUserAsync(Guid userId)
    {
        var connections = await _db.PlaidConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();
        return await SyncConnections(connections);
    }

    /// <summary>
    /// Trip-specific sync: fetches Plaid transactions covering exactly this trip's
    /// date range (start-2 to end+2, capped at today) and assigns them all to this trip.
    /// Any transactions already in the DB that are currently on a *different* trip but
    /// fall within this trip's window are reassigned here — this corrects the common
    /// "two overlapping trips, wrong trip got the transactions" problem.
    /// </summary>
    public async Task<SyncResult> SyncForTripAsync(Guid userId, Guid tripId)
    {
        var connections = await _db.PlaidConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();

        if (connections.Count == 0) return new SyncResult(0, 0, 0, 0);

        var trip = await _db.Trips
            .FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);

        if (trip is null) return new SyncResult(0, 0, 0, 0);

        const int BufferDays = 2;
        const int MaxDays = 90;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var startDate = trip.StartDate.HasValue
            ? trip.StartDate.Value.AddDays(-BufferDays)
            : today.AddDays(-MaxDays);

        // Never go further than 90 days back
        var hardLimit = today.AddDays(-MaxDays);
        if (startDate < hardLimit) startDate = hardLimit;

        var endDate = trip.EndDate.HasValue
            ? (trip.EndDate.Value.AddDays(BufferDays) < today
                ? trip.EndDate.Value.AddDays(BufferDays)
                : today)
            : today;

        int fetched = 0, upserted = 0, skipped = 0;

        // Map plaid_transaction_id → existing expense row so we can detect wrong-trip assignments
        var existingByPlaidId = await _db.Expenses
            .Where(e => e.UserId == userId && e.PlaidTransactionId != null)
            .Select(e => new { e.Id, e.TripId, e.PlaidTransactionId })
            .ToDictionaryAsync(e => e.PlaidTransactionId!, e => e);

        foreach (var connection in connections)
        {
            var transactions = await _plaid.GetTransactionsAsync(
                connection.AccessToken, startDate, endDate);
            fetched += transactions.Count;

            foreach (var tx in transactions)
            {
                // Skip credits (money coming in)
                if (tx.Amount < 0) { skipped++; continue; }

                // Skip pending — wait for settlement so only the stable Plaid ID is stored
                if (tx.Pending) { skipped++; continue; }

                if (existingByPlaidId.TryGetValue(tx.TransactionId, out var existing))
                {
                    if (existing.TripId == tripId)
                    {
                        skipped++; // Already on the right trip
                        continue;
                    }

                    // Transaction exists but landed on a different trip — reassign it here
                    var row = await _db.Expenses.FindAsync(existing.Id);
                    if (row is not null)
                    {
                        row.TripId = tripId;
                        upserted++;
                    }
                    continue;
                }

                // Brand new transaction — create it on this trip
                _db.Expenses.Add(new Expense
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    TripId = tripId,
                    PlaidTransactionId = tx.TransactionId,
                    MerchantName = tx.MerchantName,
                    Amount = tx.Amount,
                    Category = _categorizer.Categorize(tx.Name, tx.MerchantName),
                    ExpenseDate = tx.Date,
                    Source = "plaid",
                    Reviewed = false,
                    CreatedAt = DateTime.UtcNow
                });
                upserted++;
            }

            connection.LastSyncAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return new SyncResult(connections.Count, fetched, upserted, skipped);
    }

    public async Task<SyncResult> SyncAllAsync()
    {
        var connections = await _db.PlaidConnections.ToListAsync();
        return await SyncConnections(connections);
    }

    private async Task<SyncResult> SyncConnections(List<PlaidConnection> connections)
    {
        int fetched = 0, upserted = 0, skipped = 0;

        foreach (var connection in connections)
        {
            var endDate = DateOnly.FromDateTime(DateTime.UtcNow);

            // Only match against planning/active trips — same as original JS filter
            var trips = await _db.Trips
                .Where(t => t.UserId == connection.UserId &&
                            (t.Status == "planning" || t.Status == "active"))
                .OrderBy(t => t.StartDate)
                .ToListAsync();

            // Sync window: whichever is earliest of —
            //   • lastSyncAt - 2 days  (incremental, catches late-posting transactions)
            //   • earliest active trip start - 2 days  (ensures full trip is always covered)
            // Hard cap at 90 days so we never request years of history.
            // Dedup by plaid_transaction_id prevents re-inserting existing rows.
            const int BufferDays = 2;
            const int MaxDays = 90;

            var fromLastSync = connection.LastSyncAt.HasValue
                ? DateOnly.FromDateTime(connection.LastSyncAt.Value.AddDays(-BufferDays))
                : endDate.AddDays(-MaxDays);

            var earliestTripStart = trips
                .Where(t => t.StartDate.HasValue)
                .Select(t => t.StartDate!.Value.AddDays(-BufferDays))
                .DefaultIfEmpty(fromLastSync)
                .Min();

            var hardLimit = endDate.AddDays(-MaxDays);
            var startDate = earliestTripStart < fromLastSync ? earliestTripStart : fromLastSync;
            if (startDate < hardLimit) startDate = hardLimit;

            var transactions = await _plaid.GetTransactionsAsync(
                connection.AccessToken, startDate, endDate);
            fetched += transactions.Count;

            var existingIds = (await _db.Expenses
                .Where(e => e.UserId == connection.UserId && e.PlaidTransactionId != null)
                .Select(e => e.PlaidTransactionId)
                .ToListAsync()).ToHashSet();

            foreach (var tx in transactions)
            {
                // Skip credits (money in) — Plaid: negative amount = credit
                if (tx.Amount < 0) { skipped++; continue; }

                // Skip pending transactions — Plaid assigns a temporary ID while pending
                // and a new permanent ID once settled, causing duplicates if both are imported.
                // Wait for settlement so only the stable ID ever enters the DB.
                if (tx.Pending) { skipped++; continue; }

                // Deduplicate by plaid_transaction_id
                if (existingIds.Contains(tx.TransactionId)) { skipped++; continue; }

                var matchedTrip = _matcher.FindMatchingTrip(tx.Date, trips);

                // No trip to assign to — skip (original JS: if (!tripMatch) continue)
                if (matchedTrip is null) { skipped++; continue; }

                _db.Expenses.Add(new Expense
                {
                    Id = Guid.NewGuid(),
                    UserId = connection.UserId,
                    TripId = matchedTrip.Id,
                    PlaidTransactionId = tx.TransactionId,
                    MerchantName = tx.MerchantName,
                    Amount = tx.Amount,
                    Category = _categorizer.Categorize(tx.Name, tx.MerchantName),
                    ExpenseDate = tx.Date,
                    Source = "plaid",
                    Reviewed = false,
                    CreatedAt = DateTime.UtcNow
                });
                upserted++;
            }

            connection.LastSyncAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return new SyncResult(connections.Count, fetched, upserted, skipped);
    }
}
