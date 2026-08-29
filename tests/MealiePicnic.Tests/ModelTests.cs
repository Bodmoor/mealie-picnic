using System.Globalization;
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

    // Dutch is the default language, so the separator here is a comma (issue #13).
    // It used to be a point, formatted invariantly, while the salt badge next to
    // it on the same card used a comma.
    [Theory]
    [InlineData(269, "€2,69")]
    [InlineData(95, "€0,95")]
    [InlineData(1799, "€17,99")]
    public void PriceText_formats_cents(int cents, string expected) =>
        Assert.Equal(expected, new PicnicProduct("s1", "x", cents, "", "").PriceText);

    [Theory]
    [InlineData(269, "€2.69")]
    [InlineData(95, "€0.95")]
    [InlineData(1799, "€17.99")]
    public void PriceText_uses_a_point_in_english(int cents, string expected) =>
        Assert.Equal(expected, new PicnicProduct("s1", "x", cents, "", "").PriceTextIn(AppText.English));

    [Fact]
    public void PriceText_is_blank_when_price_unknown() =>
        Assert.Equal("", new PicnicProduct("s1", "x", null, "", "").PriceText);

    // The host's culture must not reach the output: that was the original rule
    // behind formatting invariantly, and swapping in a chosen language must not
    // have quietly given it back. InvariantGlobalization means CurrentCulture is
    // already invariant here, so this guards the intent rather than reproducing
    // a nl-NL host, which the build cannot create.
    [Fact]
    public void PriceText_ignores_the_thread_culture()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Assert.Equal("€2,69", new PicnicProduct("s1", "x", 269, "", "").PriceTextIn(AppText.Dutch));
            Assert.Equal("€2.69", new PicnicProduct("s1", "x", 269, "", "").PriceTextIn(AppText.English));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = before;
        }
    }

    [Fact]
    public void AmountReason_follows_the_language()
    {
        var item = Item(500, "gram") with { PicnicPack = null };

        Assert.Equal("1 pak (geen pakgrootte bekend)", item.AmountReasonIn(AppText.Dutch));
        Assert.Equal("1 pack (pack size unknown)", item.AmountReasonIn(AppText.English));
    }
}
