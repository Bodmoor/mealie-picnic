using System.Net;
using System.Text.RegularExpressions;
using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using Microsoft.AspNetCore.Mvc.Testing;
using RazorSlices;

namespace MealiePicnic.Tests;

/// <summary>
/// The browser's Back button (issue #74). Two halves: the app's own views have to
/// become history entries at all, and the entry Back used to land on -- the login
/// page -- has to stop showing itself to a live session.
/// </summary>
[Collection(AppTextCollection.Name)]
public class BrowserHistoryTests
{
    private const string Password = "correct-horse-battery-staple";
    private const string ListId = "00000000-0000-0000-0000-000000000003";
    private const string FoodId = "00000000-0000-0000-0000-000000000002";

    private static WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            builder.UseSetting("APP_PASSWORD", Password);
            builder.UseSetting("DATA_DIR", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            builder.UseSetting("BOODSCHAPPEN_OIDC_AUTHORITY", "");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_ID", "");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_SECRET", "");
        });

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static async Task<string> SignIn(HttpClient client)
    {
        var login = await client.PostAsync("/login",
            new FormUrlEncodedContent([new KeyValuePair<string, string>("password", Password)]));
        return login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("mealiepicnic=")).Split(';')[0];
    }

    // -------------------------------------------------------- the login entry

    [Fact]
    public async Task Login_sends_an_already_signed_in_caller_onward()
    {
        // What Back landed on. Showing a sign-in page to a live session is the
        // reported symptom.
        using var factory = NewFactory();
        var client = NewClient(factory);
        var cookie = await SignIn(client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/login");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Login_still_shows_the_form_to_someone_who_is_not_signed_in()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("//evil.test/", "/")]
    [InlineData("https://evil.test/", "/")]
    [InlineData("/api/list?listId=x", "/api/list?listId=x")]
    public async Task A_return_url_is_followed_only_when_it_is_a_path_on_this_site(string returnUrl, string expected)
    {
        // A redirect parameter an attacker can set is worth more to them than the
        // page they would be redirecting away from. "//evil.test" is the one that
        // catches people: protocol-relative, not local.
        using var factory = NewFactory();
        var client = NewClient(factory);
        var cookie = await SignIn(client);

        var request = new HttpRequestMessage(HttpMethod.Get, "/login?ReturnUrl=" + Uri.EscapeDataString(returnUrl));
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        Assert.Equal(expected, response.Headers.Location!.ToString());
    }

    // ------------------------------------------------- fragment versus page

    [Fact]
    public async Task A_view_navigated_to_directly_comes_back_as_a_whole_page()
    {
        // Once these URLs are in history they can be reloaded, shared, or restored
        // past htmx's cache. A bare fragment would render unstyled, with no shell.
        using var factory = NewFactory();
        var client = NewClient(factory);
        var cookie = await SignIn(client);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/items/{FoodId}?listId={ListId}");
        request.Headers.Add("Cookie", cookie);
        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.StartsWith("<!doctype html>", body);
        // And it boots into the view that was asked for, not the default list.
        Assert.Contains($"hx-get=\"/api/items/{FoodId}?listId={ListId}\"", body);
    }

    [Fact]
    public async Task The_same_view_stays_a_fragment_for_htmx()
    {
        using var factory = NewFactory();
        var client = NewClient(factory);
        var cookie = await SignIn(client);

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/items/{FoodId}?listId={ListId}");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("HX-Request", "true");
        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<!doctype html>", body);
    }

    [Fact]
    public void The_boot_url_cannot_carry_markup_or_point_off_site()
    {
        // It is the request's own path and query, so it is caller-controlled: the
        // quote that would close the attribute must not survive as a quote.
        var booted = Html.BootingInto("/api/list?x=\"><script>alert(1)</script>");

        Assert.DoesNotContain("<script>alert(1)", booted);
        Assert.Contains("hx-get=\"/api/list?x=", booted);
        Assert.Throws<ArgumentException>(() => Html.BootingInto("https://evil.test/"));
        Assert.Throws<ArgumentException>(() => Html.BootingInto("//evil.test/"));
    }

    // ----------------------------------------------------- the history entries

    [Fact]
    public async Task Navigating_between_views_pushes_history()
    {
        // Without this the whole app is one entry at "/", and Back means "leave".
        var item = new ShoppingItem(
            ItemId: "00000000-0000-0000-0000-000000000001", FoodId: FoodId,
            Display: "melk", FoodName: "melk", Quantity: 1, Unit: "l", Label: "",
            Checked: false, State: LinkState.New, PicnicUid: null, PicnicLabel: null, PicnicPack: null);

        var list = await ListView.Create(new ListViewModel(
            [new ShoppingListSummary(ListId, "Boodschappen")], ListId, [item])).RenderAsync();
        var search = await SearchView.Create(new SearchViewModel(item, ListId)).RenderAsync();
        var hits = await HitsGrid.Create(new HitsModel(
            item, [new PicnicProduct("s1002202", "Volle melk", 119, "1 liter", "img1")], ListId)).RenderAsync();

        // list -> search view, search view -> back to list, card -> product detail
        Assert.Matches(new Regex("class=\"item\"[^>]*hx-push-url=\"true\""), list);
        Assert.Contains("hx-push-url=\"true\"", search);
        // The details button specifically, not merely somewhere in the grid: the
        // linking button beside it must NOT push, since linking is an action
        // rather than a place, and a history entry for it would send Back
        // somewhere that no longer describes what is on screen.
        Assert.Contains("hx-push-url=\"true\">i</button>", hits);
        Assert.DoesNotContain("hx-vals=\"@linkVals\" hx-push-url", hits);
    }
}
