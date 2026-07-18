using CampCost.Core.Data;

namespace CampCost.Core.Services;

/// <summary>
/// Pre-trip fuel cost estimator: miles / MPG × state average price, per leg.
/// Pure math on seeded prices — no I/O — so the whole thing is unit-testable
/// and the estimate is reproducible for a given input.
/// </summary>
public class FuelEstimateService
{
    public record LegInput(decimal Miles, string? State);

    public record LegEstimate(decimal Miles, string? State, decimal PricePerGallon, decimal Gallons, decimal Cost);

    public record FuelEstimate(
        decimal Mpg,
        string FuelType,
        decimal TotalMiles,
        decimal TotalGallons,
        decimal TotalCost,
        string PricesAsOf,
        IReadOnlyList<LegEstimate> Legs);

    public const string Gas = "gas";
    public const string Diesel = "diesel";

    /// <exception cref="ArgumentException">mpg or any leg miles is not positive, or fuelType is unknown.</exception>
    public FuelEstimate Estimate(IEnumerable<LegInput> legs, decimal mpg, string fuelType = Gas)
    {
        if (mpg <= 0) throw new ArgumentException("mpg must be positive", nameof(mpg));
        if (fuelType is not (Gas or Diesel))
            throw new ArgumentException($"fuelType must be '{Gas}' or '{Diesel}'", nameof(fuelType));

        var results = new List<LegEstimate>();
        foreach (var leg in legs)
        {
            if (leg.Miles <= 0) throw new ArgumentException("leg miles must be positive", nameof(legs));

            var price = StateGasPrices.For(leg.State);
            var perGallon = fuelType == Diesel ? price.Diesel : price.Regular;
            var gallons = Math.Round(leg.Miles / mpg, 2);
            var cost = Math.Round(gallons * perGallon, 2);
            results.Add(new LegEstimate(leg.Miles, NormalizeState(leg.State), perGallon, gallons, cost));
        }

        if (results.Count == 0) throw new ArgumentException("at least one leg is required", nameof(legs));

        return new FuelEstimate(
            Mpg: mpg,
            FuelType: fuelType,
            TotalMiles: results.Sum(l => l.Miles),
            TotalGallons: results.Sum(l => l.Gallons),
            TotalCost: results.Sum(l => l.Cost),
            PricesAsOf: StateGasPrices.AsOf,
            Legs: results);
    }

    private static string? NormalizeState(string? state) =>
        state is not null && StateGasPrices.ByState.ContainsKey(state.Trim())
            ? state.Trim().ToUpperInvariant()
            : null; // unknown/blank states fall back to the national average and report no state
}
