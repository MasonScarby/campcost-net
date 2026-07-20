using System.Text.Json.Serialization;

namespace CampCost.Api.Contracts;

// Request body for TripController POST/PUT. All fields nullable because PUT is a
// partial update — the controller gates the RESULTING date range, so absent fields
// fall back to the stored trip rather than being treated as intentional clears.
// `budgets` maps expense_category → planned amount (e.g. { "fuel": 200 }); when present on
// create, total_budget is derived from its sum so the two can't drift apart.
public record TripWriteRequest(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("destination")] string? Destination,
    [property: JsonPropertyName("start_date")] DateOnly? StartDate,
    [property: JsonPropertyName("end_date")] DateOnly? EndDate,
    [property: JsonPropertyName("total_budget")] decimal? TotalBudget,
    [property: JsonPropertyName("total_miles")] decimal? TotalMiles,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("budgets")] Dictionary<string, decimal>? Budgets
);

// Legs are optional — omitted, the trip's total_miles becomes one national-average leg.
public record FuelEstimateRequest(
    [property: JsonPropertyName("mpg")] decimal Mpg,
    [property: JsonPropertyName("fuel_type")] string? FuelType,
    [property: JsonPropertyName("legs")] List<FuelLegRequest>? Legs,
    [property: JsonPropertyName("apply")] bool Apply
);

public record FuelLegRequest(
    [property: JsonPropertyName("miles")] decimal Miles,
    [property: JsonPropertyName("state")] string? State
);
