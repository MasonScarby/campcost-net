using CampCost.Core.Entities;
using CampCost.Core.Interfaces;
using CampCost.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CampCost.Infrastructure.Services;

/// <summary>
/// Assigns transactions to trips. See <see cref="ITransactionAssignmentService"/>.
/// The soft-delete query filter applies to all reads here, so removed transactions are
/// never assigned or surfaced.
/// </summary>
public class TransactionAssignmentService : ITransactionAssignmentService
{
    private readonly CampCostDbContext _db;
    private readonly ILogger<TransactionAssignmentService> _logger;

    public TransactionAssignmentService(CampCostDbContext db, ILogger<TransactionAssignmentService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> AutoAssignForTripAsync(Guid userId, Guid tripId)
    {
        var trip = await _db.Trips.FirstOrDefaultAsync(t => t.Id == tripId && t.UserId == userId);
        if (trip is null || trip.StartDate is null || trip.EndDate is null)
            return 0;

        var start = trip.StartDate.Value;
        var end = trip.EndDate.Value;

        // Only claim UNASSIGNED, non-reviewed rows in range — never steal from another trip
        // and never move a user-confirmed (reviewed) assignment.
        var toAssign = await _db.Transactions
            .Where(t => t.UserId == userId
                        && t.TripId == null
                        && !t.Reviewed
                        && t.TransactionDate >= start
                        && t.TransactionDate <= end)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var tx in toAssign)
        {
            tx.TripId = tripId;
            tx.UpdatedAt = now;
        }

        if (toAssign.Count > 0)
        {
            await _db.SaveChangesAsync();
            _logger.LogInformation(
                "Auto-assigned {Count} unassigned transaction(s) to trip {TripId} (range {Start}..{End})",
                toAssign.Count, tripId, start, end);
        }

        return toAssign.Count;
    }

    public async Task<bool> AssignAsync(Guid userId, Guid transactionId, Guid? tripId)
    {
        var tx = await _db.Transactions
            .FirstOrDefaultAsync(t => t.Id == transactionId && t.UserId == userId);
        if (tx is null) return false;

        if (tripId is not null)
        {
            var ownsTrip = await _db.Trips.AnyAsync(t => t.Id == tripId && t.UserId == userId);
            if (!ownsTrip) return false;
        }

        tx.TripId = tripId;
        tx.Reviewed = true; // user-confirmed — auto-assign must not move it again
        tx.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<Transaction>> GetUnassignedAsync(Guid userId)
    {
        return await _db.Transactions
            .Where(t => t.UserId == userId && t.TripId == null)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync();
    }
}
