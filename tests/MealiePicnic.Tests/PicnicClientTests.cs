using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealiePicnic.Tests;

public class PicnicClientTests
{
    /// <summary>
    /// Shaped like a real search-page-results payload: sellingUnit objects buried at
    /// varying depths, in relevance order, with one duplicate.
    /// </summary>
    private const string SearchPage = """
        {
          "script": { "someExpression": "(a,b) => a" },
          "layout": {
            "children": [
              { "content": { "sellingUnit": {
                  "id": "s1005080", "name": "Santa Maria tortillawraps large",
                  "display_price": 269, "unit_quantity": "6 stuks", "image_id": "img1" } } },
              { "children": [
                  { "content": { "sellingUnit": {
                      "id": "s1004843", "name": "Santa Maria volkoren",
                      "display_price": 275, "unit_quantity": "8 stuks", "image_id": "img2" } } },
                  { "content": { "sellingUnit": {
                      "id": "s1005080", "name": "duplicate tile",
                      "display_price": 269, "unit_quantity": "6 stuks", "image_id": "img1" } } }
              ] },
              { "content": { "sellingUnit": {
                  "id": "s1161238", "name": "Doritos cool american",
                  "display_price": 289, "unit_quantity": "272 gram", "image_id": "img3" } } }
            ]
          }
        }
        """;

    private static TokenStore Tokens(string? seed = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = TestFactory.Options(dir);
        var store = new TokenStore(options, NullLogger<TokenStore>.Instance);
        if (seed is not null) store.Save(seed);
        return store;
    }

    [Fact]
    public async Task Search_collects_selling_units_in_relevance_order()
    {
        var handler = new StubHandler().OnJson("search-page-results", SearchPage);

        var products = await TestFactory.Picnic(handler, tokens: Tokens("tok")).SearchAsync("wrap", default);

        // Document order is Picnic's own ranking, so it must be preserved.
        Assert.Equal(new[] { "s1005080", "s1004843", "s1161238" }, products.Select(p => p.Id));
    }

    [Fact]
    public async Task Search_deduplicates_repeated_products()
    {
        var handler = new StubHandler().OnJson("search-page-results", SearchPage);

        var products = await TestFactory.Picnic(handler, tokens: Tokens("tok")).SearchAsync("wrap", default);

        Assert.Equal(3, products.Count);
        Assert.Single(products, p => p.Id == "s1005080");
        Assert.Equal("Santa Maria tortillawraps large", products[0].Name);   // first tile wins
    }

    [Fact]
    public async Task Search_maps_price_and_unit()
    {
        var handler = new StubHandler().OnJson("search-page-results", SearchPage);

        var products = await TestFactory.Picnic(handler, tokens: Tokens("tok")).SearchAsync("wrap", default);

        Assert.Equal(269, products[0].DisplayPrice);
        Assert.Equal("6 stuks", products[0].UnitQuantity);
        Assert.Equal("img1", products[0].ImageId);
    }

    [Fact]
    public async Task Search_is_cached_per_term()
    {
        var handler = new StubHandler().OnJson("search-page-results", SearchPage);
        var client = TestFactory.Picnic(handler, tokens: Tokens("tok"));

        await client.SearchAsync("wrap", default);
        await client.SearchAsync("wrap", default);

        Assert.Single(handler.Sent);
    }

    [Fact]
    public async Task Forbidden_becomes_PicnicAuthException()
    {
        // What an un-verified 2FA token produces: 403 with an empty body.
        var handler = new StubHandler().OnStatus("search-page-results", HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<PicnicAuthException>(
            () => TestFactory.Picnic(handler, tokens: Tokens("tok")).SearchAsync("wrap", default));
    }

    [Fact]
    public async Task Login_without_credentials_throws_PicnicCredentialsException()
    {
        var handler = new StubHandler().OnJson("user/login", "{}");

        await Assert.ThrowsAsync<PicnicCredentialsException>(
            () => TestFactory.Picnic(handler).LoginAsync(null, null, default));

        Assert.Empty(handler.Sent);   // never reaches the network
    }

    [Fact]
    public async Task Login_accepts_credentials_from_arguments()
    {
        var handler = new StubHandler().OnJson("user/login",
            """{ "user_id": "u1", "second_factor_authentication_required": true }""");

        var needs2fa = await TestFactory.Picnic(handler)
            .LoginAsync("me@example.com", "hunter2", default);

        Assert.True(needs2fa);

        var body = JsonNode.Parse(handler.Sent.Single().Body)!;
        Assert.Equal("me@example.com", body["key"]!.GetValue<string>());
        Assert.Equal(30100, body["client_id"]!.GetValue<int>());
        // Picnic wants md5(password) as a lowercase hex digest.
        Assert.Equal("2ab96390c7dbe3439de74d0c9b0b1767", body["secret"]!.GetValue<string>());
    }

    [Fact]
    public async Task Login_detects_error_body_returned_with_http_200()
    {
        // Picnic answers bad credentials with 200 and an error object.
        var handler = new StubHandler().OnJson("user/login",
            """{ "error": { "code": "AUTH_INVALID_CRED", "message": "nope" } }""");

        var ex = await Assert.ThrowsAsync<PicnicAuthException>(
            () => TestFactory.Picnic(handler).LoginAsync("me@example.com", "wrong", default));

        Assert.Contains("AUTH_INVALID_CRED", ex.Message);
    }

    [Fact]
    public async Task Captures_refreshed_token_from_response_header()
    {
        // 2FA verification returns 204 plus a NEW x-picnic-auth header.
        var tokens = Tokens("old-token");
        var handler = new StubHandler().On("2fa/verify", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.NoContent)
            {
                Content = new StringContent(""),
            };
            response.Headers.Add("x-picnic-auth", "fresh-token");
            return response;
        });

        await TestFactory.Picnic(handler, tokens: tokens).Verify2faAsync("123456", default);

        Assert.Equal("fresh-token", tokens.Token());
    }

    [Fact]
    public async Task Sends_required_picnic_headers()
    {
        var inspector = new HeaderInspector();
        var tokens = Tokens("tok");

        await TestFactory.Picnic(inspector, tokens: tokens).SearchAsync("wrap", default);

        Assert.Equal("tok", inspector.Headers["x-picnic-auth"]);
        // Per-install random device id; Picnic binds 2FA to it, so it must be the
        // store's stable value, not a constant shared by every deployment.
        Assert.Equal(tokens.DeviceId(), inspector.Headers["x-picnic-did"]);
        Assert.Matches("^[A-F0-9]{16}$", inspector.Headers["x-picnic-did"]);
        Assert.Contains("30100;", inspector.Headers["x-picnic-agent"]);
        Assert.Equal("okhttp/4.9.0", inspector.Headers["User-Agent"]);
    }

    [Fact]
    public void Reports_which_credentials_configuration_provides()
    {
        // TestFactory.Options sets neither, so the UI must ask for both.
        var client = TestFactory.Picnic(new StubHandler());

        Assert.False(client.HasConfiguredUser);
        Assert.False(client.HasConfiguredPassword);
        Assert.True(client.NeedsCredentials);
        Assert.Equal("", client.ConfiguredUser);
    }

    [Fact]
    public async Task Logout_clears_the_cached_token()
    {
        var tokens = Tokens("tok");
        var handler = new StubHandler().OnStatus("user/logout", HttpStatusCode.OK);

        await TestFactory.Picnic(handler, tokens: tokens).LogoutAsync(default);

        Assert.Null(tokens.Token());
        Assert.Contains(handler.Sent, c => c.Url.Contains("user/logout"));
    }

    [Fact]
    public async Task Logout_clears_the_token_even_when_the_remote_call_fails()
    {
        // Otherwise a token Picnic already rejects could never be cleared.
        var tokens = Tokens("stale");
        var handler = new StubHandler().OnStatus("user/logout", HttpStatusCode.Forbidden);

        await TestFactory.Picnic(handler, tokens: tokens).LogoutAsync(default);

        Assert.Null(tokens.Token());
    }

    [Fact]
    public async Task Logout_without_a_token_does_not_call_picnic()
    {
        var handler = new StubHandler();

        await TestFactory.Picnic(handler, tokens: Tokens()).LogoutAsync(default);

        Assert.Empty(handler.Sent);
    }

    // ---------------------------------------------------------------- OIDC per-user storage

    private static ClaimsPrincipal Principal(string sub) =>
        new(new ClaimsIdentity([new Claim("sub", sub)], "test"));

    [Fact]
    public void Tokens_are_isolated_per_user_once_oidc_is_enabled()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = TestFactory.Options(dir, oidcEnabled: true);
        var tokens = new TokenStore(options, NullLogger<TokenStore>.Instance);

        var alice = Principal("alice-sub");
        var bob = Principal("bob-sub");
        tokens.Save("alice-token", UserKey.From(alice));

        // IHttpContextAccessor.HttpContext is backed by a process-wide AsyncLocal,
        // so building both clients before asserting on either would have the
        // second constructor's context silently win for both -- assert on each
        // client right after it is built instead of batching the pair.
        Assert.True(TestFactory.Picnic(new StubHandler(), options, tokens, alice).HasToken);
        Assert.False(TestFactory.Picnic(new StubHandler(), options, tokens, bob).HasToken);
    }

    [Fact]
    public void Token_is_shared_across_identities_when_oidc_is_disabled()
    {
        // TestFactory.Options defaults to OIDC disabled: whoever is signed in,
        // PicnicClient must keep reading the one shared, legacy token slot.
        var tokens = Tokens("shared-token");
        var client = TestFactory.Picnic(new StubHandler(), tokens: tokens, user: Principal("alice-sub"));

        Assert.True(client.HasToken);
    }

    private sealed class HeaderInspector : StubHandler
    {
        public Dictionary<string, string> Headers { get; } = [];

        public HeaderInspector() => OnJson("search-page-results", "{}");

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
                Headers[header.Key] = string.Join(",", header.Value);
            return base.SendAsync(request, cancellationToken);
        }
    }

    [Theory]
    [InlineData("../../api/15/user")]
    [InlineData("abc/def")]
    [InlineData("")]
    public async Task Rejects_bad_image_ids_before_any_request(string imageId)
    {
        // B3: route values arrive URL-decoded, so ..%2F..%2F would walk up the
        // Picnic storefront path -- a limited SSRF. Nothing must leave the process.
        var handler = new StubHandler();

        await Assert.ThrowsAsync<ArgumentException>(() => TestFactory
            .Picnic(handler, tokens: Tokens("tok")).GetImageAsync(imageId, "medium", default));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Rejects_unknown_image_sizes()
    {
        var handler = new StubHandler();

        await Assert.ThrowsAsync<ArgumentException>(() => TestFactory
            .Picnic(handler, tokens: Tokens("tok")).GetImageAsync("abc123", "../../etc", default));

        Assert.Empty(handler.Sent);
    }

    // ---------------------------------------------------------------- issue #7

    private const string CartWith = """
        { "items": [ { "items": [ { "id": "s1005080", "name": "Wraps" } ] } ],
          "total_count": 1 }
        """;

    private const string EmptyCart = """
        { "items": [], "total_count": 0 }
        """;

    [Fact]
    public async Task Add_succeeds_when_the_product_is_in_the_returned_cart()
    {
        var handler = new StubHandler().OnJson("cart/add_product", CartWith);

        await TestFactory.Picnic(handler, tokens: Tokens("tok"))
            .AddToCartAsync("s1005080", 1, default);

        var body = JsonNode.Parse(handler.Sent.Single().Body)!;
        Assert.Equal("s1005080", body["product_id"]!.GetValue<string>());
        Assert.Equal(1, body["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task Add_throws_when_picnic_returns_an_error_body_under_200()
    {
        // The trap behind issue #7: HTTP 200, but nothing was added. Treating this
        // as success ticked the Mealie item off and lost the line.
        var handler = new StubHandler().OnJson("cart/add_product",
            """{ "error": { "code": "MAX_COUNT_EXCEEDED", "message": "nope" } }""");

        var ex = await Assert.ThrowsAsync<PicnicCartException>(() => TestFactory
            .Picnic(handler, tokens: Tokens("tok")).AddToCartAsync("s1005080", 500, default));

        Assert.Contains("MAX_COUNT_EXCEEDED", ex.Message);
    }

    [Fact]
    public async Task Add_throws_when_the_product_is_absent_from_the_returned_cart()
    {
        var handler = new StubHandler().OnJson("cart/add_product", EmptyCart);

        await Assert.ThrowsAsync<PicnicCartException>(() => TestFactory
            .Picnic(handler, tokens: Tokens("tok")).AddToCartAsync("s1005080", 1, default));
    }

    [Fact]
    public async Task Add_accepts_an_empty_response_body()
    {
        // Some endpoints answer 204. Absence of a cart is not evidence of failure.
        var handler = new StubHandler().OnStatus("cart/add_product", HttpStatusCode.NoContent);

        await TestFactory.Picnic(handler, tokens: Tokens("tok"))
            .AddToCartAsync("s1005080", 1, default);
    }

    // ---------------------------------------------------------------- issue #6

    /// <summary>
    /// Shaped like a real product-details page: the product's own blocks carry the
    /// ids this parser looks for, the nutrition table lives in an accordion item,
    /// and an "alternatives-container" holds a competing product that happens to be
    /// organic and salt-free. That last block is the whole point of the fixture.
    /// </summary>
    private const string ProductPage = """
        {
          "layout": { "body": { "children": [
            { "id": "product-details-page-root-main-container", "pml": { "component": {
                "children": [
                  { "textType": "HEADER1", "markdown": "Volle melk" },
                  { "markdown": "Campina" },
                  { "type": "STACK", "children": [
                      { "type": "RICH_TEXT", "markdown": "1 liter" },
                      { "type": "RICH_TEXT", "markdown": "#(#9AA0A6)EUR 1,19/l" }
                  ] }
                ] } } },
            { "id": "product-page-highlights", "children": [
                { "markdown": "**Van Nederlandse koeien**" },
                { "markdown": "Bron van calcium" }
            ] },
            { "id": "description", "children": [
                { "markdown": "Verse volle melk, gepasteuriseerd." }
            ] },
            { "id": "accordion-list", "items": [
                { "header": { "markdown": "**Ingredienten**" },
                  "body": { "markdown": "Volle melk" } },
                { "header": { "markdown": "**Voedingswaarde**" },
                  "body": { "children": [
                      { "markdown": "Per 100 ml" },
                      { "markdown": "Energie" }, { "markdown": "272 kJ" },
                      { "markdown": "Zout" }, { "markdown": "0,11 g" }
                  ] } }
            ] },
            { "id": "alternatives-container", "children": [
                { "content": { "sellingUnit": {
                    "id": "s2000001", "name": "Biologische volle melk",
                    "display_price": 159, "unit_quantity": "1 liter", "image_id": "img9" } },
                  "markdown": "Biologisch, zonder zout" }
            ] }
          ] } }
        }
        """;

    [Fact]
    public async Task Details_reads_the_salt_from_the_nutrition_accordion()
    {
        var handler = new StubHandler().OnJson("product-details-page-root", ProductPage);

        var details = await TestFactory.Picnic(handler, tokens: Tokens("tok"))
            .GetDetailsAsync("s1000001", default);

        Assert.Equal(0.11, details.SaltGramsPer100!.Value, 3);
        Assert.Equal("0,11 g", details.SaltText);
    }

    [Fact]
    public async Task Details_ignores_the_organic_claim_of_a_suggested_alternative()
    {
        // The trap: this page recommends an organic alternative. Scanning the whole
        // tree would brand the conventional product organic -- worse than showing
        // nothing at all.
        var handler = new StubHandler().OnJson("product-details-page-root", ProductPage);

        var details = await TestFactory.Picnic(handler, tokens: Tokens("tok"))
            .GetDetailsAsync("s1000001", default);

        Assert.False(details.Organic);
    }

    [Fact]
    public async Task Details_reads_an_organic_claim_from_the_products_own_blocks()
    {
        var page = ProductPage.Replace("Bron van calcium", "Biologisch, Skal-gecertificeerd");
        var handler = new StubHandler().OnJson("product-details-page-root", page);

        var details = await TestFactory.Picnic(handler, tokens: Tokens("tok"))
            .GetDetailsAsync("s1000001", default);

        Assert.True(details.Organic);
    }

    [Fact]
    public async Task Details_requests_the_product_page_once_and_caches_it()
    {
        var handler = new StubHandler().OnJson("product-details-page-root", ProductPage);
        var picnic = TestFactory.Picnic(handler, tokens: Tokens("tok"));

        await picnic.GetDetailsAsync("s1000001", default);
        await picnic.GetDetailsAsync("s1000001", default);

        // One card scrolling in and out of view must not re-fetch a page that
        // cannot change between deliveries.
        Assert.Single(handler.Sent, x => x.Url.Contains("product-details-page-root"));
        Assert.Contains("id=s1000001", handler.Sent[0].Url);
    }

    [Fact]
    public async Task Details_degrade_to_empty_when_the_page_layout_is_unrecognised()
    {
        // Picnic renames a node id: both features go quiet, nothing throws, and the
        // card still links.
        var handler = new StubHandler().OnJson("product-details-page-root",
            """{ "layout": { "body": { "children": [ { "id": "something-new" } ] } } }""");

        var details = await TestFactory.Picnic(handler, tokens: Tokens("tok"))
            .GetDetailsAsync("s1000001", default);

        Assert.False(details.Organic);
        Assert.Null(details.SaltGramsPer100);
    }

    [Theory]
    [InlineData("s1000001/../../user")]
    [InlineData("s1 000001")]
    [InlineData("")]
    public async Task Details_refuses_a_product_id_that_could_reshape_the_url(string id)
    {
        var handler = new StubHandler().OnJson("product-details-page-root", ProductPage);

        await Assert.ThrowsAsync<ArgumentException>(() => TestFactory
            .Picnic(handler, tokens: Tokens("tok")).GetDetailsAsync(id, default));
    }
}
