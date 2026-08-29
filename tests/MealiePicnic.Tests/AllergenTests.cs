using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using RazorSlices;

namespace MealiePicnic.Tests;

/// <summary>
/// Issue #14. Two things are being checked here, and only one of them is "does
/// the word match".
///
/// The other is the asymmetry the feature is built around: under positive-only
/// display a false chip is a lie on the card, while a missed one is invisible.
/// So the false-positive cases (kokosmelk, nootmuskaat) matter more than the
/// coverage cases, and the tick-versus-question-mark distinction has to survive
/// every path, because it is the only thing separating "Picnic says so" from
/// "we spotted a word".
/// </summary>
public class AllergenTests
{
    private static IReadOnlyList<AllergenMark> Read(params string[] fragments) =>
        ProductFacts.ReadAllergens(fragments);

    private static AllergenMark? Group(IReadOnlyList<AllergenMark> marks, string group) =>
        marks.FirstOrDefault(m => m.Group == group);

    // ------------------------------------------------------------- plain text

    [Theory]
    [InlineData("Ingrediënten: tarwebloem, hazelnoten, suiker")]
    [InlineData("Ingrediënten: suiker, amandelen, zout")]
    [InlineData("Bevat pinda's")]
    [InlineData("Ingrediënten: cashew, rozijnen")]
    [InlineData("Kan sporen van noten bevatten")]
    public void Nut_words_raise_the_nut_chip(string text)
    {
        var mark = Group(Read(text), AllergenGroups.Nuts);

        Assert.NotNull(mark);
        Assert.False(mark.Declared);  // plain text is never a declaration
    }

    [Theory]
    [InlineData("Ingrediënten: melk, suiker")]
    [InlineData("Ingrediënten: water, lactose")]
    [InlineData("Bevat wei")]
    [InlineData("Ingrediënten: caseïne, plantaardige olie")]
    [InlineData("Ingrediënten: karnemelk")]
    public void Dairy_words_raise_the_milk_chip(string text)
    {
        var mark = Group(Read(text), AllergenGroups.Milk);

        Assert.NotNull(mark);
        Assert.False(mark.Declared);
    }

    // The derived names are the whole point of grouping: without them the milk
    // chip misses most processed products, which was the case this design set
    // out to cover.
    [Fact]
    public void The_milk_group_covers_names_that_do_not_contain_the_word()
    {
        foreach (var text in new[] { "lactose", "wei", "caseïne", "caseine" })
            Assert.NotNull(Group(Read($"Ingrediënten: {text}, water"), AllergenGroups.Milk));
    }

    // ------------------------------------------------------- false positives

    [Theory]
    [InlineData("Ingrediënten: kokosmelk, rijst")]      // no dairy at all
    [InlineData("Ingrediënten: amandelmelk")]           // nuts, but not milk
    [InlineData("Ingrediënten: sojamelk, suiker")]
    [InlineData("Ingrediënten: havermelk")]
    [InlineData("Extract van melkdistel")]
    [InlineData("Ingrediënten: water, melkzuur")]       // an acid, not dairy
    public void Plant_milks_and_lookalikes_do_not_raise_the_milk_chip(string text) =>
        Assert.Null(Group(Read(text), AllergenGroups.Milk));

    [Theory]
    [InlineData("Ingrediënten: kokosnoot, suiker")]     // not a tree nut in EU labelling
    [InlineData("Ingrediënten: nootmuskaat")]
    [InlineData("Ingrediënten: muskaatnoot, peper")]
    public void Nut_lookalikes_do_not_raise_the_nut_chip(string text) =>
        Assert.Null(Group(Read(text), AllergenGroups.Nuts));

    [Fact]
    public void Almond_milk_is_nuts_and_not_milk()
    {
        // The two exclusion lists are kept apart precisely for this: almond milk
        // really does carry a tree-nut allergen, and really does not carry milk.
        var marks = Read("Ingrediënten: **amandelmelk**, water");

        Assert.NotNull(Group(marks, AllergenGroups.Nuts));
        Assert.Null(Group(marks, AllergenGroups.Milk));
    }

    [Fact]
    public void A_substring_in_plain_text_yields_nothing()
    {
        // The whole-word rule on unemphasised text. "melk" sits inside
        // "buttermilk"-shaped compounds all over an ingredient list, and matching
        // it there is how a chip ends up on a product that does not carry it.
        Assert.Empty(Read("Ingrediënten: kokosmelkpoeder"));
    }

    // ------------------------------------------------------------- emphasis

    [Fact]
    public void An_emphasised_term_is_a_declaration()
    {
        var mark = Group(Read("Ingrediënten: tarwebloem, **melk**, suiker"), AllergenGroups.Milk);

        Assert.NotNull(mark);
        Assert.True(mark.Declared);
    }

    [Theory]
    [InlineData("Ingrediënten: karne**melk**poeder")]
    [InlineData("Ingrediënten: **melk**eiwit")]
    [InlineData("Ingrediënten: __melk__eiwit")]
    public void Emphasis_inside_a_compound_still_declares(string text)
    {
        // EU labelling emphasises the allergen within the word, so the
        // emphasised path deliberately does not require a whole-word match --
        // which is the one place it differs from plain text.
        var mark = Group(Read(text), AllergenGroups.Milk);

        Assert.NotNull(mark);
        Assert.True(mark.Declared);
    }

    [Fact]
    public void A_declaration_anywhere_outranks_a_plain_mention_elsewhere()
    {
        var mark = Group(
            Read("Kan sporen van melk bevatten", "Ingrediënten: **melk**"),
            AllergenGroups.Milk);

        Assert.NotNull(mark);
        Assert.True(mark.Declared);
    }

    [Fact]
    public void Emphasis_on_an_unrelated_word_declares_nothing()
    {
        Assert.Empty(Read("**Nieuw** in het assortiment"));
    }

    // --------------------------------------------------------------- nothing

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ingrediënten: water, suiker, zout")]
    public void Nothing_matched_is_an_empty_list_not_a_claim(string text)
    {
        // Empty means "we found nothing", never "contains none of them". The
        // distinction lives in the UI copy; here it just has to stay empty
        // rather than becoming a set of negative marks.
        Assert.Empty(Read(text));
    }

    [Fact]
    public void Groups_come_back_in_a_stable_order()
    {
        var marks = Read("Ingrediënten: **melk**, hazelnoten");

        Assert.Equal([AllergenGroups.Nuts, AllergenGroups.Milk], marks.Select(m => m.Group));
    }

    // ------------------------------------------------------------ the wording

    [Fact]
    public async Task The_grid_explains_the_two_marks_once()
    {
        // Once under the grid, not per card: repeating it on ninety cards is
        // exactly the noise the chips exist to avoid.
        var html = await Grid();

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "allergen-note"));
        Assert.Contains("allergeen", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_grid_never_claims_a_product_is_free_of_anything()
    {
        // The hard rule from the issue. Absence of a chip is not evidence: a card
        // with nothing on it looks identical whether the page listed no allergens
        // or could not be read at all. This is the tripwire for someone later
        // "improving" the wording into a claim the parse cannot support.
        string[] forbidden = ["vrij van", "bevat geen", "allergeenvrij", "free from", "zonder allergenen"];

        var html = await Grid();
        var found = forbidden.Where(p => html.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.True(found.Count == 0, "The grid makes a free-from claim: " + string.Join(", ", found));
    }

    private static ValueTask<string> Grid() => HitsGrid.Create(new HitsModel(
        new ShoppingItem(
            ItemId: "00000000-0000-0000-0000-000000000001",
            FoodId: "00000000-0000-0000-0000-000000000002",
            Display: "melk", FoodName: "melk", Quantity: 1, Unit: "l", Label: "",
            Checked: false, State: LinkState.New,
            PicnicUid: null, PicnicLabel: null, PicnicPack: null),
        [new PicnicProduct("s1002202", "Volle melk", 119, "1 liter", "img1")],
        "00000000-0000-0000-0000-000000000003")).RenderAsync();
}
