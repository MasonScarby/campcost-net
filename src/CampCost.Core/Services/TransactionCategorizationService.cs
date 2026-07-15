using System.Text.RegularExpressions;

namespace CampCost.Core.Services;

/// <summary>
/// Maps merchant names to the expense_category enum values used by the Supabase DB.
/// Category strings MUST exactly match the DB enum: fuel, campground, food_groceries,
/// gear, repairs, propane_utilities, activities, misc.
/// Ported directly from the original JS categorize() function.
/// </summary>
public class TransactionCategorizationService
{
    // Order matters: first matching rule wins. Fuel is checked before groceries so
    // "Costco Gas"/"Sam's Club Gas" land in fuel, while plain "Costco"/"Sam's Club"
    // fall through to groceries. Short/ambiguous tokens use \b word boundaries to avoid
    // false hits (e.g. \barco\b so "Marco's Pizza" isn't fuel; zoo\b so "Zoom" isn't activities).
    private static readonly (Regex Pattern, string Category)[] Rules =
    {
        (new Regex(@"shell|bp |chevron|exxon|mobil|pilot|love['\s]?s|loves|flying j|speedway|sunoco|circle k|casey|kwik trip|fuel|gasoline|gas station|valero|marathon|phillips 66|conoco|citgo|texaco|sinclair|\barco\b|wawa|sheetz|racetrac|quik ?trip|maverik|buc[-\s]?ee|murphy usa|costco gas|sam['\s]?s club gas", RegexOptions.IgnoreCase), "fuel"),
        (new Regex(@"koa|hipcamp|reserveamerica|campground|rv\s?park|rv resort|state park|campsite|boondock|harvest host|thousand trails|good sam|jellystone|recreation\.gov|passport america|sun outdoors", RegexOptions.IgnoreCase), "campground"),
        (new Regex(@"walmart|kroger|safeway|aldi|publix|heb|meijer|whole foods|trader joe|grocery|supermarket|food lion|sprouts|costco|sam['\s]?s club|winco|wegmans|albertsons|ralphs|\bvons\b|giant eagle|harris teeter|stop ?& ?shop|winn.?dixie|natural grocers|hy.?vee|king soopers", RegexOptions.IgnoreCase), "food_groceries"),
        (new Regex(@"rei|cabela['\s]?s|bass pro|academy sports|backcountry|moosejaw|gear|dick['\s]?s sporting|sportsman['\s]?s|camping world|patagonia|scheels", RegexOptions.IgnoreCase), "gear"),
        (new Regex(@"autozone|o['\s]?reilly|napa auto|advance auto|mechanic|muffler|tire|jiffy lube|repair|firestone|pep boys|valvoline|les schwab|\bmidas\b|meineke|goodyear|caliber collision|mavis", RegexOptions.IgnoreCase), "repairs"),
        (new Regex(@"propane|amerigas|ferrellgas|blue rhino|utilities|dump station|suburban propane|u.?haul propane", RegexOptions.IgnoreCase), "propane_utilities"),
        (new Regex(@"national park|state park|entrance fee|permit|recreation|national forest|america the beautiful|museum|zoo\b|aquarium", RegexOptions.IgnoreCase), "activities"),
    };

    /// <summary>
    /// Returns the DB enum category string for a merchant name.
    /// Falls back to "misc" (the DB default) if nothing matches.
    /// </summary>
    public string Categorize(string? name, string? merchantName = null)
    {
        var text = $"{name} {merchantName}".Trim();
        if (string.IsNullOrWhiteSpace(text)) return "misc";

        foreach (var (pattern, category) in Rules)
        {
            if (pattern.IsMatch(text)) return category;
        }

        return "misc";
    }
}
