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
///
/// Issue #43 went through the gap this class originally left: not a refused
/// directive, but an expression *shape* the build cannot parse. Its evaluator
/// takes exactly one expression per directive -- the parser accepts one
/// expression plus an optional trailing semicolon and throws on anything after
/// it, and the interpreter has no node type for a statement sequence -- so
/// x-init="$store.picnic.refresh(); $store.me.refresh()" threw on parse and
/// neither call ran. Same silent shape, different cause, so the shape is guarded
/// here too.
/// </summary>
public class CspDirectiveAuditTests
{
    // x-html is the one that bit us. x-effect is likewise unavailable in the CSP
    // build (it evaluates an arbitrary expression), so it is guarded here too.
    private static readonly string[] Prohibited = ["x-html", "x-effect"];

    // Every Alpine directive attribute and its expression. Deliberately scoped to
    // x-* so an ordinary style="a:1;b:2" is not mistaken for one.
    private static readonly System.Text.RegularExpressions.Regex Directive =
        new(@"\b(?<name>x-[a-z][a-z0-9:._-]*)=""(?<expression>[^""]*)""",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Directives whose expression holds more than one statement. A trailing
    /// semicolon is fine -- the parser consumes exactly one -- so only a
    /// semicolon with something after it is a fault.
    /// </summary>
    private static List<string> MultiStatementDirectives(string html) =>
        Directive.Matches(html)
            .Where(m =>
            {
                var expression = m.Groups["expression"].Value.TrimEnd();
                var semicolon = expression.IndexOf(';');
                return semicolon >= 0 && semicolon < expression.Length - 1;
            })
            .Select(m => $"{m.Groups["name"].Value}=\"{m.Groups["expression"].Value}\"")
            .ToList();

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

    // ---------------------------------------------------- expression shapes

    [Fact]
    public void App_shell_has_no_directive_holding_two_statements()
    {
        // Issue #43. The failure is invisible in the markup and invisible in the
        // browser except for one console line, so this is the only place it gets
        // caught before a deploy.
        var offenders = MultiStatementDirectives(Html.AppPage);

        Assert.True(offenders.Count == 0,
            "The CSP build parses one expression per directive; these hold two:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public async Task Rendered_slices_have_no_directive_holding_two_statements()
    {
        var offenders = MultiStatementDirectives(await RenderHitsGridAsync());

        Assert.True(offenders.Count == 0,
            "The CSP build parses one expression per directive; these hold two:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void The_two_statement_check_recognises_the_shape_that_broke_issue_43()
    {
        // Guarding the guard: a tripwire that silently matched nothing would be
        // worse than none, since it reads as coverage. This is the exact markup
        // that shipped broken.
        const string broken =
            """<div x-data x-init="$store.picnic.refresh(); $store.me.refresh()"></div>""";

        Assert.Single(MultiStatementDirectives(broken));

        // ...and these must not be flagged: one call, a trailing semicolon, and
        // an object literal whose braces contain no statement break.
        Assert.Empty(MultiStatementDirectives("""<div x-init="$store.me.refresh()"></div>"""));
        Assert.Empty(MultiStatementDirectives("""<div x-init="$store.me.refresh();"></div>"""));
        Assert.Empty(MultiStatementDirectives("""<div x-data="{ showExcluded: false }"></div>"""));

        // A style attribute is not a directive, and several carry semicolons.
        Assert.Empty(MultiStatementDirectives("""<input style="padding:7px;border:0">"""));
    }
}
