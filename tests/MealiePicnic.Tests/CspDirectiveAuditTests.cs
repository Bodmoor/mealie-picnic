using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using RazorSlices;

namespace MealiePicnic.Tests;

/// <summary>
/// Tripwire for issue #33: the app deliberately ships the @alpinejs/csp build so
/// it can run under a strict CSP with no 'unsafe-eval' (see Vendor.cs). That
/// build refuses a handful of directives outright at runtime -- x-html throws
/// "Using the x-html directive is prohibited in the CSP build" -- and the only
/// symptom is that the bound element silently stays empty. That is exactly how
/// the organic mark and salt figure went missing for three deploys: the fetch
/// worked, the endpoint answered correctly, and x-html quietly rendered nothing.
///
/// These pages are plain strings/templates, so nothing but a test like this
/// notices a prohibited directive before it reaches a browser.
/// </summary>
public class CspDirectiveAuditTests
{
    // x-html is the one that bit us. x-effect is likewise unavailable in the CSP
    // build (it evaluates an arbitrary expression), so it is guarded here too.
    private static readonly string[] Prohibited = ["x-html", "x-effect"];

    private static ShoppingItem Item() => new(
        ItemId: "00000000-0000-0000-0000-000000000001",
        FoodId: "00000000-0000-0000-0000-000000000002",
        Display: "melk", FoodName: "melk", Quantity: 1, Unit: "l", Label: "",
        Checked: false, State: LinkState.New,
        PicnicUid: null, PicnicLabel: null, PicnicPack: null);

    private static async Task<string> RenderHitsGridAsync() =>
        await HitsGrid.Create(new HitsModel(
            Item(),
            [new PicnicProduct("s1002202", "Volle melk", 119, "1 liter", "img1")],
            "00000000-0000-0000-0000-000000000003")).RenderAsync();

    [Fact]
    public async Task Hits_grid_uses_no_directive_the_csp_build_refuses()
    {
        var html = await RenderHitsGridAsync();

        foreach (var directive in Prohibited)
            Assert.DoesNotContain(directive, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Hits_grid_still_hands_the_product_id_to_the_facts_loader()
    {
        // The other half of the contract: dropping x-html must not also drop the
        // component, or the facts stay just as invisible (issue #26/#33).
        var html = await RenderHitsGridAsync();

        Assert.Contains("x-data=\"factsLoader\"", html);
        Assert.Contains("data-product-id=\"s1002202\"", html);
    }

    [Fact]
    public void App_shell_uses_no_directive_the_csp_build_refuses()
    {
        foreach (var directive in Prohibited)
            Assert.DoesNotContain(directive, Html.AppPage, StringComparison.OrdinalIgnoreCase);
    }
}
