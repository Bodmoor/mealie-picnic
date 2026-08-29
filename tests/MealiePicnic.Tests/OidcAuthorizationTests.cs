using System.Net;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace MealiePicnic.Tests;

/// <summary>
/// Covers the behaviour that changes once OIDC is configured: /login stops
/// showing the password form and challenges instead, and the /login/admin
/// break-glass route only exists alongside APP_PASSWORD. The OIDC handshake
/// itself is ASP.NET Core's own well-tested code, so these tests short-circuit
/// discovery with a static Configuration instead of reaching a real Authentik.
/// </summary>
[Collection(AppTextCollection.Name)]
public class OidcAuthorizationTests
{
    private const string Password = "correct-horse-battery-staple";

    private static WebApplicationFactory<Program> NewFactory(bool appPassword = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            if (appPassword) builder.UseSetting("APP_PASSWORD", Password);
            builder.UseSetting("BOODSCHAPPEN_OIDC_AUTHORITY", "https://authentik.test/application/o/boodschappen/");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_ID", "boodschappen");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_SECRET", "secret");
            builder.UseSetting("DATA_DIR",
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

            // Bypass real metadata discovery: a fake Authority is unreachable from
            // a test run, and the handler consults Options.Configuration first.
            builder.ConfigureServices(services =>
                services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                    o.Configuration = new OpenIdConnectConfiguration
                    {
                        AuthorizationEndpoint = "https://authentik.test/application/o/authorize/",
                        TokenEndpoint = "https://authentik.test/application/o/token/",
                        Issuer = "https://authentik.test/application/o/boodschappen/",
                    }));
        });

    private static HttpClient NewClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    [Fact]
    public async Task Login_shows_a_sign_in_button_instead_of_the_password_form()
    {
        // Not an auto-challenge: Authentik's own SSO session usually
        // re-authenticates silently, so signing out of the app must not bounce
        // straight back in with no visible transition -- there has to be a
        // deliberate click first.
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync("/login");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Wachtwoord", body);
        Assert.Contains("/login/oidc", body);
    }

    [Fact]
    public async Task Sign_in_button_is_a_link_not_a_form_submit()
    {
        // The CSP's form-action 'self' (Program.cs) applies, in Chrome, to a
        // form's whole resulting redirect chain -- not just the form's own
        // same-origin action -- so a <form method=get action=/login/oidc> here
        // got silently blocked from ever reaching the OIDC authority's
        // cross-origin authorize page. form-action does not govern ordinary
        // link navigation, so the button must stay a plain <a href>.
        using var factory = NewFactory();
        var body = await (await NewClient(factory).GetAsync("/login")).Content.ReadAsStringAsync();

        Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/login/oidc?ReturnUrl=", body);
    }

    [Fact]
    public async Task Login_oidc_challenges_authentik()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).GetAsync("/login/oidc");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(
            "https://authentik.test/application/o/authorize/",
            response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Legacy_login_post_is_gone_once_oidc_is_enabled()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).PostAsync("/login",
            new FormUrlEncodedContent([new("password", Password)]));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Panic_route_is_reachable_when_app_password_is_set()
    {
        using var factory = NewFactory(appPassword: true);
        var response = await NewClient(factory).GetAsync("/login/admin");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Wachtwoord", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Panic_route_is_absent_when_app_password_is_not_set()
    {
        // Not mapped at all, so an anonymous GET falls under the same global
        // fallback-auth policy as any other unmapped path -- a redirect to the
        // OIDC challenge, never the anonymous password form a mapped route
        // would serve. That is what actually proves it does not exist.
        using var factory = NewFactory(appPassword: false);
        var response = await NewClient(factory).GetAsync("/login/admin");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task Correct_panic_password_sets_the_same_session_cookie()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).PostAsync("/login/admin",
            new FormUrlEncodedContent([new("password", Password)]));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("mealiepicnic="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrong_panic_password_sets_no_cookie()
    {
        using var factory = NewFactory();
        var response = await NewClient(factory).PostAsync("/login/admin",
            new FormUrlEncodedContent([new("password", "wrong")]));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Contains("Onjuist wachtwoord", await response.Content.ReadAsStringAsync());
    }
}
