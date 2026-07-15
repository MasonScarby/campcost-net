using CampCost.Core.Services;
using FluentAssertions;

namespace CampCost.Tests.Services;

public class TransactionCategorizationServiceTests
{
    private readonly TransactionCategorizationService _sut = new();

    // Test name-only categorization (single-arg form)
    [Theory]
    [InlineData("KOA Campgrounds", "campground")]
    [InlineData("HipCamp Reservation", "campground")]
    [InlineData("ReserveAmerica Campsite", "campground")]
    [InlineData("Walmart Supercenter", "food_groceries")]
    [InlineData("Whole Foods Market", "food_groceries")]
    [InlineData("Kroger Grocery", "food_groceries")]
    [InlineData("Shell Gas Station", "fuel")]
    [InlineData("Pilot Flying J", "fuel")]
    [InlineData("Chevron", "fuel")]
    [InlineData("REI Co-op", "gear")]
    [InlineData("Bass Pro Shops", "gear")]
    [InlineData("Cabela's Outfitters", "gear")]
    [InlineData("AutoZone", "repairs")]
    [InlineData("Jiffy Lube", "repairs")]
    [InlineData("Propane Exchange", "propane_utilities")]
    [InlineData("AmeriGas Propane", "propane_utilities")]
    [InlineData("National Park Entrance Fee", "activities")]
    [InlineData("Recreation Permit", "activities")]
    [InlineData("McDonald's", "misc")]
    [InlineData("Netflix", "misc")]
    [InlineData("Some Random Merchant", "misc")]
    [InlineData("", "misc")]
    // Newly added merchants
    [InlineData("Buc-ee's", "fuel")]
    [InlineData("Valero", "fuel")]
    [InlineData("Wawa", "fuel")]
    [InlineData("ARCO", "fuel")]
    [InlineData("Costco Wholesale", "food_groceries")]
    [InlineData("Sam's Club", "food_groceries")]
    [InlineData("Wegmans", "food_groceries")]
    [InlineData("King Soopers", "food_groceries")]
    [InlineData("Camping World", "gear")]
    [InlineData("Valvoline Instant Oil", "repairs")]
    [InlineData("Les Schwab Tire Center", "repairs")]
    [InlineData("Thousand Trails", "campground")]
    [InlineData("San Diego Zoo", "activities")]
    [InlineData("Smithsonian Museum", "activities")]
    public void Categorize_ByName_ReturnsExpectedCategory(string name, string expectedCategory)
    {
        _sut.Categorize(name).Should().Be(expectedCategory);
    }

    [Theory]
    // Word-boundary guards: these must NOT trip the short/ambiguous tokens.
    [InlineData("Marco's Pizza", "misc")]   // not \barco\b -> fuel
    [InlineData("Zoom Video", "misc")]      // not zoo\b -> activities
    public void Categorize_ShortTokens_DoNotFalseMatch(string name, string expectedCategory)
    {
        _sut.Categorize(name).Should().Be(expectedCategory);
    }

    [Fact]
    public void Categorize_CostcoGas_IsFuel_NotGroceries()
    {
        // Fuel rule runs before groceries, so a Costco fuel purchase is fuel even though
        // "costco" also matches the groceries rule.
        _sut.Categorize("COSTCO GAS #0472").Should().Be("fuel");
        _sut.Categorize("Costco Wholesale").Should().Be("food_groceries");
    }

    [Fact]
    public void Categorize_MerchantNameTriggersRule()
    {
        // Raw name has no signal; merchant name identifies the category
        _sut.Categorize("SQ *001", "KOA Campgrounds").Should().Be("campground");
        _sut.Categorize("POS PURCHASE", "Shell").Should().Be("fuel");
    }

    [Fact]
    public void Categorize_IsCaseInsensitive()
    {
        _sut.Categorize("WALMART").Should().Be("food_groceries");
        _sut.Categorize("walmart").Should().Be("food_groceries");
        _sut.Categorize("WaLmArT").Should().Be("food_groceries");
    }

    [Fact]
    public void Categorize_NullInput_ReturnsMisc()
    {
        _sut.Categorize(null).Should().Be("misc");
    }

    [Fact]
    public void Categorize_BothNull_ReturnsMisc()
    {
        _sut.Categorize(null, null).Should().Be("misc");
    }

    [Fact]
    public void Categorize_FirstMatchingRuleWins()
    {
        // "state park" appears in both campground and activities patterns;
        // campground rule is listed first so it wins.
        _sut.Categorize("Oregon State Park Entrance").Should().Be("campground");
    }
}
