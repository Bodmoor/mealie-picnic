using System.Security.Claims;
using MealiePicnic.Presentation;
using MealiePicnic.Storage;

namespace MealiePicnic.Endpoints;

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
            // "owner" account, not a personal one, so there is nothing worth
            // labelling. Shared with the request log now (issue #72), which was
            // resolving the name its own, wrong way and calling every OIDC caller
            // anonymous.
            return Results.Ok(new { label = Identity.LabelOf(user) });
        });
    }
}
