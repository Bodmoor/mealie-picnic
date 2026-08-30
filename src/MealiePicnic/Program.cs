using System.Security.Claims;
using System.Threading.RateLimiting;
using MealiePicnic;
using MealiePicnic.Clients;
using MealiePicnic.Endpoints;
using MealiePicnic.Storage;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

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
builder.Services.AddSingleton<ProductFactsStore>();
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
// an OOM waiting to happen.
//
// Every entry sets its Size in approximate BYTES, so this limit means what it
// says. It did not before: images counted their real length while a search list
// claimed a flat 64 KB and a details record claimed 1 KB, which made the budget
// a number with no unit and 64 MB a guess about nothing in particular.
builder.Services.AddMemoryCache(cache => cache.SizeLimit = 64 * 1024 * 1024);

builder.Services.AddHttpClient<MealieClient>();
builder.Services.AddHttpClient<PicnicClient>();

// Scoped, not singleton: this memoises Mealie reads for the life of ONE request
// and no longer. The shopping list is shared mutable data, so it gets no TTL --
// what it removes is the same list being fetched two or three times while
// serving a single keystroke. See MealieReads.
builder.Services.AddScoped<MealieReads>();

// Scoped so it composes with MealieReads above; the run itself holds no state.
builder.Services.AddScoped<BasketRun>();

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
            // Silent authentication: GET /login attempts the handshake with
            // prompt=none first, so an existing Authentik session signs the user
            // straight in and no button is shown at all. Authentik must not
            // render any UI during that attempt -- prompt=none is what makes it
            // answer immediately with either a code or an error.
            OnRedirectToIdentityProvider = ctx =>
            {
                if (ctx.Properties.Items.ContainsKey(SsoFlow.SilentItem))
                    ctx.ProtocolMessage.Prompt = "none";
                return Task.CompletedTask;
            },

            // A silent attempt that finds no usable session comes back as
            // `login_required` (or `interaction_required`), which the handler
            // surfaces as a remote failure. That is an expected outcome, not an
            // error: swallow it and fall back to the page with the button, which
            // is exactly the behaviour this app had before silent auth existed.
            // Non-silent failures are left alone so real problems still surface.
            OnRemoteFailure = ctx =>
            {
                if (ctx.Properties?.Items.ContainsKey(SsoFlow.SilentItem) != true)
                    return Task.CompletedTask;

                var target = ctx.Properties.RedirectUri is { Length: > 0 } uri ? uri : "/";
                ctx.HandleResponse();
                ctx.Response.Redirect($"/login?sso=manual&ReturnUrl={Uri.EscapeDataString(target)}");
                return Task.CompletedTask;
            },

            // Resolve the caller's Mealie household once, here at sign-in, and
            // stash it on the cookie -- not on every request, which would mean
            // an admin-API round trip on every shopping-list load (issue #17).
            // A lookup failure (email not linked to any Mealie household, or
            // Mealie briefly unreachable) must not block sign-in: the claim is
            // simply left absent, and the mapping endpoints surface that
            // explicitly (see HouseholdContext) instead of guessing a household.
            OnTicketReceived = async ctx =>
            {
                // Sign-in succeeded, so neither guard rail is needed any more:
                // the "just signed out" suppression has served its purpose, and
                // the one-attempt marker must go or the next visit after this
                // cookie expires would skip the silent attempt for no reason.
                SsoFlow.ClearMarkers(ctx.HttpContext);

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

    // /health is anonymous and does a write-and-delete on DATA_DIR, so an
    // unlimited one is free disk I/O on the volume holding the tokens and links.
    // Generous rather than strict: it exists to stop hammering, not to interfere
    // with polling, and a proxy checking every few seconds must never be the
    // thing that trips it.
    limiter.AddPolicy("health", ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

// Issue #64. The store writes at most every 30 seconds from inside Put, which is
// the right trade for an unclean kill -- but a `docker compose down` is not one.
// The process is being asked to stop and has time to write one small file, and
// the alternative is re-fetching product pages after every deploy, on a host
// where those are the slowest thing this app does. Flush already swallows its
// own write failures, so this cannot turn a shutdown into an exception.
app.Lifetime.ApplicationStopping.Register(app.Services.GetRequiredService<ProductFactsStore>().Flush);

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

// One deliberate line per request (issue #68), replacing the four the framework
// emitted at Information -- which is now off, see appsettings.json.
//
// The path is logged WITHOUT its query string, and that is the point rather than
// tidiness: the OIDC callback carries the authorization code and state as query
// parameters, and the framework's own request logging wrote them to the console
// in full. A logger that never sees a query string cannot leak one.
//
// Static assets and the health endpoint are skipped so polling and cached files
// do not refill the log with lines nobody will read.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path.Value ?? "/";
    if (path.StartsWith("/assets/", StringComparison.Ordinal) || path == "/health")
    {
        await next();
        return;
    }

    var log = ctx.RequestServices.GetRequiredService<ILogger<Program>>();
    var started = System.Diagnostics.Stopwatch.GetTimestamp();

    // Scope, so every warning raised while serving this request says who it was
    // about -- otherwise "Household lookup failed" is a line with no way back to
    // the person who hit it.
    //
    // Resolved when a line is written, not here. This middleware runs before
    // UseAuthentication (deliberately: a request rejected by the rate limiter
    // still deserves a log line), so ctx.User is not populated yet and reading it
    // now recorded every request as anonymous, signed in or not. No request id
    // either: the framework's own scope already carries RequestId and RequestPath.
    using (log.BeginScope(new SignedInScope(ctx)))
    {
        await next();
        log.LogInformation("{Method} {Path} {Status} in {Elapsed:0}ms",
            ctx.Request.Method, LogSafe(path), ctx.Response.StatusCode,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }
});

// Route values arrive percent-DECODED, so a request for "/login%0aFORGED" would
// otherwise write a second, invented line into the log -- and a forged line is
// exactly what misleads whoever is reading `docker logs` while debugging. Also
// capped: a path is caller-chosen and has no length limit worth trusting.
static string LogSafe(string path)
{
    var clean = new string([.. path.Where(c => !char.IsControl(c)).Take(200)]);
    return clean.Length < path.Length ? clean + "…" : clean;
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

// ----------------------------------------------------------------- endpoints
//
// Issue #66. These were a thousand lines of Map calls in this file, which made it
// the file every feature touched and therefore where every merge conflict landed.
// Grouping is by concern, and each group is an extension method over the same
// builder -- nothing about the routes, their order or their behaviour changed in
// the move. Shared helpers live in EndpointHelpers, which is what they had to
// become once they were no longer local functions in this file.

app.MapPwaEndpoints();
app.MapAuthEndpoints(options);
app.MapShellEndpoints();

var api = app.MapGroup("/api");

api.MapPicnicEndpoints();
api.MapListEndpoints();
api.MapProductEndpoints();
api.MapMappingEndpoints();
api.MapBasketEndpoints();

app.Run();
// ----------------------------------------------------------------- dto records

// NB: a record must not have a member with the same name as the type,
// so this is TwoFactorChannel rather than Channel.
internal record TwoFactorChannel(string? Channel);
internal record Otp(string? Code);
internal record LoginRequest(string? User, string? Password);

/// <summary>
/// The signed-in identity, read when a log line is written rather than when the
/// scope opens.
///
/// The request-logging middleware runs before UseAuthentication, so a dictionary
/// built at scope-open time captured the unauthenticated principal and recorded
/// every request as "anonymous" -- including requests from a signed-in session.
/// A scope state that is enumerated by the console formatter at write time sees
/// the principal the pipeline has since established.
/// </summary>
internal sealed class SignedInScope(HttpContext ctx) : IReadOnlyList<KeyValuePair<string, object>>
{
    private KeyValuePair<string, object> User =>
        new("user", ctx.User.Identity?.Name ?? "anonymous");

    public int Count => 1;

    public KeyValuePair<string, object> this[int index] =>
        index == 0 ? User : throw new ArgumentOutOfRangeException(nameof(index));

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
    {
        yield return User;
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    // What the console formatter prints for the scope when it is not enumerating
    // the pairs itself.
    public override string ToString() => $"user:{User.Value}";
}

/// <summary>Marker so WebApplicationFactory-based integration tests can host the app.</summary>
public partial class Program;
