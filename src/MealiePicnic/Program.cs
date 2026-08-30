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

var builder = WebApplication.CreateBuilder(args);

var options = AppOptions.FromConfiguration(builder.Configuration);

// One language for the whole process, fixed before anything renders (issue #13).
// Set here rather than injected: LANGUAGE is a property of the deployment, so
// there is nothing per-request to vary on, and the Razor slices take no services.
AppText.Current = AppText.For(options.Language);

builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<HouseholdLinkStore>();
builder.Services.AddSingleton<AllergenVerdictStore>();
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
        // Enforced by default (requires TLS end-to-end, or a proxy with
        // TRUST_PROXY=true); set COOKIE_SECURE=false explicitly for plain-HTTP
        // local dev.
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

        oidc.Events = new OpenIdConnectEvents
        {
            // Resolve the caller's Mealie household once, here at sign-in, and
            // stash it on the cookie -- not on every request, which would mean
            // an admin-API round trip on every shopping-list load (issue #17).
            // A lookup failure (email not linked to any Mealie household, or
            // Mealie briefly unreachable) must not block sign-in: the claim is
            // simply left absent, and the mapping endpoints surface that
            // explicitly (see HouseholdContext) instead of guessing a household.
            OnTicketReceived = async ctx =>
            {
                var email = ctx.Principal?.FindFirstValue("email")
                    ?? ctx.Principal?.FindFirstValue(ClaimTypes.Email);
                if (string.IsNullOrWhiteSpace(email))
                    return;

                var mealie = ctx.HttpContext.RequestServices.GetRequiredService<MealieClient>();
                var log = ctx.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                try
                {
                    var householdId = await mealie.FindHouseholdIdByEmailAsync(email, ctx.HttpContext.RequestAborted);
                    if (householdId is { } id)
                    {
                        ((ClaimsIdentity)ctx.Principal!.Identity!)
                            .AddClaim(new Claim(HouseholdContext.ClaimType, HouseholdKey.From(id)));
                    }
                    else
                    {
                        log.LogWarning("No Mealie household found for {Email}", email);
                    }
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Household lookup failed for {Email}", email);
                }
            },
        };
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

// Minimal security headers.
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    // No 'unsafe-inline', no 'unsafe-eval': htmx needs neither, and Alpine is the
    // @alpinejs/csp build, which restricts directive expressions to a safe
    // subset instead of `new Function()`-evaluating arbitrary JS. Every script
    // (htmx, Alpine, app.js) is a same-origin static file (see Vendor.cs) --
    // nothing is inlined and nothing is loaded from a CDN, so 'self' covers it.
    ctx.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
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

// htmx, Alpine (CSP build) and this app's own client JS -- embedded and
// self-served rather than CDN-loaded (see Vendor.cs). Only ever referenced from
// the (authenticated) app shell, so no AllowAnonymous here, unlike the assets
// above.
app.MapGet("/assets/{name}", (string name, HttpContext ctx) =>
{
    var content = name switch
    {
        "htmx.min.js" => Vendor.HtmxJs,
        "alpine-csp.min.js" => Vendor.AlpineCspJs,
        "app.js" => Vendor.AppJs,
        _ => null,
    };
    if (content is null) return Results.NotFound();

    // Html.AppPage links each of these with a ?v={content hash} (Vendor.cs), so
    // this exact URL's content genuinely never changes -- a deploy that edits
    // app.js changes the URL instead. "immutable" is then correct, not just
    // long-lived: browsers skip the revalidation request entirely, even on a
    // manual reload (issue #33 -- the previous fixed, un-versioned URL was still
    // serving a week-old app.js from cache well after the server-side bug was
    // already fixed).
    ctx.Response.Headers.CacheControl = week + ", immutable";
    return Results.Content(content, "text/javascript");
});

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
// With OIDC configured this shows a "sign in" button rather than auto-challenging.
// Authentik typically has its own active SSO session and re-authenticates
// silently once challenged, so an immediate auto-challenge would make signing
// out of the app instantly bounce back in with no visible transition -- this
// page is the deliberate click that stands between the two. The cookie
// handler's own redirect-to-/login (with ?ReturnUrl=...) is what lands here for
// any unauthenticated request, so the original destination is preserved through
// the round trip to Authentik and back.
app.MapGet("/login", async (HttpContext ctx, AppOptions opt) =>
{
    if (!opt.OidcEnabled)
        return Results.Content(await LoginPage.Create(false).RenderAsync(), "text/html");

    var returnUrl = ctx.Request.Query["ReturnUrl"].ToString();
    var page = await OidcLoginPage.Create(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl).RenderAsync();
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
    app.MapGet("/login/admin", async () => Results.Content(await LoginPage.Create(false).RenderAsync(), "text/html"))
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

// Issue #22: the "afmelden bij deze app" button says only that, with no way to
// tell which account is signed in -- easy to lose track of in a shared
// household browser. Html.AppPage is a compile-time constant (served identically
// to everyone), so this rides along as a fetch from app.js instead of being
// rendered server-side, the same as Picnic's own auth status.
app.MapGet("/api/me", (ClaimsPrincipal user) =>
{
    // Only an OIDC identity has an email claim at all -- the local/password
    // fallback identity (see HandlePasswordLoginAsync) is a single shared
    // "owner" account, not a personal one, so there is nothing worth labelling.
    var email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);
    var label = email is null ? null : (user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name) ?? email);
    return Results.Ok(new { label });
});

// The signed-in identity resolved to no Mealie household at login (see the
// OIDC OnTicketReceived handler above) -- e.g. the email is not linked to a
// household in Mealie yet. Not a permission problem (403), a data/config one:
// naming the email lets whoever sees it know exactly what to fix (issue #17).
static IResult NoHousehold(ClaimsPrincipal user)
{
    var email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email);
    return Results.Json(new
    {
        error = "no_household",
        message = string.IsNullOrWhiteSpace(email)
            ? AppText.Current.NoHousehold
            : AppText.Current.NoHouseholdForEmail(email),
    }, statusCode: StatusCodes.Status422UnprocessableEntity);
}

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
//
// Below here, every response is an HTML fragment for htmx to swap in, not JSON --
// the server renders (RazorSlices), the client just swaps. The one exception is
// GET /lists, still JSON: nothing here consumes it (the picker is now server-
// rendered by ListView itself), but nothing else needs it removed either.

// Resolves which Mealie list to show when the caller did not name one: the
// household's remembered choice if it still exists, else the configured default
// by name, else whichever list is first. The old client-side loadLists() used to
// do this from a cached list array; the server does it now that there is none.
async Task<string> ResolveListIdAsync(
    string householdKey, List<ShoppingListSummary> lists, HouseholdLinkStore links, AppOptions opt)
{
    var remembered = links.SelectedListId(householdKey);
    if (remembered is not null && lists.Any(l => l.Id == remembered))
        return remembered;

    var fallback = lists.FirstOrDefault(l =>
        l.Name.Trim().Equals(opt.MealieList.Trim(), StringComparison.OrdinalIgnoreCase));
    return (fallback ?? lists.FirstOrDefault())?.Id ?? "";
}

async Task<(List<ShoppingListSummary> Lists, string ListId, List<ShoppingItem> Items)> LoadListAsync(
    string householdKey, string? listId, MealieClient mealie, HouseholdLinkStore links, AppOptions opt,
    CancellationToken ct)
{
    var lists = await mealie.GetListsAsync(ct);
    var resolvedListId = string.IsNullOrEmpty(listId)
        ? await ResolveListIdAsync(householdKey, lists, links, opt)
        : listId;
    var items = links.Merge(householdKey, await mealie.GetItemsAsync(
        string.IsNullOrEmpty(resolvedListId) ? null : resolvedListId, ct));
    return (lists, resolvedListId, items);
}

async Task<IResult> RenderListAsync(
    string householdKey, string? listId, MealieClient mealie, HouseholdLinkStore links, AppOptions opt,
    CancellationToken ct)
{
    var (lists, resolvedListId, items) = await LoadListAsync(householdKey, listId, mealie, links, opt, ct);
    var html = await ListView.Create(new ListViewModel(lists, resolvedListId, items)).RenderAsync();
    return Results.Content(html, "text/html");
}

// Still JSON: kept for anyone hitting the API directly, but nothing in the UI
// calls it any more -- the list picker is server-rendered by ListView.
api.MapGet("/lists", async (MealieClient mealie, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
{
    var householdKey = HouseholdContext.KeyOf(ctx.User);
    return Results.Ok(new
    {
        defaultName = opt.MealieList,
        lists = await mealie.GetListsAsync(ct),
        selectedListId = householdKey is null ? null : links.SelectedListId(householdKey),
    });
});

// Triggered by the list <select>'s own hx-post (a plain element with
// name="listId", not a form -- htmx still sends it as ordinary form-urlencoded).
api.MapPost("/lists/select", async (HttpContext ctx, MealieClient mealie, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var listId = form["listId"].ToString();
    if (!Guid.TryParse(listId, out _))
        return Results.BadRequest(new { error = "invalid_list_id" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    links.SelectList(householdKey, listId);
    return await RenderListAsync(householdKey, listId, mealie, links, opt, ct);
});

// listId omitted falls back to ResolveListIdAsync (remembered choice, or the
// MEALIE_LIST default, or whatever list is first).
api.MapGet("/list", async (string? listId, MealieClient mealie, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
{
    if (listId is not null && !Guid.TryParse(listId, out _))
        return Results.BadRequest(new { error = "invalid_list_id" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    return await RenderListAsync(householdKey, listId, mealie, links, opt, ct);
});

// The detail/search view for one food. listId rides along as a query param
// (baked into every link that points here) purely so the eventual "back to the
// list" fragment knows which list to re-render.
api.MapGet("/items/{foodId}", async (string foodId, string? listId, MealieClient mealie, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
{
    if (!Guid.TryParse(foodId, out _) || (listId is not null && !Guid.TryParse(listId, out _)))
        return Results.BadRequest(new { error = "invalid_request" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    var (_, resolvedListId, items) = await LoadListAsync(householdKey, listId, mealie, links, opt, ct);
    var item = items.FirstOrDefault(i => i.FoodId == foodId);
    if (item is null) return Results.NotFound();

    var html = await SearchView.Create(new SearchViewModel(item, resolvedListId)).RenderAsync();
    return Results.Content(html, "text/html");
});

// The Picnic search grid for one food, re-fetched on every keystroke (debounced
// client-side) and once on the search view's initial load.
api.MapGet("/items/{foodId}/search", async (string foodId, string? listId, string? term, MealieClient mealie, PicnicClient picnic, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
{
    if (!Guid.TryParse(foodId, out _) || (listId is not null && !Guid.TryParse(listId, out _)))
        return Results.BadRequest(new { error = "invalid_request" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    var (_, resolvedListId, items) = await LoadListAsync(householdKey, listId, mealie, links, opt, ct);
    var item = items.FirstOrDefault(i => i.FoodId == foodId);
    if (item is null) return Results.NotFound();

    var products = await picnic.SearchAsync(string.IsNullOrWhiteSpace(term) ? item.FoodName : term, ct);
    var html = await HitsGrid.Create(new HitsModel(item, products, resolvedListId)).RenderAsync();
    return Results.Content(html, "text/html");
});

// Organic claim and salt per 100 g, read from the product page (issue #6). Its own
// endpoint because the search response cannot carry it: one page fetch per product
// would turn a search into ninety upstream calls, so the UI asks per card, only
// for cards that actually come into view, and both sides cache. Still JSON: it's
// fetched by app.js's factsLoader, not swapped in as HTML by htmx.
api.MapGet("/details/{productId}", async (string productId, PicnicClient picnic, CancellationToken ct) =>
{
    if (!Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
        return Results.BadRequest(new { error = "invalid_product_id" });

    return Results.Ok(await picnic.GetDetailsAsync(productId, ct));
});

// ------------------------------------------------------------ product detail
// Issue #48. The card's small corner button opens this; the rest of the card
// still links the product. Name, pack and image id ride along from the card
// (hx-vals) rather than being looked up again -- the card already has them, and
// the product page fetch below is the only upstream call this view makes.

api.MapGet("/products/{productId}", async (
    string productId, string? listId, string? foodId, string? name, string? pack, string? imageId,
    PicnicClient picnic, MealieClient mealie, HouseholdLinkStore links,
    AllergenVerdictStore verdicts, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
{
    if (!Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
        return Results.BadRequest(new { error = "invalid_product_id" });

    return await RenderProductAsync(productId, listId, foodId, name, pack, imageId,
        picnic, mealie, links, verdicts, opt, ctx, ct);
});

// Confirm or deny a suspected allergen, globally (issue #48). Re-renders the same
// panel rather than returning a fragment, so the verdict and the buttons around it
// update together and the view cannot drift out of step with the store.
api.MapPost("/products/{productId}/allergens/{group}", async (
    string productId, string group, string? verdict, string? listId,
    PicnicClient picnic, MealieClient mealie, HouseholdLinkStore links,
    AllergenVerdictStore verdicts, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
{
    if (!Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
        return Results.BadRequest(new { error = "invalid_product_id" });

    var form = await ctx.Request.ReadFormAsync(ct);
    var details = await picnic.GetDetailsAsync(productId, ct);
    var mark = details.Allergens.FirstOrDefault(a => a.Group == group);

    // Only groups this product actually carries, and only suspected ones. A
    // declared allergen is Picnic's own emphasis in the ingredient list, which EU
    // labelling requires of a real one -- letting a click switch that off would
    // turn a safety label into a preference. The UI does not offer it; this is
    // what stops a hand-made request doing it anyway.
    if (mark is null) return Results.NotFound();
    if (mark.Declared) return Results.BadRequest(new { error = "declared_allergen" });

    var by = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.User.FindFirstValue("email");
    switch (verdict)
    {
        case "confirmed": verdicts.Set(productId, group, VerdictState.Confirmed, by, mark.Source); break;
        case "denied": verdicts.Set(productId, group, VerdictState.Denied, by, mark.Source); break;
        case "clear": verdicts.Clear(productId, group); break;
        default: return Results.BadRequest(new { error = "invalid_verdict" });
    }

    return await RenderProductAsync(productId, listId,
        form["foodId"].ToString(), form["name"].ToString(), form["pack"].ToString(), form["imageId"].ToString(),
        picnic, mealie, links, verdicts, opt, ctx, ct);
});

async Task<IResult> RenderProductAsync(
    string productId, string? listId, string? foodId, string? name, string? pack, string? imageId,
    PicnicClient picnic, MealieClient mealie, HouseholdLinkStore links,
    AllergenVerdictStore verdicts, AppOptions opt, HttpContext ctx, CancellationToken ct)
{
    if (string.IsNullOrEmpty(foodId) || !Guid.TryParse(foodId, out _) ||
        (!string.IsNullOrEmpty(listId) && !Guid.TryParse(listId, out _)))
        return Results.BadRequest(new { error = "invalid_request" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    // The item is what the back button and the heading need; the detail view is
    // still reached from one food's search results, not from nowhere.
    var (_, resolvedListId, items) = await LoadListAsync(householdKey, listId, mealie, links, opt, ct);
    var item = items.FirstOrDefault(i => i.FoodId == foodId);
    if (item is null) return Results.NotFound();

    // Same rule as every other id that reaches a URL: never pass through an
    // unvalidated one. A bad image id simply drops the picture rather than
    // failing the whole view.
    var safeImage = !string.IsNullOrEmpty(imageId) && Regex.IsMatch(imageId, "^[A-Za-z0-9_-]{1,128}$")
        ? imageId
        : null;

    var details = await picnic.GetDetailsAsync(productId, ct);
    var model = new ProductDetailModel(
        item, resolvedListId, productId,
        string.IsNullOrWhiteSpace(name) ? null : name,
        string.IsNullOrWhiteSpace(pack) ? null : pack,
        safeImage,
        details,
        verdicts.ForProduct(productId));

    return Results.Content(await ProductDetail.Create(model).RenderAsync(), "text/html");
}

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

// Triggered by a product card's hx-post; foodId/picnicUid/label/pack arrive as
// ordinary form fields (see HitsGrid.cshtml -- label/pack are free-text product
// names, read via data-* attributes and htmx's js: hx-vals rather than
// interpolated into a hand-built JSON string, which a quote in a product name
// would break).
api.MapPost("/link", async (HttpContext ctx, MealieClient mealie, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
{
    var listId = ctx.Request.Query["listId"].ToString();
    var form = await ctx.Request.ReadFormAsync(ct);
    var foodId = form["foodId"].ToString();
    var picnicUid = form["picnicUid"].ToString();

    if (!Guid.TryParse(foodId, out _) || string.IsNullOrWhiteSpace(picnicUid)
        || !Regex.IsMatch(picnicUid, "^[A-Za-z0-9_-]{1,32}$"))
        return Results.BadRequest(new { error = "invalid_request" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    // label/pack stored so the basket can work out how many packs a weight needs.
    links.Link(householdKey, foodId, picnicUid, form["label"].ToString(), form["pack"].ToString());
    return await RenderListAsync(householdKey, listId, mealie, links, opt, ct);
});

api.MapPost("/exclude", async (HttpContext ctx, MealieClient mealie, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
{
    var listId = ctx.Request.Query["listId"].ToString();
    var form = await ctx.Request.ReadFormAsync(ct);
    var foodId = form["foodId"].ToString();
    if (!Guid.TryParse(foodId, out _))
        return Results.BadRequest(new { error = "invalid_request" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    links.Exclude(householdKey, foodId);
    return await RenderListAsync(householdKey, listId, mealie, links, opt, ct);
});

// Revert an exclusion: clear the flag so the item shows up as 'new' again.
api.MapPost("/include", async (HttpContext ctx, MealieClient mealie, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
{
    var listId = ctx.Request.Query["listId"].ToString();
    var form = await ctx.Request.ReadFormAsync(ct);
    var foodId = form["foodId"].ToString();
    if (!Guid.TryParse(foodId, out _))
        return Results.BadRequest(new { error = "invalid_request" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    links.Clear(householdKey, foodId);
    return await RenderListAsync(householdKey, listId, mealie, links, opt, ct);
});

// ----------------------------------------------------------------- basket

api.MapPost("/basket", async (HttpContext ctx, MealieClient mealie, PicnicClient picnic,
                              HouseholdLinkStore links, AppOptions opt,
                              ILogger<Program> log, CancellationToken ct) =>
{
    var form = await ctx.Request.ReadFormAsync(ct);
    var listId = form["listId"].ToString();
    var checkOff = form["checkOff"].ToString() == "true";
    if (!string.IsNullOrEmpty(listId) && !Guid.TryParse(listId, out _))
        return Results.BadRequest(new { error = "invalid_list_id" });

    var householdKey = HouseholdContext.KeyOf(ctx.User);
    if (householdKey is null) return NoHousehold(ctx.User);

    var (_, resolvedListId, items) = await LoadListAsync(householdKey, listId, mealie, links, opt, ct);
    var toAdd = items.Where(i => i.State == LinkState.Linked && !i.Checked).ToList();

    // One request for the whole basket (issue #27), keyed by Picnic product id:
    // two Mealie items can link to the same product, so their amounts are summed
    // rather than sent as separate lines.
    var quantities = new Dictionary<string, int>();
    foreach (var item in toAdd)
        quantities[item.PicnicUid!] = quantities.GetValueOrDefault(item.PicnicUid!) + item.Amount;

    var results = new List<CartResult>();
    var aborted = false;
    var abortReason = "";

    if (quantities.Count > 0)
    {
        JsonNode? cart = null;
        try
        {
            cart = await picnic.AddProductsToCartAsync(quantities, ct);
        }
        // These must not be swallowed: the 401 middleware turns the auth failure
        // into the login dialog, and cancellation must stop here.
        catch (PicnicAuthException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Full detail to the log; only a short, stable message to the client
            // (upstream error bodies can contain internal paths and payloads).
            log.LogWarning(ex, "Batch basket add failed for {Count} products", quantities.Count);
            aborted = true;
            abortReason = ex is HttpRequestException
                ? AppText.Current.BasketUpstreamFailed
                : AppText.Current.BasketInternalError;
        }

        foreach (var item in toAdd)
        {
            ct.ThrowIfCancellationRequested();

            // Verifiably in the cart, not just a cheerful status code (issue #7):
            // still true for the batch call, which answers with the updated cart.
            if (aborted || cart is null || !PicnicClient.CartHasProduct(cart, item.PicnicUid!))
            {
                results.Add(new CartResult(item.FoodName, item.PicnicUid!, item.Amount, false,
                    aborted ? abortReason : AppText.Current.BasketPicnicRefused));
                continue;
            }

            var checkedOff = false;
            if (checkOff)
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
    }

    var skipped = items.Count(i => i.State == LinkState.New);
    var logHtml = await BasketLog.Create(new BasketLogModel(results, skipped, aborted, checkOff)).RenderAsync();
    if (!checkOff)
        return Results.Content(logHtml, "text/html");

    // checkOff can change item.Checked server-side (Mealie), so the list itself
    // needs to move too -- an out-of-band swap updates #view in the same
    // response the basket log arrives in, instead of a second round trip.
    var (freshLists, _, freshItems) = await LoadListAsync(householdKey, resolvedListId, mealie, links, opt, ct);
    var listHtml = await ListView.Create(new ListViewModel(freshLists, resolvedListId, freshItems)).RenderAsync();
    var oob = listHtml.Replace("<div id=\"view\"", "<div id=\"view\" hx-swap-oob=\"true\"");
    return Results.Content(logHtml + oob, "text/html");
});

api.MapGet("/cart", async (PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(await picnic.GetCartAsync(ct)));

app.Run();

// ----------------------------------------------------------------- dto records

// NB: a record must not have a member with the same name as the type,
// so this is TwoFactorChannel rather than Channel.
internal record TwoFactorChannel(string? Channel);
internal record Otp(string? Code);
internal record LoginRequest(string? User, string? Password);

/// <summary>Marker so WebApplicationFactory-based integration tests can host the app.</summary>
public partial class Program;
