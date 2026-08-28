using MealiePicnic.Clients;

namespace MealiePicnic.Tests;

/// <summary>
/// Issue #6. Both facts are read out of human-readable text, so the tests here are
/// mostly about the ways that reading can go wrong. The asymmetry is deliberate:
/// missing a leaf is a minor annoyance, putting one on a non-organic product is
/// misinformation, so the false-positive cases carry the most weight.
/// </summary>
public class ProductFactsTests
{
    [Theory]
    // Plain claims, as they appear in names, highlights and ingredient lists.
    [InlineData("Bio bananen")]
    [InlineData("Biologische halfvolle melk")]
    [InlineData("biologisch")]
    [InlineData("Bio-eieren van vrije uitloop")]
    [InlineData("Organic quinoa")]
    [InlineData("**Biologisch** en fairtrade")]           // markdown emphasis
    [InlineData("#(#8CC63F)Biologisch")]                  // picnic colour markup
    [InlineData("Gecertificeerd NL-BIO-01")]              // EU control body code
    [InlineData("Bevat biologische tarwebloem")]
    public void Organic_claims_are_recognised(string text) =>
        Assert.True(ProductFacts.IsOrganic([text]), $"should count as organic: {text}");

    [Theory]
    // The expensive mistakes. "Biologisch afbreekbaar" is biodegradable packaging
    // and says nothing about farming; the rest merely start with the same letters.
    [InlineData("Biologisch afbreekbare vuilniszakken")]
    [InlineData("100% biologisch afbreekbaar")]
    [InlineData("Biotex voorwas")]
    [InlineData("Biogarde yoghurt")]
    [InlineData("Biologie voor beginners")]
    [InlineData("Bioscoopkaartjes")]
    [InlineData("Scharreleieren")]
    [InlineData("")]
    [InlineData(null)]
    public void Non_claims_are_not_read_as_organic(string? text) =>
        Assert.False(ProductFacts.IsOrganic([text]), $"should not count as organic: {text}");

    [Fact]
    public void A_sentence_can_contain_both_and_the_disqualifier_only_kills_itself()
    {
        // One string, two statements: biodegradable packaging around organic tea.
        Assert.True(ProductFacts.IsOrganic(
            ["Biologisch afbreekbare theezakjes, gevuld met biologische thee"]));

        // ... and without the second statement it stays false.
        Assert.False(ProductFacts.IsOrganic(["Biologisch afbreekbare theezakjes"]));
    }

    [Theory]
    // Label and value in one fragment, in the shapes a markdown table collapses to.
    [InlineData("Zout 1,2 g", 1.2)]
    [InlineData("Zout: 0.35 g", 0.35)]
    [InlineData("Zout | 1,2 g", 1.2)]
    [InlineData("**Zout** 2,5 gram", 2.5)]
    [InlineData("zout 480 mg", 0.48)]
    [InlineData("Zout 0 g", 0.0)]
    public void Salt_is_read_from_a_single_fragment(string line, double expected) =>
        Assert.Equal(expected, Salt([line]), 3);

    [Fact]
    public void Salt_is_read_from_the_next_fragment_when_the_table_splits_cells()
    {
        Assert.Equal(1.2, Salt(["Voedingswaarde per 100 g", "Energie", "254 kJ", "Zout", "1,2 g"]), 3);
    }

    [Fact]
    public void The_per_100_g_heading_is_not_mistaken_for_the_value()
    {
        // "Zout" followed by "per 100 g" would otherwise read as 100 g of salt.
        Assert.Null(ProductFacts.ParseSalt(["Zout", "per 100 g"]));
    }

    [Fact]
    public void Sodium_is_converted_only_when_salt_is_absent()
    {
        // 2.5 is the standard natrium -> zout factor.
        Assert.Equal(1.0, Salt(["Natrium 0,4 g"]), 3);

        // A table listing both must use the salt row, not 2.5x it.
        Assert.Equal(1.2, Salt(["Natrium 0,4 g", "Zout 1,2 g"]), 3);
    }

    [Theory]
    [InlineData("Zout")]                  // label with no value anywhere
    [InlineData("Zout 1,2")]              // value with no unit: could be anything
    [InlineData("Energie 254 kJ")]        // no salt row at all
    [InlineData("Zoutjes 100 gram")]      // a product name, not a nutrient
    [InlineData("Zout 250 gram")]         // out of range: a pack weight, misread
    public void Unreadable_tables_yield_nothing(string line) =>
        Assert.Null(ProductFacts.ParseSalt([line]));

    [Fact]
    public void Salt_keeps_the_text_it_was_read_from()
    {
        // The raw text is what makes a wrong figure diagnosable later.
        var salt = ProductFacts.ParseSalt(["Zout 1,2 g"]);

        Assert.NotNull(salt);
        Assert.Equal("1,2 g", salt!.Value.Text);
    }

    private static double Salt(string[] lines)
    {
        // Asserting here keeps the numeric comparisons on doubles (not double?) and
        // turns "parsed nothing" into its own, obvious failure.
        var salt = ProductFacts.ParseSalt(lines);

        Assert.NotNull(salt);
        return salt!.Value.Grams;
    }
}
