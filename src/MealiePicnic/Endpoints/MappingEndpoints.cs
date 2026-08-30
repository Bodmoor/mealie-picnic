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

/// <summary>Linking a Mealie food to a Picnic product, and excluding one from Picnic entirely.</summary>
internal static class MappingEndpoints
{
    internal static void MapMappingEndpoints(this IEndpointRouteBuilder api)
    {
    // ----------------------------------------------------------------- mapping

    // Triggered by a product card's hx-post; foodId/picnicUid/label/pack arrive as
    // ordinary form fields (see HitsGrid.cshtml -- label/pack are free-text product
    // names, read via data-* attributes and htmx's js: hx-vals rather than
    // interpolated into a hand-built JSON string, which a quote in a product name
    // would break).
    api.MapPost("/link", async (HttpContext ctx, MealieReads reads, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
    {
        var listId = ctx.Request.Query["listId"].ToString();
        var form = await ctx.Request.ReadFormAsync(ct);
        var foodId = form["foodId"].ToString();
        var picnicUid = form["picnicUid"].ToString();

        if (!Guid.TryParse(foodId, out _) || string.IsNullOrWhiteSpace(picnicUid)
            || !Regex.IsMatch(picnicUid, "^[A-Za-z0-9_-]{1,32}$"))
            return Results.BadRequest(new { error = "invalid_request" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        // label/pack stored so the basket can work out how many packs a weight needs.
        links.Link(householdKey, foodId, picnicUid, form["label"].ToString(), form["pack"].ToString());
        return await EndpointHelpers.RenderListAsync(householdKey, listId, reads, links, opt, ct);
    });

    api.MapPost("/exclude", async (HttpContext ctx, MealieReads reads, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
    {
        var listId = ctx.Request.Query["listId"].ToString();
        var form = await ctx.Request.ReadFormAsync(ct);
        var foodId = form["foodId"].ToString();
        if (!Guid.TryParse(foodId, out _))
            return Results.BadRequest(new { error = "invalid_request" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        links.Exclude(householdKey, foodId);
        return await EndpointHelpers.RenderListAsync(householdKey, listId, reads, links, opt, ct);
    });

    // Revert an exclusion: clear the flag so the item shows up as 'new' again.
    api.MapPost("/include", async (HttpContext ctx, MealieReads reads, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
    {
        var listId = ctx.Request.Query["listId"].ToString();
        var form = await ctx.Request.ReadFormAsync(ct);
        var foodId = form["foodId"].ToString();
        if (!Guid.TryParse(foodId, out _))
            return Results.BadRequest(new { error = "invalid_request" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        links.Clear(householdKey, foodId);
        return await EndpointHelpers.RenderListAsync(householdKey, listId, reads, links, opt, ct);
    });

    }
}
