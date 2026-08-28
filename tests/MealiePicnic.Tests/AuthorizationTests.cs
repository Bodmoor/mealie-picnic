using Microsoft.AspNetCore.Mvc.Testing;

namespace MealiePicnic.Tests;

/// <summary>
/// The single most security-critical behaviour in the app: every route except the
/// login page requires authentication (via the fallback policy), the login form
/// behaves correctly, and repeated failures hit the rate limiter.
///
/// Each test gets its own factory: the rate limiter partitions on remote IP, which
/// the TestServer does not set, so all requests in one factory share a partition —
/// a shared factory would leak rate-limit state between tests.
/// </summary>
public class AuthorizationTests
{
    private const string Password = "correct-horse-battery-staple";

    private static WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            builder.UseSetting("APP_PASSWORD", Password);
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            // WebApplicationFactory loads the developer's real user secrets in
            // Development regardless of the settings above, so a developer who
            // has OIDC_* set locally (for manual testing) would otherwise flip
            // these OIDC-disabled tests over to OIDC-enabled behaviour.
            builder.UseSetting("BOODSCHAPPEN_OIDC_AUTHORITY", "");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_ID", "");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_SECRET", "");
        });

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,   // assert on the redirect itself
        });

    public static TheoryData<string> ProtectedGets => new()
    {
        "/",
        "/api/lists",
        "/api/list",
        "/api/search?term=melk",
        "/api/details/s1005080",
        "/api/image/abc",
        "/api/cart",
        "/api/picnic/status",
    };

    public static TheoryData<string> ProtectedPosts => new()
    {
        "/logout",
        "/api/link",
        "/api/exclude",
        "/api/include",
        "/api/basket",
        "/api/lists/select",
        "/api/picnic/login",
        "/api/picnic/logout",
        "/api/picnic/2fa/generate",
        "/api/picnic/2fa/verify",
    };

    /// <summary>
    /// A denial is either a 302 to the login page (browser navigation) or a bare
    /// 401 -- the cookie handler picks per request. Both satisfy the requirement;
    /// asserting one specific mechanism made the test brittle rather than stricter.
    /// What must never happen is the endpoint running, so this also checks no
    /// session cookie is issued and no app content comes back.
    /// </summary>
    private static async Task AssertDeniedAsync(HttpResponseMessage response)
    {
        Assert.True(
            response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Redirect,
            $"expected 401 or 302, got {(int)response.StatusCode}");

        if (response.StatusCode is System.Net.HttpStatusCode.Redirect)
            Assert.Contains("/login", response.Headers.Location!.ToString());

        Assert.False(response.Headers.Contains("Set-Cookie"));

        // The SPA shell must not leak to an anonymous caller.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Haal Mealie lijst op", body);
    }

    [Theory]
    [MemberData(nameof(ProtectedGets))]
    public async Task Unauthenticated_GET_is_denied(string path)
    {
        using var factory = NewFactory();
        await AssertDeniedAsync(await NewClient(factory).GetAsync(path));
    }

    [Theory]
    [MemberData(nameof(ProtectedPosts))]
    public async Task Unauthenticated_POST_is_denied(string path)
    {
        using var factory = NewFactory();
        await AssertDeniedAsync(await NewClient(factory)
            .PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json")));
    }

    [Fact]
    public async Task Login_page_is_anonymous()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync("/login");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_sets_no_cookie_and_shows_the_error()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).PostAsync("/login",
            new FormUrlEncodedContent([new("password", "wrong")]));

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Onjuist wachtwoord", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Correct_password_sets_an_httponly_session_cookie()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).PostAsync("/login",
            new FormUrlEncodedContent([new("password", Password)]));

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.ToString());

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("mealiepicnic="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        // Lax, not Strict: an OIDC login redirect is a legitimate cross-site
        // entry point, and Strict would not survive it (see Program.cs).
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Session_cookie_grants_access_to_the_app()
    {
        using var factory = NewFactory();
        var client = NewClient(factory);

        var login = await client.PostAsync("/login",
            new FormUrlEncodedContent([new("password", Password)]));
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("mealiepicnic=")).Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("Cookie", cookie);
        var home = await client.SendAsync(request);

        Assert.Equal(System.Net.HttpStatusCode.OK, home.StatusCode);
    }

    [Fact]
    public async Task Sixth_login_attempt_in_a_minute_is_rate_limited()
    {
        using var factory = NewFactory();
        var client = NewClient(factory);

        for (var i = 0; i < 5; i++)
        {
            var attempt = await client.PostAsync("/login",
                new FormUrlEncodedContent([new("password", "wrong")]));
            Assert.Equal(System.Net.HttpStatusCode.OK, attempt.StatusCode);
        }

        var sixth = await client.PostAsync("/login",
            new FormUrlEncodedContent([new("password", "wrong")]));

        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, sixth.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync("/login");

        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task Logout_via_GET_is_rejected()
    {
        // State-changing GETs are cross-site triggerable; /logout is POST-only now.
        using var factory = NewFactory();
        var client = NewClient(factory);

        var login = await client.PostAsync("/login",
            new FormUrlEncodedContent([new("password", Password)]));
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith("mealiepicnic=")).Split(';')[0];

        var request = new HttpRequestMessage(HttpMethod.Get, "/logout");
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        // 405 (method mismatch) or 404 (no GET endpoint) both prove the point:
        // a GET can no longer end the session.
        Assert.True(response.StatusCode is System.Net.HttpStatusCode.MethodNotAllowed
                        or System.Net.HttpStatusCode.NotFound,
            $"GET /logout returned {(int)response.StatusCode}; it must not succeed.");
    }

    public static TheoryData<string, string> PwaAssets => new()
    {
        { "/favicon.ico", "image/png" },
        { "/icons/192.png", "image/png" },
        { "/icons/512.png", "image/png" },
        { "/icons/maskable-512.png", "image/png" },
        { "/manifest.webmanifest", "application/manifest+json" },
        { "/sw.js", "text/javascript" },
    };

    [Theory]
    [MemberData(nameof(PwaAssets))]
    public async Task Pwa_assets_are_anonymous(string path, string contentType)
    {
        // Android fetches these before login; a 302 makes the app look
        // uninstallable, so they must be reachable without a session.
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Unknown_icon_name_is_not_found()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync("/icons/../appsettings.json");

        Assert.NotEqual(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Manifest_declares_the_icons_it_serves()
    {
        using var factory = NewFactory();
        var client = NewClient(factory);

        var manifest = await client.GetStringAsync("/manifest.webmanifest");
        using var json = System.Text.Json.JsonDocument.Parse(manifest);

        Assert.Equal("standalone", json.RootElement.GetProperty("display").GetString());

        // Every declared icon must actually resolve, or install silently fails.
        foreach (var icon in json.RootElement.GetProperty("icons").EnumerateArray())
        {
            var src = icon.GetProperty("src").GetString()!;
            var response = await client.GetAsync(src);
            Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        }
    }
}
