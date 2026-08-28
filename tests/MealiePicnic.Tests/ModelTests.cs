using MealiePicnic.Clients;

namespace MealiePicnic.Tests;

public class ModelTests
{
    // Amount lives in Quantities now and is covered by QuantitiesTests; these are
    // the plain formatting concerns.
    private static ShoppingItem Item(double quantity, string unit) => new(
        ItemId: "11111111-1111-1111-1111-111111111111",
        FoodId: "aaaaaaaa-1111-1111-1111-111111111111",
        Display: "melk", FoodName: "melk",
        Quantity: quantity, Unit: unit, Label: "Zuivel", Checked: false,
        State: LinkState.Linked, PicnicUid: "s1", PicnicLabel: null, PicnicPack: null);

    [Theory]
    [InlineData(0, "", 1)]        // Mealie items added without an amount carry quantity 0
    [InlineData(0.5, "", 1)]
    [InlineData(1, "", 1)]
    [InlineData(2, "", 2)]
    [InlineData(2.1, "", 3)]      // round up: you cannot buy a fraction of a pack
    [InlineData(500, "gram", 1)]  // and a weight is not a pack count
    public void Amount_never_drops_below_one(double quantity, string unit, int expected) =>
        Assert.Equal(expected, Item(quantity, unit).Amount);

    [Theory]
    [InlineData(269, "€2.69")]
    [InlineData(95, "€0.95")]
    [InlineData(1799, "€17.99")]
    public void PriceText_formats_cents(int cents, string expected) =>
        Assert.Equal(expected, new PicnicProduct("s1", "x", cents, "", "").PriceText);

    [Fact]
    public void PriceText_is_blank_when_price_unknown() =>
        Assert.Equal("", new PicnicProduct("s1", "x", null, "", "").PriceText);
}
