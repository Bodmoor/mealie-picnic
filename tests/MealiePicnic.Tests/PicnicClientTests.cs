using System.Net;
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

        Assert.Equal("fresh-token", tokens.Token);
    }

    [Fact]
    public async Task Sends_required_picnic_headers()
    {
        var inspector = new HeaderInspector();

        await TestFactory.Picnic(inspector, tokens: Tokens("tok")).SearchAsync("wrap", default);

        Assert.Equal("tok", inspector.Headers["x-picnic-auth"]);
        // Picnic binds 2FA verification to the device id, so it must not drift.
        Assert.Equal("3C417201548B2E3B", inspector.Headers["x-picnic-did"]);
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

        Assert.Null(tokens.Token);
        Assert.Contains(handler.Sent, c => c.Url.Contains("user/logout"));
    }

    [Fact]
    public async Task Logout_clears_the_token_even_when_the_remote_call_fails()
    {
        // Otherwise a token Picnic already rejects could never be cleared.
        var tokens = Tokens("stale");
        var handler = new StubHandler().OnStatus("user/logout", HttpStatusCode.Forbidden);

        await TestFactory.Picnic(handler, tokens: tokens).LogoutAsync(default);

        Assert.Null(tokens.Token);
    }

    [Fact]
    public async Task Logout_without_a_token_does_not_call_picnic()
    {
        var handler = new StubHandler();

        await TestFactory.Picnic(handler, tokens: Tokens()).LogoutAsync(default);

        Assert.Empty(handler.Sent);
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
}
