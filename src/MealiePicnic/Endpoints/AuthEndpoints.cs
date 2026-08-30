using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using MealiePicnic;
using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using MealiePicnic.Storage;
using RazorSlices;
using Microsoft.AspNetCore.Authentication;          // SignInAsync / SignOutAsync
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

/// <summary>
/// Signing in and out: the password form (when no OIDC is configured), the OIDC
/// challenge and its silent-authentication attempt, the /login/admin break-glass
/// route, and the app's own sign-out.
/// </summary>
internal static class AuthEndpoints
{
    /// <param name="options">Read at map time, not per request: whether the
    /// break-glass route exists at all is a deployment fact, and a route that is
    /// never mapped is the only kind that cannot be reached.</param>
    internal static void MapAuthEndpoints(this IEndpointRouteBuilder app, AppOptions options)
    {
    // Password check shared by the plain /login form (no OIDC configured) and the
    // /login/admin panic route (OIDC configured, APP_PASSWORD kept as a fallback).
    // Either way the resulting session is the same synthetic "local" identity, so
    // TokenStore treats it exactly like any other user once OIDC is enabled.
        static async Task<IResult> HandlePasswordLoginAsync(HttpContext ctx, AppOptions opt, ILogger<Program> log)
    {
        var form = await ctx.Request.ReadFormAsync();
        var supplied = form["password"].ToString();

        // Hash both sides so the comparison is fixed-length and fixed-time.
        static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var ok = CryptographicOperations.FixedTimeEquals(Hash(supplied), Hash(opt.AppPassword));

        if (!ok)
        {
            // Loggable signal for fail2ban / the reverse proxy.
            log.LogWarning("Failed login attempt from {Ip}", ctx.Connection.RemoteIpAddress);
            return Results.Content(await LoginPage.Create(true).RenderAsync(), "text/html");
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "owner"), new Claim(ClaimTypes.NameIdentifier, UserKey.LocalSubject),
             new Claim(HouseholdContext.ClaimType, HouseholdKey.Local)],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity));

        return Results.Redirect("/");
    }

    // Static files (the SPA) are behind auth too, hence no UseStaticFiles() before this.
    //
    // With OIDC configured, this first tries to sign the caller in *silently*
    // (prompt=none): Authentik usually has its own live session, and making someone
    // click a button to consummate a session they already hold is friction with no
    // decision in it (issue #49). Three things stop that becoming a nuisance:
    //
    //   * an explicit sign-out sets a short-lived marker, so signing out of the app
    //     does not immediately bounce back in through Authentik's still-live session
    //     with no visible transition -- the reason this page existed in the first place
    //   * every attempt sets a one-minute marker, so a silent success whose cookie
    //     fails to stick (misconfigured TLS, say) cannot turn into a redirect loop
    //   * ?sso=manual, which the silent attempt's own failure path redirects to,
    //     always renders the button
    //
    // In all three cases the page below appears, exactly as it did before. The cookie
    // handler's own redirect-to-/login (with ?ReturnUrl=...) is what lands here for any
    // unauthenticated request, so the original destination survives the round trip
    // through Authentik either way.
    app.MapGet("/login", async (HttpContext ctx, AppOptions opt) =>
    {
        if (!opt.OidcEnabled)
            return Results.Content(await LoginPage.Create(false).RenderAsync(), "text/html");

        var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();
        var target = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;

        if (SsoFlow.ShouldTrySilently(ctx))
        {
            SsoFlow.MarkAttempted(ctx, opt);
            var silent = new AuthenticationProperties { RedirectUri = target, IsPersistent = true };
            silent.Items[SsoFlow.SilentItem] = "1";
            return Results.Challenge(silent, [OpenIdConnectDefaults.AuthenticationScheme]);
        }

        var page = await OidcLoginPage.Create(target).RenderAsync();
        return Results.Content(page, "text/html");
    }).AllowAnonymous();

    // IsPersistent is what actually gives the browser a cookie that outlives the
    // browser session. Without it the cookie handler writes no Expires at all, and
    // the 30-day ExpireTimeSpan configured above governs only the ticket inside the
    // cookie -- so closing the browser, or a phone evicting the tab, meant signing in
    // again from scratch (issue #49). Deliberately not applied to the /login/admin
    // break-glass path: a shared operator password should not leave a month-long
    // cookie behind on whatever machine used it.
    app.MapGet("/login/oidc", (HttpContext ctx) =>
    {
        var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();
        return Results.Challenge(
            new AuthenticationProperties
            {
                RedirectUri = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl,
                IsPersistent = true,
            },
            [OpenIdConnectDefaults.AuthenticationScheme]);
    }).AllowAnonymous();

    // Once OIDC is configured, plain /login never shows a password form (see above),
    // so this becomes dead weight rather than a second, undocumented back door.
    app.MapPost("/login", async (HttpContext ctx, AppOptions opt, ILogger<Program> log) =>
        opt.OidcEnabled ? Results.NotFound() : await HandlePasswordLoginAsync(ctx, opt, log)
    ).AllowAnonymous().RequireRateLimiting("credentials");

    // Break-glass password login, only reachable when both OIDC and APP_PASSWORD are
    // configured. Deliberately not linked from anywhere in the UI -- an operator who
    // needs it already knows the URL; nothing here should tempt or confuse an
    // ordinary OIDC user.
    if (options.OidcEnabled && options.AppPassword.Length > 0)
    {
        app.MapGet("/login/admin", async () => Results.Content(await LoginPage.Create(false).RenderAsync(), "text/html"))
           .AllowAnonymous();

        app.MapPost("/login/admin", (HttpContext ctx, AppOptions opt, ILogger<Program> log) =>
            HandlePasswordLoginAsync(ctx, opt, log)
        ).AllowAnonymous().RequireRateLimiting("credentials");
    }

    // POST, not GET: a state-changing GET can be triggered cross-site by an <img> tag,
    // and SameSite=Lax/Strict does not stop top-level GET navigations.
    app.MapPost("/logout", async (HttpContext ctx, AppOptions opt) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        // Authentik's own session outlives this one, so without a marker the silent
        // attempt on the very next /login would sign the user straight back in and
        // the sign-out would look like it did nothing.
        SsoFlow.MarkSignedOut(ctx, opt);
        return Results.Redirect("/login");
    });

    }
}

/// <summary>
/// Cookie bookkeeping for the silent-SSO attempt on GET /login (issue #49).
///
/// Both markers are deliberately cookies rather than session state: they must be
/// readable on a request that has no session yet, which is the entire situation
/// being handled. Neither carries anything sensitive -- their presence alone is
/// the signal -- but they are HttpOnly and SameSite=Lax like the auth cookie, so
/// nothing script-side can clear them to force a silent attempt.
/// </summary>
internal static class SsoFlow
{
    /// <summary>Key on AuthenticationProperties.Items marking an attempt as silent.</summary>
    public const string SilentItem = "mp:silent";

    private const string AttemptedCookie = "mp_sso_tried";
    private const string SignedOutCookie = "mp_signed_out";

    // Long enough to break a redirect loop, short enough that a genuine second
    // visit a few minutes later still gets the silent path.
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);

    // Long enough to see the login page and walk away; after this, returning to
    // the app signs back in silently again, which is the desired default.
    private static readonly TimeSpan SignedOutWindow = TimeSpan.FromMinutes(30);

    public static bool ShouldTrySilently(HttpContext ctx) =>
        !ctx.Request.Query.ContainsKey("sso")
        && !ctx.Request.Cookies.ContainsKey(AttemptedCookie)
        && !ctx.Request.Cookies.ContainsKey(SignedOutCookie);

    public static void MarkAttempted(HttpContext ctx, AppOptions opt) =>
        ctx.Response.Cookies.Append(AttemptedCookie, "1", Options(ctx, opt, AttemptWindow));

    public static void MarkSignedOut(HttpContext ctx, AppOptions opt)
    {
        ctx.Response.Cookies.Delete(AttemptedCookie);
        ctx.Response.Cookies.Append(SignedOutCookie, "1", Options(ctx, opt, SignedOutWindow));
    }

    public static void ClearMarkers(HttpContext ctx)
    {
        ctx.Response.Cookies.Delete(AttemptedCookie);
        ctx.Response.Cookies.Delete(SignedOutCookie);
    }

    private static CookieOptions Options(HttpContext ctx, AppOptions opt, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        // Mirrors the auth cookie's CookieSecurePolicy: Always when COOKIE_SECURE
        // is on, otherwise whatever the current request is, so plain-HTTP local
        // development still receives the cookie.
        Secure = opt.CookieSecure || ctx.Request.IsHttps,
        Path = "/",
        MaxAge = lifetime,
    };
}
