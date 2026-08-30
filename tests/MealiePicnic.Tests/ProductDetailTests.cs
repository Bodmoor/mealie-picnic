using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using MealiePicnic.Storage;
using RazorSlices;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace MealiePicnic.Tests;

/// <summary>
/// The product detail view and the card that opens it (issue #48): what the view
/// shows about an allergen claim, and which claims it lets a person overrule.
/// </summary>
[Collection(AppTextCollection.Name)]
public class ProductDetailTests
{
    private const string FoodId = "00000000-0000-0000-0000-000000000002";
    private const string ListId = "00000000-0000-0000-0000-000000000003";

    private static ShoppingItem Item() => new(
        ItemId: "00000000-0000-0000-0000-000000000001",
        FoodId: FoodId, Display: "melk", FoodName: "melk", Quantity: 1, Unit: "l", Label: "",
        Checked: false, State: LinkState.New,
        PicnicUid: null, PicnicLabel: null, PicnicPack: null);

    private static ValueTask<string> Detail(
        PicnicDetails details,
        IReadOnlyDictionary<string, AllergenVerdict>? verdicts = null) =>
        ProductDetail.Create(new ProductDetailModel(
            Item(), ListId, "s1002202", "Volle melk", "1 liter", "img1",
            details, verdicts ?? new Dictionary<string, AllergenVerdict>())).RenderAsync();

    /// <summary>Razor encodes non-ASCII to numeric entities, so Dutch text with a
    /// diaeresis never appears literally in the output.</summary>
    private static string Encoded(string text) => HtmlEncoder.Default.Encode(text);

    private static PicnicDetails Details(params AllergenMark[] allergens) =>
        new("s1002202", Organic: false, SaltGramsPer100: null, SaltText: null, Allergens: allergens);

    // ------------------------------------------------------------------ the card

    [Fact]
    public async Task The_card_keeps_linking_and_gains_a_details_button()
    {
        // A button cannot contain a button, so the card is no longer one element.
        // Both actions must survive that split: the big one still links.
        var html = await HitsGrid.Create(new HitsModel(
            Item(),
            [new PicnicProduct("s1002202", "Volle melk", 119, "1 liter", "img1")],
            ListId)).RenderAsync();

        Assert.Contains("class=\"pick\"", html);
        Assert.Contains("hx-post=\"/api/link", html);
        Assert.Contains("class=\"info\"", html);
        Assert.Contains("hx-get=\"/api/products/s1002202", html);
        // Nested buttons would be invalid HTML and behave differently per browser.
        Assert.DoesNotContain("<button class=\"card", html);
    }

    [Fact]
    public void The_details_button_meets_the_minimum_touch_target()
    {
        // Issue #54. It started at 22px, over the product image, where a miss ran
        // the other action and linked the product. 44px is what both mobile
        // platforms ask for, and it is the number this rule exists to hold.
        var rule = Regex.Match(Html.AppPage, @"\.card \.info \{[^}]*\}").Value;

        Assert.Contains("width:44px", rule);
        Assert.Contains("height:44px", rule);
    }

    // ------------------------------------------------------------- the evidence

    [Fact]
    public async Task The_detail_view_shows_which_word_matched_and_where()
    {
        // The substance of issue #48: a chip on a card asserts something, and this
        // view is where that assertion is backed up.
        var html = await Detail(Details(
            new AllergenMark(AllergenGroups.Nuts, Declared: false, "hazelnoten", "Volle melk, hazelnoten")));

        Assert.Contains("hazelnoten", html);
        Assert.Contains("Volle melk, hazelnoten", html);
    }

    [Fact]
    public async Task A_suspected_allergen_can_be_confirmed_or_denied()
    {
        var html = await Detail(Details(
            new AllergenMark(AllergenGroups.Nuts, Declared: false, "hazelnoten", "Volle melk, hazelnoten")));

        Assert.Contains("/api/products/s1002202/allergens/nuts", html);
        Assert.Contains("verdict=confirmed", html);
        Assert.Contains("verdict=denied", html);
    }

    [Fact]
    public async Task A_declared_allergen_offers_no_way_to_deny_it()
    {
        // Picnic emphasising the term in the ingredient list is EU labelling, not
        // a guess of ours. Offering a "not correct" button against it would turn a
        // safety label into a preference.
        var html = await Detail(Details(
            new AllergenMark(AllergenGroups.Milk, Declared: true, "melk", "Volle **melk**")));

        Assert.DoesNotContain("verdict=denied", html);
        Assert.DoesNotContain("verdict=confirmed", html);
        Assert.Contains(Encoded(AppText.Current.DetailsDeclaredFixed), html);
    }

    // ------------------------------------------------------------- the verdicts

    [Fact]
    public async Task A_recorded_verdict_is_shown_with_who_made_it()
    {
        var mark = new AllergenMark(AllergenGroups.Nuts, false, "hazelnoten", "Volle melk, hazelnoten");
        var html = await Detail(Details(mark), new Dictionary<string, AllergenVerdict>
        {
            [AllergenGroups.Nuts] = new(VerdictState.Denied, "paul@example.com", DateTimeOffset.UtcNow,
                "Volle melk, hazelnoten"),
        });

        Assert.Contains(AppText.Current.VerdictDenied, html);
        Assert.Contains("paul@example.com", html);
        // Already denied, so only the other two moves are offered.
        Assert.DoesNotContain("verdict=denied", html);
        Assert.Contains("verdict=clear", html);
    }

    [Fact]
    public async Task A_verdict_made_against_different_ingredients_is_flagged_as_stale()
    {
        // Picnic reformulates. A denial made against last year's recipe still
        // stands -- discarding it silently would be worse -- but it is not silent.
        var mark = new AllergenMark(AllergenGroups.Nuts, false, "hazelnoten", "Volle melk, hazelnoten, soja");
        var html = await Detail(Details(mark), new Dictionary<string, AllergenVerdict>
        {
            [AllergenGroups.Nuts] = new(VerdictState.Denied, null, DateTimeOffset.UtcNow, "Volle melk"),
        });

        Assert.Contains(Encoded(AppText.Current.VerdictStale), html);
    }

    [Fact]
    public async Task A_verdict_against_the_same_ingredients_is_not_flagged()
    {
        var mark = new AllergenMark(AllergenGroups.Nuts, false, "hazelnoten", "Volle melk, hazelnoten");
        var html = await Detail(Details(mark), new Dictionary<string, AllergenVerdict>
        {
            [AllergenGroups.Nuts] = new(VerdictState.Confirmed, null, DateTimeOffset.UtcNow,
                "Volle melk, hazelnoten"),
        });

        Assert.DoesNotContain(Encoded(AppText.Current.VerdictStale), html);
    }

    // ----------------------------------------------------------- the rest of it

    [Fact]
    public async Task The_page_text_picnic_publishes_is_shown()
    {
        var details = new PicnicDetails(
            "s1002202", Organic: true, SaltGramsPer100: 0.12, SaltText: "0,12 g",
            Allergens: [],
            Title: "Volle melk",
            Highlights: ["Van Nederlandse koeien"],
            Description: ["Romige volle melk."],
            Sections: [new DetailSection("Ingrediënten", ["Volle melk"]) { Ingredients = true }]);

        var html = await Detail(details);

        Assert.Contains("Van Nederlandse koeien", html);
        Assert.Contains("Romige volle melk.", html);
        Assert.Contains(Encoded("Ingrediënten"), html);
        Assert.Contains(AppText.Current.DetailsOrganic, html);
    }

    [Fact]
    public async Task A_page_that_could_not_be_read_says_so_rather_than_showing_an_empty_frame()
    {
        var html = await Detail(Details());

        Assert.Contains(AppText.Current.DetailsUnavailable, html);
    }
}
