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
    // Optional "category" corrects the auto-derived category during review (must match
    // the expense_category enum exactly). Marks the transaction reviewed so auto-assign
    // won't move it again.
    // Free-plan trips are capped at 50 assigned transactions → 402 when the cap is reached.
    [HttpPatch("{id:guid}/assign")]
    public async Task<IActionResult> Assign(Guid id, [FromBody] AssignRequest req)
    {
        var outcome = await _assignment.AssignAsync(UserId, id, req.TripId, req.Category);
        return outcome switch
        {
            AssignOutcome.NotFound => NotFound(new { error = "Transaction or target trip not found for this user." }),
            AssignOutcome.InvalidCategory => BadRequest(new { error = $"unknown category '{req.Category}'" }),
            AssignOutcome.CapReached => StatusCode(StatusCodes.Status402PaymentRequired, new
            {
                error = FreePlanLimits.TransactionCapWarning,
                code = "upgrade_required"
            }),
            _ => Ok(new { success = true, transaction_id = id, trip_id = req.TripId })
        };
    }
    // DELETE /api/transactions/{id}
    // SOFT delete only (sets deleted_at) — financial history is never hard-deleted, and
    // dedup on plaid_transaction_id (IgnoreQueryFilters) prevents the next sync from
    // re-importing it. The row disappears from all views via the global query filter.
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var outcome = await _assignment.SoftDeleteAsync(UserId, id);
        return outcome switch
        {
            AssignOutcome.NotFound => NotFound(new { error = "Transaction not found for this user." }),
            _ => Ok(new { success = true, transaction_id = id })
        };
    }

    // PATCH /api/transactions/{id}/adjust
    // Split/reimbursed overlay: body { "adjusted_amount": 12.50, "adjustment_note": "Split 50/50" }.
    // { "adjusted_amount": null } clears the adjustment. The raw Plaid amount is never
    // modified — effective spend everywhere is coalesce(adjusted_amount, amount).
    [HttpPatch("{id:guid}/adjust")]
    public async Task<IActionResult> Adjust(Guid id, [FromBody] AdjustRequest req)
    {
        var outcome = await _assignment.AdjustAsync(UserId, id, req.AdjustedAmount, req.AdjustmentNote);
        return outcome switch
        {
            AssignOutcome.NotFound => NotFound(new { error = "Transaction not found for this user." }),
            AssignOutcome.InvalidAdjustment => BadRequest(new { error = "adjusted_amount cannot be negative" }),
            _ => Ok(new { success = true, transaction_id = id, adjusted_amount = req.AdjustedAmount })
        };
    }
}

public record AssignRequest(
    [property: JsonPropertyName("trip_id")] Guid? TripId,
    [property: JsonPropertyName("category")] string? Category = null
);

public record AdjustRequest(
    [property: JsonPropertyName("adjusted_amount")] decimal? AdjustedAmount,
    [property: JsonPropertyName("adjustment_note")] string? AdjustmentNote = null
);
