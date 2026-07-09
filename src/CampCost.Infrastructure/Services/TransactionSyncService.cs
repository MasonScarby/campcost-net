using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Cursor-based ingestion via Plaid /transactions/sync into the `transactions` table
/// (migration 004) — the raw Plaid mirror and source of truth. Ingestion is fully
/// decoupled from trips: every delta is stored regardless of whether a trip claims it,
/// so the old "unmatched transaction skipped past the cursor = permanent data loss" bug
/// disappears by construction.
///
/// Delta handling (all against `transactions`, never `expenses`):
///   added    → insert (dedup on plaid_transaction_id). If the added transaction carries a
///              pending_transaction_id matching a pending row we already imported, the
///              existing row is updated in place — the Plaid ID is swapped to the settled
///              ID so a pending and its settled version never coexist. trip_id is set only
///              if a planning/active trip's window contains the date, else left NULL.
///   modified → update the existing row in place (amount/date/merchant corrections).
///   removed  → soft-delete (DeletedAt) — financial history is never hard-deleted.
///
/// The cursor is persisted on the connection in the same SaveChanges as the applied
/// deltas, so it never advances past unstored data. When a user has no planning/active
/// trips the sync STILL runs and stores every transaction as unassigned (trip_id NULL) —
/// SyncResult.Warning simply notes there was nothing to auto-assign to.
/// </summary>
public class TransactionSyncService : ITransactionSyncService
{
    private const int BufferDays = 2;
    private const int MaxDays = 90;

    public const string NoActiveTripsWarning =
        "Transactions were stored, but there are no active or planning trips to assign them " +
        "to — they're saved as unassigned. Create or activate a trip to claim them.";

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
    /// trip's window are routed to this trip), then claims any existing unassigned or
    /// non-reviewed transactions that fall inside the trip's window — corrects the
    /// "transaction landed on the wrong trip / no trip" problem. Reviewed transactions
    /// are never moved (user-confirmed assignments are respected).
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

        // 2. Claim existing in-window transactions on other trips / unassigned, except
        //    reviewed rows (user-confirmed assignments are never auto-moved).
        var (windowStart, windowEnd) = GetTripWindow(trip);

        var toReassign = await _db.Transactions
            .Where(t => t.UserId == userId
                        && t.TripId != tripId
                        && !t.Reviewed
                        && t.TransactionDate >= windowStart
                        && t.TransactionDate <= windowEnd)
            .ToListAsync();

        foreach (var tx in toReassign)
        {
            tx.TripId = tripId;
            tx.UpdatedAt = DateTime.UtcNow;
        }

        if (toReassign.Count > 0)
            await _db.SaveChangesAsync();

        _logger.LogInformation(
            "Trip sync for trip {TripId}: claimed {Reassigned} existing transaction(s) into window {Start}..{End}",
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
            // Trips are only used to AUTO-ASSIGN newly stored transactions. Their absence
            // no longer gates ingestion — every transaction is stored either way.
            var trips = await _db.Trips
                .Where(t => t.UserId == connection.UserId &&
                            (t.Status == "planning" || t.Status == "active"))
                .OrderBy(t => t.StartDate)
                .ToListAsync();

            if (trips.Count == 0)
                warning = NoActiveTripsWarning;

            _logger.LogInformation(
                "Plaid sync start: connection {ConnectionId}, user {UserId}, first sync: {IsFirstSync}",
                connection.Id, connection.UserId, connection.SyncCursor is null);

            var delta = await _plaid.SyncTransactionsAsync(connection.AccessToken, connection.SyncCursor);
            fetched += delta.Added.Count + delta.Modified.Count;

            // All existing transactions for this user, INCLUDING soft-deleted ones
            // (IgnoreQueryFilters) so a removed transaction is never re-imported and
            // modified/removed deltas can still find their row.
            var existingByPlaidId = await _db.Transactions
                .IgnoreQueryFilters()
                .Where(t => t.UserId == connection.UserId)
                .ToDictionaryAsync(t => t.PlaidTransactionId);

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

                // Dedup on plaid_transaction_id (includes soft-deleted rows)
                if (existingByPlaidId.ContainsKey(tx.TransactionId)) { skipped++; continue; }

                // TODO(free-tier cap): FreePlanLimits.MaxAssignedTransactionsPerTrip (50) is
                // enforced in TransactionAssignmentService (auto-assign + manual PATCH), but
                // THIS sync path still routes in-window transactions to a trip uncapped via
                // MatchTrip. Storing the row is correct (ingestion is never capped); gate the
                // trip_id it gets — count trip-ASSIGNED rows (trip_id != null, not deleted),
                // never the raw stored count, and fall back to NULL when the trip is full.

                var newRow = Build(connection, tx, MatchTrip(tx.Date, trips, targetTrip)?.Id);
                _db.Transactions.Add(newRow);
                // Track it so a later modified/removed delta in the same run finds it.
                existingByPlaidId[tx.TransactionId] = newRow;
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

                // Modified delta for a transaction we never imported — treat as added.
                var newRow = Build(connection, tx, MatchTrip(tx.Date, trips, targetTrip)?.Id);
                _db.Transactions.Add(newRow);
                existingByPlaidId[tx.TransactionId] = newRow;
                upserted++;
            }

            foreach (var plaidId in delta.RemovedTransactionIds)
            {
                // Soft-delete only — financial history is never hard-deleted.
                if (existingByPlaidId.TryGetValue(plaidId, out var row) && row.DeletedAt is null)
                {
                    row.DeletedAt = DateTime.UtcNow;
                    row.UpdatedAt = DateTime.UtcNow;
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

    private Transaction Build(PlaidConnection connection, PlaidTransaction tx, Guid? tripId)
    {
        var now = DateTime.UtcNow;
        return new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = connection.UserId,
            PlaidConnectionId = connection.Id,
            PlaidTransactionId = tx.TransactionId,
            PendingTransactionId = tx.PendingTransactionId,
            TripId = tripId,
            Amount = tx.Amount,
            TransactionDate = tx.Date,
            MerchantName = tx.MerchantName,
            Name = tx.Name,
            Category = _categorizer.Categorize(tx.Name, tx.MerchantName),
            Pending = tx.Pending,
            Reviewed = false,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>
    /// Trip routing for AUTO-assignment of newly stored transactions: trip-specific syncs
    /// force in-window transactions onto the target trip; everything else goes through the
    /// normal date-window matcher. Returns null (unassigned) when nothing matches — the
    /// transaction is still stored.
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

    /// <summary>Update an existing transaction from a Plaid delta — the pending→settled and `modified` path.</summary>
    private void ApplyTransaction(Transaction row, PlaidTransaction tx)
    {
        row.PlaidTransactionId = tx.TransactionId;
        row.PendingTransactionId = tx.PendingTransactionId;
        row.MerchantName = tx.MerchantName;
        row.Name = tx.Name;
        row.Amount = tx.Amount;
        row.TransactionDate = tx.Date;
        row.Category = _categorizer.Categorize(tx.Name, tx.MerchantName);
        row.Pending = tx.Pending;
        row.UpdatedAt = DateTime.UtcNow;
    }
}
