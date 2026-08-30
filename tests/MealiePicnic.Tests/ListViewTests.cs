using System.Text.RegularExpressions;
using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using RazorSlices;

namespace MealiePicnic.Tests;

/// <summary>
/// The controls above the shopping list (issue #56). These assert the grouping,
/// not the styling: the point of the change is that the markup says which
/// controls belong together, so a browser breaking the row cannot separate a
/// button from its own option.
/// </summary>
[Collection(AppTextCollection.Name)]
public class ListViewTests
{
    private const string ListId = "00000000-0000-0000-0000-000000000003";

    private static ValueTask<string> View() =>
        ListView.Create(new ListViewModel(
            [new ShoppingListSummary(ListId, "Boodschappen")],
            ListId,
            [new ShoppingItem(
                ItemId: "00000000-0000-0000-0000-000000000001",
                FoodId: "00000000-0000-0000-0000-000000000002",
                Display: "melk", FoodName: "melk", Quantity: 1, Unit: "l", Label: "",
                Checked: false, State: LinkState.New,
                PicnicUid: null, PicnicLabel: null, PicnicPack: null)])).RenderAsync();

    [Fact]
    public async Task The_controls_are_split_into_three_groups()
    {
        // Which list, what to do with it, what to show.
        var html = await View();

        Assert.Contains("class=\"toolbar\"", html);
        Assert.Equal(3, Regex.Matches(html, "class=\"group\"").Count);
    }

    [Fact]
    public async Task The_basket_button_and_its_checkbox_share_a_group()
    {
        // "afvinken in Mealie" is an option OF the basket button -- it is already
        // inside the same <form> -- and used to sit next to an unrelated checkbox
        // once the row wrapped.
        var html = await View();
        var groups = Regex.Matches(html, "<div class=\"group\">(.*?)</div>\\s*(?=<div class=\"group\">|</div>)",
            RegexOptions.Singleline).Select(m => m.Value).ToList();

        var basket = Assert.Single(groups, g => g.Contains(AppText.Current.AddAllToBasket));

        Assert.Contains("name=\"checkOff\"", basket);
        Assert.DoesNotContain("showExcluded", basket);
    }

    [Fact]
    public async Task The_view_filter_is_its_own_group()
    {
        // It changes nothing on either service, so it does not belong beside the
        // controls that do.
        var html = await View();
        var filter = html[html.IndexOf("showExcluded\">", StringComparison.Ordinal)..];

        Assert.DoesNotContain(AppText.Current.AddAllToBasket, filter);
    }

    [Fact]
    public void The_account_line_carries_no_separator_that_can_strand()
    {
        // A literal middot between the two statements could end a line with
        // nothing after it once the header wrapped; spacing does that job now.
        var account = Regex.Match(Html.AppPage, "<div class=\"account[^\"]*\".*?</div>\\s*<div id=\"view\"",
            RegexOptions.Singleline).Value;

        Assert.NotEqual("", account);
        Assert.DoesNotContain("&middot;", account);
    }
}
