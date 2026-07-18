using CampCost.Core.Data;
using CampCost.Core.Services;
using Xunit;

namespace CampCost.Tests.Services;

public class FuelEstimateServiceTests
{
    private readonly FuelEstimateService _svc = new();

    private static FuelEstimateService.LegInput Leg(decimal miles, string? state = null) => new(miles, state);

    [Fact]
    public void SingleLeg_NoState_UsesNationalAverage()
    {
        var e = _svc.Estimate(new[] { Leg(1200) }, mpg: 12);

        Assert.Equal(100m, e.TotalGallons); // 1200 / 12
        Assert.Equal(Math.Round(100m * StateGasPrices.NationalAverage.Regular, 2), e.TotalCost);
        Assert.Null(e.Legs[0].State);
        Assert.Equal(StateGasPrices.AsOf, e.PricesAsOf);
    }

    [Fact]
    public void MultiLeg_UsesEachLegsStatePrice_AndSums()
    {
        var e = _svc.Estimate(new[] { Leg(300, "PA"), Leg(300, "OH") }, mpg: 10);

        Assert.Equal(600m, e.TotalMiles);
        Assert.Equal(StateGasPrices.ByState["PA"].Regular, e.Legs[0].PricePerGallon);
        Assert.Equal(StateGasPrices.ByState["OH"].Regular, e.Legs[1].PricePerGallon);
        Assert.Equal(e.Legs.Sum(l => l.Cost), e.TotalCost);
    }

    [Fact]
    public void Diesel_UsesDieselPrice()
    {
        var e = _svc.Estimate(new[] { Leg(100, "TX") }, mpg: 10, fuelType: FuelEstimateService.Diesel);

        Assert.Equal(StateGasPrices.ByState["TX"].Diesel, e.Legs[0].PricePerGallon);
    }

    [Fact]
    public void StateCode_IsCaseInsensitive_AndNormalized()
    {
        var e = _svc.Estimate(new[] { Leg(100, " ca ") }, mpg: 10);

        Assert.Equal(StateGasPrices.ByState["CA"].Regular, e.Legs[0].PricePerGallon);
        Assert.Equal("CA", e.Legs[0].State);
    }

    [Fact]
    public void UnknownState_FallsBackToNationalAverage_ReportsNoState()
    {
        var e = _svc.Estimate(new[] { Leg(100, "ZZ") }, mpg: 10);

        Assert.Equal(StateGasPrices.NationalAverage.Regular, e.Legs[0].PricePerGallon);
        Assert.Null(e.Legs[0].State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void NonPositiveMpg_Throws(decimal mpg)
    {
        Assert.Throws<ArgumentException>(() => _svc.Estimate(new[] { Leg(100) }, mpg));
    }

    [Fact]
    public void NonPositiveLegMiles_Throws()
    {
        Assert.Throws<ArgumentException>(() => _svc.Estimate(new[] { Leg(0) }, mpg: 10));
    }

    [Fact]
    public void NoLegs_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            _svc.Estimate(Array.Empty<FuelEstimateService.LegInput>(), mpg: 10));
    }

    [Fact]
    public void UnknownFuelType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _svc.Estimate(new[] { Leg(100) }, mpg: 10, fuelType: "electric"));
    }

    [Fact]
    public void AllStatePrices_ArePlausible()
    {
        // Guard against a fat-fingered seed edit (e.g. 31.0 instead of 3.10).
        foreach (var (state, price) in StateGasPrices.ByState)
        {
            Assert.InRange(price.Regular, 2m, 7m);
            Assert.InRange(price.Diesel, 2m, 8m);
            Assert.True(price.Diesel >= price.Regular - 0.5m, $"{state} diesel far below regular");
        }
    }
}
