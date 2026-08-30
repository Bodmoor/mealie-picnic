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

/// <summary>The app shell itself, and the signed-in account label it fetches.</summary>
internal static class ShellEndpoints
{
    internal static void MapShellEndpoints(this IEndpointRouteBuilder app)
    {
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

    }
}
