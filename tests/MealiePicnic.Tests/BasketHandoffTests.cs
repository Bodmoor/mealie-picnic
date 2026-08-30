using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using RazorSlices;

namespace MealiePicnic.Tests;

/// <summary>
/// The parts of a basket run that stayed in the handler when #62 extracted the
/// run itself (issue #70): which items are sent, how many are reported as not
/// linked, and the out-of-band swap that refreshes the list in the same response.
///
/// All three decide what a person sees after pressing the one button that spends
/// money, and none of them was reachable from a test until they were pulled out
/// of the endpoint lambda.
/// </summary>
[Collection(AppTextCollection.Name)]
public class BasketHandoffTests
{
    private static ShoppingItem Item(LinkState state, bool ticked = false, string? uid = "s1005080") =>
        new(ItemId: Guid.NewGuid().ToString(), FoodId: Guid.NewGuid().ToString(),
            Display: "melk", FoodName: "melk", Quantity: 1, Unit: "l", Label: "",
            Checked: ticked, State: state,
            PicnicUid: uid, PicnicLabel: "Volle melk", PicnicPack: "1 l");

    // ------------------------------------------------------------- selection

    [Fact]
    public void Only_linked_items_that_are_still_open_go_to_the_basket()
    {
        var selection = BasketSelection.From([
            Item(LinkState.Linked),
            Item(LinkState.Linked, ticked: true),   // already bought
            Item(LinkState.New, uid: null),         // nothing to order
            Item(LinkState.Excluded, uid: null),    // decided against
        ]);

        Assert.Single(selection.ToAdd);
        Assert.All(selection.ToAdd, i => Assert.Equal(LinkState.Linked, i.State));
        Assert.All(selection.ToAdd, i => Assert.False(i.Checked));
    }

    [Fact]
    public void Unlinked_foods_are_counted_so_the_reader_knows_the_basket_is_not_the_whole_list()
    {
        var selection = BasketSelection.From([
            Item(LinkState.New, uid: null),
            Item(LinkState.New, uid: null),
            Item(LinkState.Linked),
            Item(LinkState.Excluded, uid: null),
        ]);

        // Excluded is a decision already made, not something left to do.
        Assert.Equal(2, selection.Skipped);
    }

    [Fact]
    public void An_empty_list_produces_an_empty_selection_rather_than_a_null_one()
    {
        var selection = BasketSelection.From([]);

        Assert.Empty(selection.ToAdd);
        Assert.Equal(0, selection.Skipped);
    }

    // ----------------------------------------------------- out-of-band swap

    [Fact]
    public async Task The_rendered_list_can_actually_be_marked_for_an_out_of_band_swap()
    {
        // The contract between ListView.cshtml and the swap helper. It used to be
        // an unchecked string Replace, so a reordered attribute in the slice would
        // have left the list silently not refreshing after a check-off.
        var listId = "00000000-0000-0000-0000-000000000003";
        var html = await ListView.Create(new ListViewModel(
            [new ShoppingListSummary(listId, "Boodschappen")], listId, [Item(LinkState.Linked)])).RenderAsync();

        var swapped = Html.AsOutOfBandSwap(html);

        Assert.Contains("<div id=\"view\" hx-swap-oob=\"true\"", swapped);
        Assert.Single(Regexes.Oob.Matches(swapped));
    }

    [Fact]
    public void A_view_the_helper_cannot_mark_fails_loudly_instead_of_silently()
    {
        // The whole point of the helper: an unrecognised root must not produce a
        // response whose second half the browser quietly ignores.
        Assert.Throws<InvalidOperationException>(() => Html.AsOutOfBandSwap("<section id=\"view\"></section>"));
    }

    private static class Regexes
    {
        public static readonly System.Text.RegularExpressions.Regex Oob =
            new("hx-swap-oob=\"true\"");
    }
}
