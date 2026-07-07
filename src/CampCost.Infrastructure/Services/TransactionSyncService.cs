using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Cursor-based ingestion via Plaid /transactions/sync.
///
/// Delta handling:
///   added    → insert (dedup on plaid_transaction_id). If the added transaction carries a
///              pending_transaction_id that matches an expense we already imported while it
///              was pending, the existing row is updated in place — the Plaid ID is swapped
///              to the settled ID so a pending and its settled version never coexist.
///   modified → update the existing row in place (amount/date/merchant corrections).
///   removed  → soft-delete (DeletedAt) — financial history is never hard-deleted.
///
/// Pending transactions ARE imported (the old skip-all-pending workaround is gone):
/// /transactions/sync reconciles the pending→settled ID swap natively via the
/// added + removed pair, so both versions can never be present at once.
///
/// The cursor is persisted on the connection in the same SaveChanges as the applied
/// deltas. If no active trips exist for a user, the sync is skipped WITHOUT advancing
/// the cursor (advancing it would permanently drop those deltas) and a warning is
/// returned in SyncResult.
/// </summary>
public class TransactionSyncService : ITransactionSyncService
{
    private const int BufferDays = 2;
    private const int MaxDays = 90;

    public const string NoActiveTripsWarning =
        "No active or planning trips to match transactions against — sync was skipped so no " +
        "transactions are lost. Create or activate a trip, then sync again.";

    private readonly CampCostDbContext _db;
    private readonly IPlaidService _plaid;
    private readonly TransactionCategorizationService _categorizer;
    private readonly TransactionMatchingService _matcher;
    private readonly ILogger<TransactionSyncService> _logger;

    public TransactionSyncService(
        CampCostDbContext db,
        IPlaidService plaid,
        TransactionCategorizationService categorizer,
        TransactionMatchingService matcher,
        ILogger<TransactionSyncService> logger)
    {
        _db = db;
        _plaid = plaid;
        _categorizer = categorizer;
        _matcher = matcher;
        _logger = logger;
    }

    public async Task<SyncResult> SyncForUserAsync(Guid userId)
    {
        var connections = await _db.PlaidConnections
            .Where(c => c.UserId == userId)
            .ToListAsync();
        return await SyncConnectionsAsync(connections);
    }

    /// <summary>
    /// Trip-specific sync: runs the normal cursor sync (new transactions inside this
    /// trip's window are routed to this trip), then reassigns any existing Plaid
    /// expenses that fall inside the trip's window but landed on a different trip —
    /// corrects the "two overlapping trips, wrong trip got the transactions" problem.
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

        // 1. Pull the latest deltas, routing new in-window transactions to this trip
        var result = await SyncConnectionsAsync(connections, targetTrip: trip);

        // 2. Reassign existing Plaid expenses inside this trip's window that are on another trip
        var (windowStart, windowEnd) = GetTripWindow(trip);

        var toReassign = await _db.Expenses
            .Where(e => e.UserId == userId
                        && e.TripId != tripId
                        && e.PlaidTransactionId != null
                        && e.Source == "plaid"
                        && e.ExpenseDate >= windowStart
                        && e.ExpenseDate <= windowEnd)
            .ToListAsync();

        foreach (var expense in toReassign)
            expense.TripId = tripId;

        if (toReassign.Count > 0)
            await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Trip sync for trip {TripId}: reassigned {Reassigned} existing expense(s) into window {Start}..{End}",
            tripId, toReassign.Count, windowStart, windowEnd);

        return result with { ExpensesUpserted = result.ExpensesUpserted + toReassign.Count };
    }

    private static (DateOnly Start, DateOnly End) GetTripWindow(Trip trip)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        var start = trip.StartDate.HasValue
            ? trip.StartDate.Value.AddDays(-BufferDays)
            : today.AddDays(-MaxDays);

        var hardLimit = today.AddDays(-MaxDays);
        if (start < hardLimit) start = hardLimit;

        var end = trip.EndDate.HasValue
            ? (trip.EndDate.Value.AddDays(BufferDays) < today
                ? trip.EndDate.Value.AddDays(BufferDays)
                : today)
            : today;

        return (start, end);
    }

    private async Task<SyncResult> SyncConnectionsAsync(
        List<PlaidConnection> connections, Trip? targetTrip = null)
    {
        int fetched = 0, upserted = 0, skipped = 0, removed = 0;
        string? warning = null;

        foreach (var connection in connections)
        {
            // Only match against planning/active trips — same as original JS filter
            var trips = await _db.Trips
                .Where(t => t.UserId == connection.UserId &&
                            (t.Status == "planning" || t.Status == "active"))
                .OrderBy(t => t.StartDate)
                .ToListAsync();

            if (trips.Count == 0)
            {
                // Do NOT call Plaid / advance the cursor here: /transactions/sync only
                // delivers each delta once per cursor position, so skipping transactions
                // while advancing the cursor would drop them permanently.
                _logger.LogWarning(
                    "Sync skipped for connection {ConnectionId} (user {UserId}): no active trips to match against",
                    connection.Id, connection.UserId);
                warning = NoActiveTripsWarning;
                continue;
            }

            _logger.LogInformation(
                "Plaid sync start: connection {ConnectionId}, user {UserId}, first sync: {IsFirstSync}",
                connection.Id, connection.UserId, connection.SyncCursor is null);

            var delta = await _plaid.SyncTransactionsAsync(connection.AccessToken, connection.SyncCursor);
            fetched += delta.Added.Count + delta.Modified.Count;

            // All existing Plaid-sourced expenses for this user, INCLUDING soft-deleted
            // ones (IgnoreQueryFilters) so a removed transaction is never re-imported
            // and modified/removed deltas can still find their row.
            var existingByPlaidId = await _db.Expenses
                .IgnoreQueryFilters()
                .Where(e => e.UserId == connection.UserId && e.PlaidTransactionId != null)
                .ToDictionaryAsync(e => e.PlaidTransactionId!);

            foreach (var tx in delta.Added)
            {
                // Skip credits (money in) — Plaid: negative amount = credit
                if (tx.Amount < 0) { skipped++; continue; }

                // Pending → settled: Plaid delivers the settled transaction as `added`
                // with a NEW transaction_id and a pending_transaction_id pointing at the
                // pending version. If we imported the pending version, update it in place
                // (swap the Plaid ID) so pending + settled never coexist.
                if (tx.PendingTransactionId is not null &&
                    existingByPlaidId.TryGetValue(tx.PendingTransactionId, out var pendingRow))
                {
                    existingByPlaidId.Remove(tx.PendingTransactionId);
                    ApplyTransaction(pendingRow, tx);
                    existingByPlaidId[tx.TransactionId] = pendingRow;
                    upserted++;
                    continue;
                }

                // Dedup on plaid_transaction_id
                if (existingByPlaidId.ContainsKey(tx.TransactionId)) { skipped++; continue; }

                var matchedTrip = MatchTrip(tx.Date, trips, targetTrip);

                // No trip to assign to — skip (trip_id is NOT NULL in the DB)
                if (matchedTrip is null) { skipped++; continue; }

                var expense = new Expense
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
                };
                _db.Expenses.Add(expense);
                existingByPlaidId[tx.TransactionId] = expense;
                upserted++;
            }

            foreach (var tx in delta.Modified)
            {
                if (existingByPlaidId.TryGetValue(tx.TransactionId, out var row))
                {
                    // Update in place — never insert a second copy of the same Plaid ID
                    ApplyTransaction(row, tx);
                    upserted++;
                    continue;
                }

                if (tx.Amount < 0) { skipped++; continue; }

                // Modified delta for a transaction we never imported (e.g. it was pending
                // and skipped under the old /transactions/get flow) — treat as added.
                var matchedTrip = MatchTrip(tx.Date, trips, targetTrip);
                if (matchedTrip is null) { skipped++; continue; }

                var expense = new Expense
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
                };
                _db.Expenses.Add(expense);
                existingByPlaidId[tx.TransactionId] = expense;
                upserted++;
            }

            foreach (var plaidId in delta.RemovedTransactionIds)
            {
                // Soft-delete only — financial history is never hard-deleted.
                // Note: when a pending transaction settles, its `removed` delta usually
                // finds nothing here because the added-handler already swapped the row's
                // Plaid ID to the settled ID.
                if (existingByPlaidId.TryGetValue(plaidId, out var row) && row.DeletedAt is null)
                {
                    row.DeletedAt = DateTime.UtcNow;
                    removed++;
                }
            }

            // Persist the cursor in the same SaveChanges as the applied deltas —
            // never advance it past deltas that were not stored.
            connection.SyncCursor = delta.NextCursor;
            connection.LastSyncAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Plaid sync end: connection {ConnectionId} — added {Added}, modified {Modified}, removed {Removed}",
                connection.Id, delta.Added.Count, delta.Modified.Count, delta.RemovedTransactionIds.Count);
        }

        await _db.SaveChangesAsync();
        return new SyncResult(connections.Count, fetched, upserted, skipped, removed, warning);
    }

    /// <summary>
    /// Trip routing: trip-specific syncs force in-window transactions onto the target
    /// trip; everything else goes through the normal date-window matcher.
    /// </summary>
    private Trip? MatchTrip(DateOnly date, List<Trip> trips, Trip? targetTrip)
    {
        if (targetTrip is not null)
        {
            var (start, end) = GetTripWindow(targetTrip);
            if (date >= start && date <= end) return targetTrip;
        }
        return _matcher.FindMatchingTrip(date, trips);
    }

    /// <summary>Update an existing expense from a Plaid delta — the pending→settled and `modified` path.</summary>
    private void ApplyTransaction(Expense expense, PlaidTransaction tx)
    {
        expense.PlaidTransactionId = tx.TransactionId;
        expense.MerchantName = tx.MerchantName;
        expense.Amount = tx.Amount;
        expense.ExpenseDate = tx.Date;
        expense.Category = _categorizer.Categorize(tx.Name, tx.MerchantName);
    }
}
