using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using MealiePicnic;
using Microsoft.AspNetCore.Authentication;          // SignInAsync / SignOutAsync
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var options = AppOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddHttpContextAccessor();

// Keep the key ring on the mounted volume. The default location is inside the
// container filesystem, so recreating the container (any image update) would
// generate fresh keys and silently invalidate every session cookie -- everyone
// gets logged out on deploy.
builder.Services
    .AddDataProtection()
    .SetApplicationName("mealie-picnic")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")));

// Bounded: search terms and image ids are caller-chosen, so an unbounded cache is
// an OOM waiting to happen. Entries set their Size (bytes for images, 1 for JSON).
builder.Services.AddMemoryCache(cache => cache.SizeLimit = 64 * 1024 * 1024);

builder.Services.AddHttpClient<MealieClient>();
builder.Services.AddHttpClient<PicnicClient>();

// Single-password cookie auth in front of everything. Simple on purpose: this app
// holds credentials for a supermarket account, so it must not be open.
var authentication = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/login";
        cookie.LogoutPath = "/logout";
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
        cookie.SlidingExpiration = true;
        cookie.Cookie.Name = "mealiepicnic";
        cookie.Cookie.HttpOnly = true;
        // Was Strict before OIDC existed, when the only way in was a same-site
        // password form. An OIDC login is a legitimate cross-site entry point
        // (the browser lands here via a redirect FROM the identity provider),
        // and a Strict cookie set on that response is not sent on the very next
        // navigation -- the sign-in silently doesn't "take" and /login loops
        // forever. Lax still blocks cross-site POST/PUT, which is what matters
        // for CSRF; it only additionally allows top-level GET navigation, which
        // is exactly what a login redirect is.
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        // Always requires TLS end-to-end (or a proxy with TRUST_PROXY=true);
        // plain-HTTP local dev keeps working with COOKIE_SECURE=false (default).
        cookie.Cookie.SecurePolicy = options.CookieSecure
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;
    });

// OIDC (e.g. Authentik) is additive: when configured, it becomes the primary way
// in via a challenge from GET /login, but the cookie above stays the sign-in
// scheme regardless of how the user got there -- OIDC only establishes identity.
// Which groups may reach this application at all is left entirely to Authentik's
// own application/provider policy bindings (enforced during the authorize
// request itself, not just dashboard visibility) -- nothing is re-checked here.
if (options.OidcEnabled)
{
    authentication.AddOpenIdConnect(oidc =>
    {
        oidc.Authority = options.OidcAuthority;
        oidc.ClientId = options.OidcClientId;
        oidc.ClientSecret = options.OidcClientSecret;
        oidc.ResponseType = "code";
        // Authentik's default is a form_post callback -- a cross-site POST back
        // to us, which the correlation cookie's SameSite=Lax can't survive
        // ("Correlation failed."). Forcing query mode makes the callback a
        // plain GET redirect instead: an ordinary top-level navigation the
        // Lax cookie is sent on.
        oidc.ResponseMode = "query";
        // The cookie above is what actually carries the session; OIDC is only
        // consulted at challenge time.
        oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.Scope.Add("email");
        oidc.GetClaimsFromUserInfoEndpoint = true;
        // No upstream tokens are needed once the identity (the `sub` claim) is
        // established, so nothing sensitive from Authentik itself is retained.
        oidc.SaveTokens = false;
    });
}

// Everything requires an authenticated user unless explicitly opted out. A new
// endpoint added tomorrow is protected by default instead of public by default.
builder.Services.AddAuthorization(auth =>
    auth.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build());

// Brute-force guard on everything that checks a password or triggers a 2FA send.
// Keyed on remote IP; with TRUST_PROXY the forwarded address is what counts.
builder.Services.AddRateLimiter(limiter =>
{
    limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limiter.AddPolicy("credentials", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

if (options.TrustProxy)
{
    // Behind a TLS-terminating reverse proxy the app must see the original scheme,
    // or CookieSecurePolicy.Always would refuse to set the cookie. Clearing the
    // known-proxy lists trusts whatever sits in front -- only enable this when a
    // proxy actually does.
    var forwarded = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

app.UseRateLimiter();

// Minimal security headers. No CSP yet: the SPA still uses inline script and
// inline event handlers, so a useful CSP needs that refactor first.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseAuthentication();
app.UseAuthorization();

// Turn a Picnic auth failure into a 401 the UI can react to, instead of a 500.
// The browser then knows to open the login / 2FA dialog rather than showing a stack trace.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (Exception ex) when (ex is PicnicAuthException or PicnicCredentialsException)
    {
        // Once the body started streaming (e.g. Results.File on /api/image) the
        // status line is gone; rewriting it would throw a second, unhandled error.
        if (ctx.Response.HasStarted)
        {
            app.Logger.LogWarning(ex, "Picnic auth failure after response start on {Path}", ctx.Request.Path);
            throw;
        }

        if (ex is PicnicAuthException)
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { error = "picnic_auth", message = ex.Message });
        }
        else
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "picnic_credentials", message = ex.Message });
        }
    }
});

// ----------------------------------------------------------------- pwa assets
// All anonymous: Android fetches the manifest, icons and service worker before
// (and independently of) login, and a 302-to-login makes the app look
// uninstallable. None of this leaks anything -- it is static branding.
var week = "public, max-age=604800";

// Browsers request /favicon.ico unprompted; serving a PNG under that path is
// fine (the Content-Type is what matters, and nosniff keeps it honest).
app.MapGet("/favicon.ico", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = week;
    return Results.File(Icons.Png192, "image/png");
}).AllowAnonymous();

app.MapGet("/icons/{name}", (string name, HttpContext ctx) =>
{
    (byte[]? bytes, string type) = name switch
    {
        "192.png" => (Icons.Png192, "image/png"),
        "512.png" => (Icons.Png512, "image/png"),
        "maskable-512.png" => (Icons.PngMaskable512, "image/png"),
        "eu-organic.svg" => (Icons.EuOrganicSvg, "image/svg+xml"),
        _ => ((byte[]?)null, ""),
    };
    if (bytes is null) return Results.NotFound();

    ctx.Response.Headers.CacheControl = week;
    return Results.File(bytes, type);
}).AllowAnonymous();

app.MapGet("/manifest.webmanifest", () =>
    Results.Content(Icons.Manifest, "application/manifest+json")).AllowAnonymous();

app.MapGet("/sw.js", () =>
    // No caching: a stale service worker is how PWAs get stuck on old code.
    Results.Content(Icons.ServiceWorker, "text/javascript")).AllowAnonymous();

// Password check shared by the plain /login form (no OIDC configured) and the
// /login/admin panic route (OIDC configured, APP_PASSWORD kept as a fallback).
// Either way the resulting session is the same synthetic "local" identity, so
// TokenStore treats it exactly like any other user once OIDC is enabled.
async Task<IResult> HandlePasswordLoginAsync(HttpContext ctx, AppOptions opt, ILogger<Program> log)
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
        return Results.Content(Html.LoginPage.Replace("<!--ERROR-->",
            "<p class=\"err\">Onjuist wachtwoord</p>"), "text/html");
    }

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "owner"), new Claim(ClaimTypes.NameIdentifier, UserKey.LocalSubject)],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Redirect("/");
}

// Static files (the SPA) are behind auth too, hence no UseStaticFiles() before this.
// With OIDC configured this shows a "sign in" button rather than auto-challenging.
// Authentik typically has its own active SSO session and re-authenticates
// silently once challenged, so an immediate auto-challenge would make signing
// out of the app instantly bounce back in with no visible transition -- this
// page is the deliberate click that stands between the two. The cookie
// handler's own redirect-to-/login (with ?ReturnUrl=...) is what lands here for
// any unauthenticated request, so the original destination is preserved through
// the round trip to Authentik and back.
app.MapGet("/login", (HttpContext ctx, AppOptions opt) =>
{
    if (!opt.OidcEnabled)
        return Results.Content(Html.LoginPage, "text/html");

    var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();
    var page = Html.OidcLoginPage.Replace("<!--RETURNURL-->",
        System.Net.WebUtility.HtmlEncode(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl));
    return Results.Content(page, "text/html");
}).AllowAnonymous();

app.MapGet("/login/oidc", (HttpContext ctx) =>
{
    var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();
    return Results.Challenge(
        new AuthenticationProperties { RedirectUri = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl },
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
    app.MapGet("/login/admin", () => Results.Content(Html.LoginPage, "text/html"))
       .AllowAnonymous();

    app.MapPost("/login/admin", (HttpContext ctx, AppOptions opt, ILogger<Program> log) =>
        HandlePasswordLoginAsync(ctx, opt, log)
    ).AllowAnonymous().RequireRateLimiting("credentials");
}

// POST, not GET: a state-changing GET can be triggered cross-site by an <img> tag,
// and SameSite=Lax/Strict does not stop top-level GET navigations.
app.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/", () => Results.Content(Html.AppPage, "text/html"));

var api = app.MapGroup("/api");

// ----------------------------------------------------------------- picnic auth

api.MapGet("/picnic/status", async (PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(new
    {
        authenticated = await picnic.IsUsableAsync(ct),
        // Lets the UI prefill the login dialog and say precisely what is missing.
        // The username is safe to expose; the password never leaves the server.
        hasConfiguredUser = picnic.HasConfiguredUser,
        hasConfiguredPassword = picnic.HasConfiguredPassword,
        configuredUser = picnic.ConfiguredUser,
    }));

api.MapPost("/picnic/logout", async (PicnicClient picnic, CancellationToken ct) =>
{
    await picnic.LogoutAsync(ct);
    return Results.Ok(new { loggedOut = true });
});

api.MapPost("/picnic/login", async (LoginRequest? body, PicnicClient picnic, CancellationToken ct) =>
{
    var needs2fa = await picnic.LoginAsync(body?.User, body?.Password, ct);
    return Results.Ok(new { needs2fa });
}).RequireRateLimiting("credentials");

api.MapPost("/picnic/2fa/generate", async (TwoFactorChannel? body, PicnicClient picnic, CancellationToken ct) =>
{
    var channel = string.IsNullOrWhiteSpace(body?.Channel) ? "EMAIL" : body.Channel.ToUpperInvariant();
    if (channel is not ("SMS" or "EMAIL"))
        return Results.BadRequest(new { error = "invalid_channel" });

    await picnic.Generate2faAsync(channel, ct);
    return Results.Ok(new { sent = true });
}).RequireRateLimiting("credentials");

api.MapPost("/picnic/2fa/verify", async (Otp? body, PicnicClient picnic, CancellationToken ct) =>
{
    var code = body?.Code?.Trim() ?? "";
    if (!Regex.IsMatch(code, "^[0-9]{4,10}$"))
        return Results.BadRequest(new { error = "invalid_code" });

    await picnic.Verify2faAsync(code, ct);
    return Results.Ok(new { verified = true });
}).RequireRateLimiting("credentials");

// ----------------------------------------------------------------- shopping list

// All shopping lists, for the picker in the UI.
api.MapGet("/lists", async (MealieClient mealie, AppOptions opt, CancellationToken ct) =>
    Results.Ok(new { defaultName = opt.MealieList, lists = await mealie.GetListsAsync(ct) }));

// listId is optional: omitted falls back to the MEALIE_LIST default.
api.MapGet("/list", async (string? listId, MealieClient mealie, CancellationToken ct) =>
{
    if (listId is not null && !Guid.TryParse(listId, out _))
        return Results.BadRequest(new { error = "invalid_list_id" });
    return Results.Ok(await mealie.GetItemsAsync(listId, ct));
});

api.MapGet("/search", async (string term, PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(await picnic.SearchAsync(term, ct)));

// Organic claim and salt per 100 g, read from the product page (issue #6). Its own
// endpoint because the search response cannot carry it: one page fetch per product
// would turn a search into ninety upstream calls, so the UI asks per card, only
// for cards that actually come into view, and both sides cache.
api.MapGet("/details/{productId}", async (string productId, PicnicClient picnic, CancellationToken ct) =>
{
    if (!Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
        return Results.BadRequest(new { error = "invalid_product_id" });

    return Results.Ok(await picnic.GetDetailsAsync(productId, ct));
});

api.MapGet("/image/{imageId}", async (string imageId, PicnicClient picnic, CancellationToken ct) =>
{
    // Route values arrive URL-decoded, so ..%2F.. would otherwise walk up the
    // Picnic storefront path (a limited SSRF). Image ids are long hex strings.
    if (!Regex.IsMatch(imageId, "^[A-Za-z0-9_-]{1,128}$"))
        return Results.BadRequest(new { error = "invalid_image_id" });

    var bytes = await picnic.GetImageAsync(imageId, "medium", ct);
    return Results.File(bytes, "image/png");
});

// ----------------------------------------------------------------- mapping

api.MapPost("/link", async (LinkRequest? body, MealieClient mealie, CancellationToken ct) =>
{
    if (body is null || !Guid.TryParse(body.FoodId, out _)
        || string.IsNullOrWhiteSpace(body.PicnicUid)
        || !Regex.IsMatch(body.PicnicUid, "^[A-Za-z0-9_-]{1,32}$"))
        return Results.BadRequest(new { error = "invalid_request" });

    var extras = await mealie.SetFoodExtrasAsync(body.FoodId, new Dictionary<string, string>
    {
        [MealieClient.ExtraUid] = body.PicnicUid,
        [MealieClient.ExtraFlag] = "true",
        [MealieClient.ExtraLabel] = body.Label ?? "",
        // Stored so the basket can work out how many packs a weight needs.
        [MealieClient.ExtraPack] = body.Pack ?? "",
    }, ct);
    return Results.Ok(new { ok = true, extras });
});

api.MapPost("/exclude", async (FoodRef? body, MealieClient mealie, CancellationToken ct) =>
{
    if (body is null || !Guid.TryParse(body.FoodId, out _))
        return Results.BadRequest(new { error = "invalid_request" });

    var extras = await mealie.SetFoodExtrasAsync(body.FoodId, new Dictionary<string, string>
    {
        [MealieClient.ExtraFlag] = "false",
    }, ct);
    return Results.Ok(new { ok = true, extras });
});

// Revert an exclusion: clear the flag so the item shows up as 'new' again.
api.MapPost("/include", async (FoodRef? body, MealieClient mealie, CancellationToken ct) =>
{
    if (body is null || !Guid.TryParse(body.FoodId, out _))
        return Results.BadRequest(new { error = "invalid_request" });

    var extras = await mealie.SetFoodExtrasAsync(body.FoodId, new Dictionary<string, string>
    {
        [MealieClient.ExtraFlag] = "",
    }, ct);
    return Results.Ok(new { ok = true, extras });
});

// ----------------------------------------------------------------- basket

api.MapPost("/basket", async (BasketRequest? body, MealieClient mealie, PicnicClient picnic,
                              ILogger<Program> log, CancellationToken ct) =>
{
    var listId = body?.ListId;
    if (listId is not null && !Guid.TryParse(listId, out _))
        return Results.BadRequest(new { error = "invalid_list_id" });

    var items = await mealie.GetItemsAsync(listId, ct);
    var results = new List<CartResult>();
    var consecutiveFailures = 0;
    var aborted = false;

    foreach (var item in items.Where(i => i.State == LinkState.Linked && !i.Checked))
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            // Throws unless the product is verifiably in the cart, so nothing below
            // runs for a refused add (issue #7).
            await picnic.AddToCartAsync(item.PicnicUid!, item.Amount, ct);
            consecutiveFailures = 0;

            var checkedOff = false;
            if (body?.CheckOff == true)
            {
                try
                {
                    await mealie.CheckItemAsync(item.ItemId, ct);
                    checkedOff = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // The basket add DID succeed. Reporting this as a failed line
                    // would invite a retry and a double order, so it is recorded as
                    // a successful add that is still on the Mealie list.
                    log.LogWarning(ex, "Checked off failed for {Food} ({ItemId})",
                        item.FoodName, item.ItemId);
                }
            }

            results.Add(new CartResult(
                item.FoodName, item.PicnicUid!, item.Amount, true, null, checkedOff));
        }
        // These must not be swallowed per-item: the 401 middleware turns the auth
        // failure into the login dialog, and cancellation must stop the loop.
        catch (PicnicAuthException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Full detail to the log; only a short, stable message to the client
            // (upstream error bodies can contain internal paths and payloads).
            log.LogWarning(ex, "Basket add failed for {Food} ({Uid})", item.FoodName, item.PicnicUid);
            var reason = ex switch
            {
                // Picnic said no (or silently did not add it): worth showing, since
                // the item stays on the Mealie list and can be re-linked.
                PicnicCartException => "Picnic weigerde het product",
                HttpRequestException => "upstream request failed",
                _ => "internal error",
            };
            results.Add(new CartResult(item.FoodName, item.PicnicUid!, item.Amount, false, reason));

            // A burst of failures usually means Picnic is refusing us; hammering an
            // unofficial API from here only invites anti-abuse measures.
            if (++consecutiveFailures >= 3)
            {
                aborted = true;
                break;
            }
        }

        // Gentle pacing against an undocumented API.
        await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
    }

    var skipped = items.Count(i => i.State == LinkState.New);
    return Results.Ok(new { results, unmapped = skipped, aborted });
});

api.MapGet("/cart", async (PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(await picnic.GetCartAsync(ct)));

app.Run();

// ----------------------------------------------------------------- dto records

// NB: a record must not have a member with the same name as the type,
// so this is TwoFactorChannel rather than Channel.
internal record TwoFactorChannel(string? Channel);
internal record Otp(string? Code);
internal record FoodRef(string FoodId);
internal record LinkRequest(string FoodId, string PicnicUid, string? Label, string? Pack);
internal record BasketRequest(bool CheckOff, string? ListId);
internal record LoginRequest(string? User, string? Password);

/// <summary>Marker so WebApplicationFactory-based integration tests can host the app.</summary>
public partial class Program;
