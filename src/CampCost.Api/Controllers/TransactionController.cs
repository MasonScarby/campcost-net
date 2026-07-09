using CampCost.Core.Interfaces;
using CampCost.Core.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Text.Json.Serialization;

namespace CampCost.Api.Controllers;

/// <summary>
/// Read + assignment surface for the raw Plaid `transactions` mirror. All queries are
/// filtered to the authenticated user, mirroring every other endpoint. Ingestion happens
/// in PlaidController/TransactionSyncService; this controller only reads and (re)assigns.
/// </summary>
[ApiController]
[Route("api/transactions")]
[Authorize]
public class TransactionController : ControllerBase
{
    private readonly ITransactionAssignmentService _assignment;

    public TransactionController(ITransactionAssignmentService assignment)
    {
        _assignment = assignment;
    }

    private Guid UserId => Guid.Parse(
        User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? User.FindFirstValue("sub")
        ?? throw new UnauthorizedAccessException("No user ID in token"));

    // GET /api/transactions/unassigned
    // The user's stored, non-deleted transactions with no trip assigned (review queue).
    [HttpGet("unassigned")]
    public async Task<IActionResult> GetUnassigned()
    {
        var txns = await _assignment.GetUnassignedAsync(UserId);
        var shaped = txns.Select(t => new
        {
            id = t.Id,
            trip_id = t.TripId,
            amount = t.Amount,
            transaction_date = t.TransactionDate,
            merchant_name = t.MerchantName,
            name = t.Name,
            category = t.Category,
            pending = t.Pending,
            reviewed = t.Reviewed
        });
        return Ok(shaped);
    }

    // PATCH /api/transactions/{id}/assign
    // Manual override: body { "trip_id": "<guid>" } assigns; { "trip_id": null } unassigns.
    // Marks the transaction reviewed so auto-assign won't move it again.
    // Free-plan trips are capped at 50 assigned transactions → 402 when the cap is reached.
    [HttpPatch("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignRequest req)
    {
        var outcome = await _assignment.AssignAsync(UserId, id, req.TripId);
        return outcome switch
        {
            AssignOutcome.NotFound => NotFound(new { error = "Transaction or target trip not found for this user." }),
            AssignOutcome.CapReached => StatusCode(StatusCodes.Status402PaymentRequired, new
            {
                error = FreePlanLimits.TransactionCapWarning,
                code = "upgrade_required"
            }),
            _ => Ok(new { success = true, transaction_id = id, trip_id = req.TripId })
        };
    }
}

public record AssignRequest(
    [property: JsonPropertyName("trip_id")] Guid? TripId
);
