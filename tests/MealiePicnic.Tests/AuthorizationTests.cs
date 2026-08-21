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
        "/api/product/s123",
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
        "/api/picnic/login",
        "/api/picnic/logout",
        "/api/picnic/2fa/generate",
        "/api/picnic/2fa/verify",
    };

    [Theory]
    [MemberData(nameof(ProtectedGets))]
    public async Task Unauthenticated_GET_is_redirected_to_login(string path)
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync(path);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());
    }

    [Theory]
    [MemberData(nameof(ProtectedPosts))]
    public async Task Unauthenticated_POST_is_redirected_to_login(string path)
    {
        using var factory = NewFactory();
        var response = await NewClient(factory)
            .PostAsync(path, new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/login", response.Headers.Location!.ToString());
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
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
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
}
