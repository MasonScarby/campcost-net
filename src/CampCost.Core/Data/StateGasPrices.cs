namespace CampCost.Core.Data;

/// <summary>
/// Seed table of average retail fuel prices ($/gallon) by state.
/// This is intentionally static data — a live price API is deferred until the
/// estimator proves useful (roadmap: "prepopulate, don't overbuild"). Refresh by
/// editing this file from EIA/AAA state averages and bumping AsOf.
/// </summary>
public static class StateGasPrices
{
    /// <summary>Date the seeded prices were last refreshed. Surfaced in the API
    /// response so the UI can show "prices as of ..." next to the estimate.</summary>
    public const string AsOf = "2025-12";

    public record FuelPrice(decimal Regular, decimal Diesel);

    /// <summary>National average, used when a leg has no state.</summary>
    public static readonly FuelPrice NationalAverage = new(3.10m, 3.60m);

    public static readonly IReadOnlyDictionary<string, FuelPrice> ByState =
        new Dictionary<string, FuelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["AL"] = new(2.75m, 3.30m),
            ["AK"] = new(3.65m, 3.95m),
            ["AZ"] = new(3.30m, 3.70m),
            ["AR"] = new(2.70m, 3.25m),
            ["CA"] = new(4.65m, 5.05m),
            ["CO"] = new(3.00m, 3.55m),
            ["CT"] = new(3.20m, 3.85m),
            ["DE"] = new(3.00m, 3.55m),
            ["DC"] = new(3.35m, 3.90m),
            ["FL"] = new(3.05m, 3.60m),
            ["GA"] = new(2.85m, 3.45m),
            ["HI"] = new(4.55m, 5.15m),
            ["ID"] = new(3.25m, 3.65m),
            ["IL"] = new(3.40m, 3.75m),
            ["IN"] = new(3.10m, 3.60m),
            ["IA"] = new(2.90m, 3.35m),
            ["KS"] = new(2.80m, 3.30m),
            ["KY"] = new(2.85m, 3.40m),
            ["LA"] = new(2.70m, 3.30m),
            ["ME"] = new(3.10m, 3.75m),
            ["MD"] = new(3.15m, 3.70m),
            ["MA"] = new(3.15m, 3.80m),
            ["MI"] = new(3.15m, 3.65m),
            ["MN"] = new(2.95m, 3.40m),
            ["MS"] = new(2.65m, 3.25m),
            ["MO"] = new(2.80m, 3.30m),
            ["MT"] = new(3.15m, 3.60m),
            ["NE"] = new(2.90m, 3.35m),
            ["NV"] = new(3.85m, 4.10m),
            ["NH"] = new(3.05m, 3.65m),
            ["NJ"] = new(3.10m, 3.70m),
            ["NM"] = new(2.95m, 3.45m),
            ["NY"] = new(3.30m, 3.90m),
            ["NC"] = new(2.90m, 3.50m),
            ["ND"] = new(2.95m, 3.40m),
            ["OH"] = new(3.05m, 3.55m),
            ["OK"] = new(2.65m, 3.20m),
            ["OR"] = new(3.75m, 4.05m),
            ["PA"] = new(3.30m, 3.95m),
            ["RI"] = new(3.10m, 3.75m),
            ["SC"] = new(2.80m, 3.40m),
            ["SD"] = new(2.95m, 3.40m),
            ["TN"] = new(2.80m, 3.35m),
            ["TX"] = new(2.75m, 3.30m),
            ["UT"] = new(3.20m, 3.65m),
            ["VT"] = new(3.15m, 3.80m),
            ["VA"] = new(3.00m, 3.60m),
            ["WA"] = new(4.10m, 4.45m),
            ["WV"] = new(3.05m, 3.60m),
            ["WI"] = new(2.95m, 3.40m),
            ["WY"] = new(3.05m, 3.50m),
        };

    public static FuelPrice For(string? state) =>
        state is not null && ByState.TryGetValue(state.Trim(), out var p) ? p : NationalAverage;
}
