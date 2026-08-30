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
[Collection(AppTextCollection.Name)]
public class AllergenTests
{
    private static IReadOnlyList<AllergenMark> Read(params string[] fragments) =>
        ProductFacts.ReadAllergens(fragments);

    private static AllergenMark? Group(IReadOnlyList<AllergenMark> marks, string group) =>
        marks.FirstOrDefault(m => m.Group == group);

    // ------------------------------------------------------- traces (issue #58)

    /// <summary>
    /// The real ingredient list from Maza hoemoes chipotle, which carried a nut
    /// chip. The nut words are in the last sentence -- the factory warning -- and
    /// note there is no full stop before "Kan", which is why the trace detection
    /// cannot rely on sentence boundaries.
    /// </summary>
    private const string Hoemoes =
        "Ingrediënten: 56% kikkererwten, raapolie, 11% chipotle puree (60% Chipotle Chili pepers, " +
        "water, azijn, uien, suiker, kruiden, zonnebloem olie), 8,5% SESAMPASTA (SESAMZAAD, zout), " +
        "voedingszuren: citroenzuur, azijnzuur; zout, kruiden en specerijen (bevat koriander), " +
        "conserveermiddel: kaliumsorbaat Kan sporen van NOTEN en PINDA'S bevatten.";

    [Fact]
    public void Sesame_does_not_raise_a_nut_mark()
    {
        // Checked because it was the first suspicion when that chip appeared. It
        // is not the cause: no nut term matches sesame at all.
        Assert.Empty(Read("8,5% SESAMPASTA (SESAMZAAD, zout)"));
    }

    [Fact]
    public void A_may_contain_traces_line_is_marked_as_traces_not_as_an_ingredient()
    {
        var nuts = Group(Read(Hoemoes), AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.Equal(AllergenEvidence.Traces, nuts.Evidence);
    }

    [Theory]
    [InlineData("Kan sporen van noten bevatten.")]
    [InlineData("Kan sporen bevatten van noten.")]
    [InlineData("Bevat mogelijk sporen van noten.")]
    // The term lists are Dutch, so the English wording only ever matters on a
    // bilingual label where the allergen itself is still named in Dutch.
    [InlineData("May contain traces of amandelen.")]
    public void The_usual_precautionary_wordings_are_all_traces(string text)
    {
        var nuts = Group(Read(text), AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.Equal(AllergenEvidence.Traces, nuts.Evidence);
    }

    [Fact]
    public void An_ingredient_outranks_a_traces_line_for_the_same_group()
    {
        // A product that really does contain the allergen and also carries the
        // factory warning must read as the stronger of the two.
        var nuts = Group(Read("Ingrediënten: hazelnoten, suiker. Kan sporen van noten bevatten."),
            AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.Equal(AllergenEvidence.Suspected, nuts.Evidence);
    }

    [Fact]
    public void An_emphasised_allergen_outranks_a_traces_line()
    {
        var nuts = Group(Read("Ingrediënten: **hazelnoten**. Kan sporen van pinda's bevatten."),
            AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.Equal(AllergenEvidence.Declared, nuts.Evidence);
    }

    [Fact]
    public void Emphasis_inside_a_traces_line_is_still_only_traces()
    {
        // Picnic bolding a word inside a factory warning does not turn that
        // warning into a statement about what is in the product.
        var nuts = Group(Read("Ingrediënten: kikkererwten. Kan sporen van **noten** bevatten."),
            AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.Equal(AllergenEvidence.Traces, nuts.Evidence);
    }

    // -------------------------------------------------- evidence window (#58)

    [Fact]
    public void The_source_contains_the_word_it_is_evidence_for()
    {
        // The bug this replaces: the snippet was the first 240 characters of the
        // fragment, so on a long ingredient list the quoted evidence reliably
        // excluded the very term it was quoting for.
        var nuts = Group(Read(Hoemoes), AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.NotNull(nuts.Term);
        Assert.Contains(nuts.Term, nuts.Source!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trimmed_source_says_which_end_was_cut()
    {
        var nuts = Group(Read(Hoemoes), AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        // The match is at the end of a long list, so there is text before it that
        // did not fit and none after.
        Assert.StartsWith("…", nuts.Source);
        Assert.DoesNotContain("kikkererwten", nuts.Source);
    }

    [Fact]
    public void A_short_ingredient_list_is_quoted_whole()
    {
        var nuts = Group(Read("Ingrediënten: hazelnoten, suiker."), AllergenGroups.Nuts);

        Assert.NotNull(nuts);
        Assert.Equal("Ingrediënten: hazelnoten, suiker.", nuts.Source);
    }

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
    public async Task The_explanation_is_translated_too()
    {
        // Issue #13 landed after this: the note is the one piece of allergen copy
        // that lives server-side, so it is the one that could have been left in
        // Dutch while the chips around it were translated.
        var before = AppText.Current;
        try
        {
            AppText.Current = AppText.English;
            var html = await Grid();

            // No apostrophe in the asserted fragment: the slice escapes it, so
            // "Picnic's" arrives as "Picnic&#x27;s" and a naive substring misses.
            Assert.Contains("No label does not mean the product is free of that allergen", html);
            Assert.DoesNotContain("Allergenen komen", html);
        }
        finally
        {
            AppText.Current = before;
        }
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
