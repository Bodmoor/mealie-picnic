using System.Net;
using MealiePicnic.Clients;
using MealiePicnic.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealiePicnic.Tests;

/// <summary>
/// The only path in this app that spends money (issue #62). It lived inside an
/// endpoint lambda until now, which is why none of this was covered: reaching it
/// meant a WebApplicationFactory and two stubbed upstreams, so the first test was
/// expensive enough that it never got written.
/// </summary>
[Collection(AppTextCollection.Name)]
public class BasketRunTests
{
    private static ShoppingItem Linked(string food, string uid, int amount, string? itemId = null) =>
        new(ItemId: itemId ?? Guid.NewGuid().ToString(),
            FoodId: Guid.NewGuid().ToString(),
            Display: food, FoodName: food, Quantity: amount, Unit: "", Label: "",
            Checked: false, State: LinkState.Linked,
            PicnicUid: uid, PicnicLabel: food, PicnicPack: null);

    /// <summary>A cart answer containing the given selling units.</summary>
    private static string Cart(params string[] uids) =>
        $$"""{ "items": [ {{string.Join(",", uids.Select(u => $$"""{ "id": "{{u}}" }"""))}} ] }""";

    private static BasketRun Run(StubHandler picnicHandler, StubHandler? mealieHandler = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = TestFactory.Options(dir);
        var tokens = new TokenStore(options, NullLogger<TokenStore>.Instance);
        tokens.Save("tok");
        return new BasketRun(
            TestFactory.Picnic(picnicHandler, options, tokens),
            TestFactory.Mealie(mealieHandler ?? new StubHandler()),
            NullLogger<BasketRun>.Instance);
    }

    // ------------------------------------------------------------------ amounts

    [Fact]
    public async Task Two_foods_linked_to_one_product_are_sent_as_a_single_summed_line()
    {
        // Issue #27. Two separate lines would order the product twice.
        var picnic = new StubHandler().OnJson("cart/products/add", Cart("s1005080"));

        await Run(picnic).ExecuteAsync(
            [Linked("wrap", "s1005080", 2), Linked("tortilla", "s1005080", 3)], checkOff: false, default);

        var body = Assert.Single(picnic.Sent).Body;

        Assert.Contains("\"s1005080\":5", body.Replace(" ", ""));
    }

    [Fact]
    public async Task An_empty_basket_makes_no_upstream_call_at_all()
    {
        var picnic = new StubHandler().OnJson("cart/products/add", Cart());

        var outcome = await Run(picnic).ExecuteAsync([], checkOff: false, default);

        Assert.Empty(outcome.Results);
        Assert.False(outcome.Aborted);
        Assert.Empty(picnic.Sent);
    }

    // ------------------------------------------------------ what actually landed

    [Fact]
    public async Task A_product_missing_from_the_returned_cart_is_reported_as_refused()
    {
        // Issue #7: a 200 is not evidence. Picnic answers with the updated cart,
        // and only what is in it was actually added.
        var picnic = new StubHandler().OnJson("cart/products/add", Cart("s1005080"));

        var outcome = await Run(picnic).ExecuteAsync(
            [Linked("wrap", "s1005080", 1), Linked("melk", "s1002202", 1)], checkOff: false, default);

        var wrap = Assert.Single(outcome.Results, r => r.Uid == "s1005080");
        var milk = Assert.Single(outcome.Results, r => r.Uid == "s1002202");

        Assert.True(wrap.Ok);
        Assert.False(milk.Ok);
        Assert.Equal(AppText.Current.BasketPicnicRefused, milk.Error);
        Assert.False(outcome.Aborted);
    }

    // ------------------------------------------------------------- check-off

    [Fact]
    public async Task A_failed_check_off_still_reports_the_add_as_successful()
    {
        // The goods ARE in the basket. Reporting the line as failed would invite a
        // retry and a double order; it is a successful add still on the Mealie
        // list instead.
        var picnic = new StubHandler().OnJson("cart/products/add", Cart("s1005080"));
        var mealie = new StubHandler().OnStatus("shopping/items", HttpStatusCode.InternalServerError);

        var outcome = await Run(picnic, mealie).ExecuteAsync(
            [Linked("wrap", "s1005080", 1)], checkOff: true, default);

        var line = Assert.Single(outcome.Results);

        Assert.True(line.Ok);
        Assert.False(line.CheckedOff);
        Assert.Null(line.Error);
    }

    [Fact]
    public async Task Nothing_is_checked_off_when_the_caller_did_not_ask_for_it()
    {
        var picnic = new StubHandler().OnJson("cart/products/add", Cart("s1005080"));
        var mealie = new StubHandler();

        var outcome = await Run(picnic, mealie).ExecuteAsync(
            [Linked("wrap", "s1005080", 1)], checkOff: false, default);

        Assert.False(Assert.Single(outcome.Results).CheckedOff);
        Assert.Empty(mealie.Sent);
    }

    // ----------------------------------------------------------------- failure

    [Fact]
    public async Task An_upstream_failure_aborts_every_line_with_the_upstream_message()
    {
        var picnic = new StubHandler().OnStatus("cart/products/add", HttpStatusCode.BadGateway);

        var outcome = await Run(picnic).ExecuteAsync(
            [Linked("wrap", "s1005080", 1), Linked("melk", "s1002202", 1)], checkOff: false, default);

        Assert.True(outcome.Aborted);
        Assert.Equal(2, outcome.Results.Count);
        Assert.All(outcome.Results, r => Assert.False(r.Ok));
        Assert.All(outcome.Results, r => Assert.Equal(AppText.Current.BasketUpstreamFailed, r.Error));
    }

    [Fact]
    public async Task An_auth_failure_is_rethrown_rather_than_turned_into_failed_lines()
    {
        // The 401 middleware turns this into the login dialog. Folding it into
        // per-line errors would leave the user reading "Picnic refused" with no
        // way to sign in again.
        var picnic = new StubHandler().OnStatus("cart/products/add", HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<PicnicAuthException>(() =>
            Run(picnic).ExecuteAsync([Linked("wrap", "s1005080", 1)], checkOff: false, default));
    }

    [Fact]
    public async Task A_cancelled_request_stops_the_run()
    {
        var picnic = new StubHandler().OnJson("cart/products/add", Cart("s1005080"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Run(picnic).ExecuteAsync([Linked("wrap", "s1005080", 1)], checkOff: false, cancelled.Token));
    }
}
