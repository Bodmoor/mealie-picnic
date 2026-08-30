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

/// <summary>Icons, manifest, service worker, the embedded client assets, and the health probe. All anonymous.</summary>
internal static class PwaEndpoints
{
    internal static void MapPwaEndpoints(this IEndpointRouteBuilder app)
    {
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

    // Issue #65. Nothing outside the process could tell "the port is open" from "the
    // app works", so the reverse proxy would happily keep sending traffic to a
    // container whose data directory had gone read-only while every page still
    // rendered.
    //
    // Deliberately narrow. It does not check Picnic: a lapsed token is an ordinary
    // state the UI already handles, and reporting it as unhealthy would have a
    // restart loop fighting a login prompt. It does check that DATA_DIR is writable,
    // because that is the failure that silently breaks token and link storage while
    // the app looks fine -- the one case where "the port answers" actively misleads.
    //
    // It is reachable without authentication, so it says only whether the process is
    // serving. No versions, no configuration, no paths.
    app.MapGet("/health", (AppOptions opt, ILogger<Program> log) =>
    {
        // Unique per probe. A fixed filename races when two things poll at once --
        // a proxy check and an orchestrator check, say -- one deleting the file
        // while the other is still writing it, and the loser answers 503. A false
        // unhealthy driving a restart loop is precisely what this endpoint exists
        // to avoid, so it must not be able to cause one itself.
        var probe = Path.Combine(opt.DataDir, $".health-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probe, "");
        }
        catch (Exception ex)
        {
            // The reason goes to the log; the anonymous response says only that
            // something is wrong.
            log.LogWarning(ex, "Health check failed: {Path} is not writable", opt.DataDir);
            return Results.Json(new { status = "unhealthy" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        finally
        {
            // Best effort: a probe left behind is litter, not a failure, and must
            // never turn a healthy instance unhealthy.
            try { File.Delete(probe); } catch { /* ignored */ }
        }

        return Results.Ok(new { status = "ok" });
    }).AllowAnonymous().RequireRateLimiting("health");

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

    }
}
