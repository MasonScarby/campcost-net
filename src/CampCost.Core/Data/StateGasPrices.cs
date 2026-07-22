namespace CampCost.Core.Data;

/// <summary>
/// Seed table of average retail fuel prices ($/gallon) by state.
/// This is intentionally static data — a live price API is deferred until the
/// estimator proves useful (roadmap: "prepopulate, don't overbuild").
/// Values are 2026 ESTIMATES (not scraped live data) — refresh from EIA/AAA state
/// averages and bump AsOf before relying on them for real users.
/// </summary>
public static class StateGasPrices
{
    /// <summary>Date the seeded prices were last refreshed. Surfaced in the API
    /// response so the UI can show "prices as of ..." next to the estimate.</summary>
    public const string AsOf = "2026-07";

    public record FuelPrice(decimal Regular, decimal Diesel);

    /// <summary>National average, used when a leg has no state.</summary>
    public static readonly FuelPrice NationalAverage = new(4.02m, 5.50m);

    public static readonly IReadOnlyDictionary<string, FuelPrice> ByState =
        new Dictionary<string, FuelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["AL"] = new(3.55m, 5.05m),
            ["AK"] = new(4.15m, 5.60m),
            ["AZ"] = new(4.15m, 5.55m),
            ["AR"] = new(3.55m, 5.05m),
            ["CA"] = new(6.50m, 7.20m),
            ["CO"] = new(3.95m, 5.45m),
            ["CT"] = new(4.05m, 5.65m),
            ["DE"] = new(3.85m, 5.40m),
            ["DC"] = new(4.25m, 5.70m),
            ["FL"] = new(3.75m, 5.35m),
            ["GA"] = new(3.60m, 5.15m),
            ["HI"] = new(4.95m, 6.15m),
            ["ID"] = new(4.20m, 5.55m),
            ["IL"] = new(4.15m, 5.55m),
            ["IN"] = new(3.90m, 5.45m),
            ["IA"] = new(3.65m, 5.20m),
            ["KS"] = new(3.55m, 5.15m),
            ["KY"] = new(3.60m, 5.15m),
            ["LA"] = new(3.55m, 5.10m),
            ["ME"] = new(3.95m, 5.55m),
            ["MD"] = new(3.95m, 5.50m),
            ["MA"] = new(4.00m, 5.60m),
            ["MI"] = new(3.95m, 5.50m),
            ["MN"] = new(3.75m, 5.30m),
            ["MS"] = new(3.45m, 5.00m),
            ["MO"] = new(3.50m, 5.10m),
            ["MT"] = new(4.00m, 5.45m),
            ["NE"] = new(3.65m, 5.20m),
            ["NV"] = new(4.55m, 5.90m),
            ["NH"] = new(3.85m, 5.50m),
            ["NJ"] = new(3.85m, 5.45m),
            ["NM"] = new(3.75m, 5.35m),
            ["NY"] = new(4.15m, 5.70m),
            ["NC"] = new(3.65m, 5.25m),
            ["ND"] = new(3.70m, 5.30m),
            ["OH"] = new(3.75m, 5.30m),
            ["OK"] = new(3.45m, 5.05m),
            ["OR"] = new(4.35m, 5.75m),
            ["PA"] = new(4.15m, 5.65m),
            ["RI"] = new(3.95m, 5.55m),
            ["SC"] = new(3.55m, 5.10m),
            ["SD"] = new(3.70m, 5.25m),
            ["TN"] = new(3.55m, 5.10m),
            ["TX"] = new(3.40m, 5.00m),
            ["UT"] = new(4.10m, 5.50m),
            ["VT"] = new(3.95m, 5.60m),
            ["VA"] = new(3.70m, 5.30m),
            ["WA"] = new(4.70m, 5.95m),
            ["WV"] = new(3.70m, 5.30m),
            ["WI"] = new(3.70m, 5.30m),
            ["WY"] = new(3.80m, 5.40m),
        };

    public static FuelPrice For(string? state) =>
        state is not null && ByState.TryGetValue(state.Trim(), out var p) ? p : NationalAverage;
}
