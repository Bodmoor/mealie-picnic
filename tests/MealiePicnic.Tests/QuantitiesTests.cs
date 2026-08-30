using MealiePicnic.Clients;

namespace MealiePicnic.Tests;

/// <summary>
/// Issue #5: "500 gram bloem" ordered 500 packs. Quantity only means "how many to
/// buy" for countable units; for mass and volume it describes how much of one
/// product is needed.
/// </summary>
/// <summary>In the AppText collection: AmountReason reads the process-wide
/// AppText for its number format, so it races with the test that switches the
/// global to English (see AppTextCollectionAuditTests).</summary>
[Collection(AppTextCollection.Name)]
public class QuantitiesTests
{
    [Theory]
    // The bug: a weight must never become a pack count.
    [InlineData(500, "gram", null, 1)]
    [InlineData(500, "g", "", 1)]
    [InlineData(2, "kg", null, 1)]
    [InlineData(250, "ml", null, 1)]
    [InlineData(1, "liter", null, 1)]
    // Countable units mean what they say.
    [InlineData(3, "stuks", null, 3)]
    [InlineData(1, "stuk", null, 1)]
    [InlineData(2, "st", null, 2)]
    // No unit at all is countable: Mealie stores "2 wraps" with no unit.
    [InlineData(2, null, null, 2)]
    [InlineData(2, "", null, 2)]
    // Quantity 0 (added without an amount) is one.
    [InlineData(0, "gram", null, 1)]
    [InlineData(0, null, null, 1)]
    // Units we do not recognise fall back to one rather than guessing.
    [InlineData(4, "snufje", null, 1)]
    [InlineData(2, "tenen", null, 1)]
    public void Amount_without_a_known_pack(double qty, string? unit, string? pack, int expected) =>
        Assert.Equal(expected, Quantities.Required(qty, unit, pack));

    [Theory]
    // The optional half of the issue: with a known pack size, deduce the count.
    [InlineData(500, "gram", "1 kg", 1)]        // one bag covers it
    [InlineData(2, "kg", "1 kg", 2)]            // two bags
    [InlineData(2.5, "kg", "1 kg", 3)]          // round up, never short
    [InlineData(1, "kg", "500 gram", 2)]
    [InlineData(750, "gram", "500 gram", 2)]
    [InlineData(1, "liter", "1 liter", 1)]
    [InlineData(2, "liter", "500 ml", 4)]
    // Multipacks are totals: 3 x 60 g is 180 g.
    [InlineData(500, "gram", "3 x 60 gram", 3)]
    [InlineData(100, "gram", "3 x 60 gram", 1)]
    // Countable against a countable pack: a pack of 8 covers 3 wraps.
    [InlineData(3, "stuks", "8 stuks", 1)]
    [InlineData(12, "stuks", "8 stuks", 2)]
    // Dimension mismatch is not comparable, whichever way it runs -> one.
    // Issue #10: "5 uien" against a "1 kilo" bag must not order 5 bags.
    [InlineData(500, "gram", "6 stuks", 1)]
    [InlineData(3, "stuks", "500 gram", 1)]
    [InlineData(5, "stuks", "1 kilo", 1)]
    [InlineData(5, null, "1 kilo", 1)]
    // Unparseable pack strings behave as though unknown.
    [InlineData(500, "gram", "2-3 porties", 1)]
    [InlineData(500, "gram", "", 1)]
    public void Amount_with_a_pack_size(double qty, string? unit, string? pack, int expected) =>
        Assert.Equal(expected, Quantities.Required(qty, unit, pack));

    [Fact]
    public void Amount_is_capped()
    {
        // A mis-parsed unit should cost one slot in the basket, not fifty. Even
        // an honest 100 kg against 500 g packs is clamped.
        Assert.Equal(Quantities.MaxPerItem, Quantities.Required(100, "kg", "500 gram"));
        Assert.Equal(Quantities.MaxPerItem, Quantities.Required(9999, "stuks", null));
    }

    [Theory]
    [InlineData("1 kg", Dimension.Mass, 1000)]
    [InlineData("500 gram", Dimension.Mass, 500)]
    [InlineData("150 g", Dimension.Mass, 150)]
    [InlineData("±500 gram", Dimension.Mass, 500)]
    [InlineData("1,5 liter", Dimension.Volume, 1500)]
    [InlineData("6 stuks", Dimension.Count, 6)]
    [InlineData("3 x 60 gram", Dimension.Mass, 180)]
    [InlineData("2 x 1 liter", Dimension.Volume, 2000)]
    public void ParsePack_reads_picnic_unit_quantities(string text, Dimension dim, double value)
    {
        var pack = Quantities.ParsePack(text);

        Assert.NotNull(pack);
        Assert.Equal(dim, pack!.Value.Dimension);
        Assert.Equal(value, pack.Value.Value, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("2-3 porties")]
    [InlineData("4 porties")]
    [InlineData("per stuk verpakt")]   // no leading number
    public void ParsePack_returns_null_when_it_cannot_tell(string? text) =>
        Assert.Null(Quantities.ParsePack(text));

    [Theory]
    [InlineData("gram", Dimension.Mass)]
    [InlineData("GRAM", Dimension.Mass)]
    [InlineData("kg", Dimension.Mass)]
    [InlineData("ml", Dimension.Volume)]
    [InlineData("el", Dimension.Volume)]
    [InlineData("stuks", Dimension.Count)]
    [InlineData("", Dimension.Count)]
    [InlineData(null, Dimension.Count)]
    [InlineData("handjes", Dimension.Unknown)]
    public void ResolveUnit_folds_mealie_unit_names(string? unit, Dimension expected) =>
        Assert.Equal(expected, Quantities.ResolveUnit(unit).Dimension);

    [Fact]
    public void ShoppingItem_uses_the_same_rules()
    {
        // The regression in issue #5 was on ShoppingItem.Amount specifically.
        var weighed = Item(500, "gram", "1 kg");
        var counted = Item(3, "stuks", null);

        Assert.Equal(1, weighed.Amount);
        Assert.Equal(3, counted.Amount);
        Assert.Contains("1 kg", weighed.AmountReason);
    }

    private static ShoppingItem Item(double qty, string unit, string? pack) => new(
        ItemId: "11111111-1111-1111-1111-111111111111",
        FoodId: "aaaaaaaa-1111-1111-1111-111111111111",
        Display: "bloem", FoodName: "bloem",
        Quantity: qty, Unit: unit, Label: "", Checked: false,
        State: LinkState.Linked, PicnicUid: "s1", PicnicLabel: "Bloem", PicnicPack: pack);
}
