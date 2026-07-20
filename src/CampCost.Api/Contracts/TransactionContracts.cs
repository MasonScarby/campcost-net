using System.Text.Json.Serialization;

namespace CampCost.Api.Contracts;

// Request bodies for TransactionController.
// AssignRequest: TripId null = unassign; Category optional so a manual assign can
// correct the auto-derived category in the same call.
// AdjustRequest: overlay only — never mutates raw Plaid Amount (see Transaction entity).
public record AssignRequest(
    [property: JsonPropertyName("trip_id")] Guid? TripId,
    [property: JsonPropertyName("category")] string? Category = null
);

public record AdjustRequest(
    [property: JsonPropertyName("adjusted_amount")] decimal? AdjustedAmount,
    [property: JsonPropertyName("adjustment_note")] string? AdjustmentNote = null
);
