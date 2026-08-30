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

/// <summary>The Mealie shopping list, the per-food search view, and the product facts the cards fetch.</summary>
internal static class ListEndpoints
{
    internal static void MapListEndpoints(this IEndpointRouteBuilder api)
    {
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

    // Still JSON: kept for anyone hitting the API directly, but nothing in the UI
    // calls it any more -- the list picker is server-rendered by ListView.
    api.MapGet("/lists", async (MealieReads reads, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
    {
        var householdKey = HouseholdContext.KeyOf(ctx.User);
        return Results.Ok(new
        {
            defaultName = opt.MealieList,
            lists = await reads.ListsAsync(ct),
            selectedListId = householdKey is null ? null : links.SelectedListId(householdKey),
        });
    });

    // Triggered by the list <select>'s own hx-post (a plain element with
    // name="listId", not a form -- htmx still sends it as ordinary form-urlencoded).
    api.MapPost("/lists/select", async (HttpContext ctx, MealieReads reads, HouseholdLinkStore links, AppOptions opt, CancellationToken ct) =>
    {
        var form = await ctx.Request.ReadFormAsync(ct);
        var listId = form["listId"].ToString();
        if (!Guid.TryParse(listId, out _))
            return Results.BadRequest(new { error = "invalid_list_id" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        links.SelectList(householdKey, listId);
        return await EndpointHelpers.RenderListAsync(householdKey, listId, reads, links, opt, ct);
    });

    // listId omitted falls back to ResolveListIdAsync (remembered choice, or the
    // MEALIE_LIST default, or whatever list is first).
    api.MapGet("/list", async (string? listId, MealieReads reads, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
    {
        if (listId is not null && !Guid.TryParse(listId, out _))
            return Results.BadRequest(new { error = "invalid_list_id" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        return await EndpointHelpers.RenderListAsync(householdKey, listId, reads, links, opt, ct);
    });

    // The detail/search view for one food. listId rides along as a query param
    // (baked into every link that points here) purely so the eventual "back to the
    // list" fragment knows which list to re-render.
    api.MapGet("/items/{foodId}", async (string foodId, string? listId, MealieReads reads, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
    {
        if (!Guid.TryParse(foodId, out _) || (listId is not null && !Guid.TryParse(listId, out _)))
            return Results.BadRequest(new { error = "invalid_request" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        var (_, resolvedListId, items) = await EndpointHelpers.LoadListAsync(householdKey, listId, reads, links, opt, ct);
        var item = items.FirstOrDefault(i => i.FoodId == foodId);
        if (item is null) return Results.NotFound();

        var html = await SearchView.Create(new SearchViewModel(item, resolvedListId)).RenderAsync();
        return Results.Content(html, "text/html");
    });

    // The Picnic search grid for one food, re-fetched on every keystroke (debounced
    // client-side) and once on the search view's initial load.
    api.MapGet("/items/{foodId}/search", async (string foodId, string? listId, string? term, MealieReads reads, PicnicClient picnic, HouseholdLinkStore links, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
    {
        if (!Guid.TryParse(foodId, out _) || (listId is not null && !Guid.TryParse(listId, out _)))
            return Results.BadRequest(new { error = "invalid_request" });

        var householdKey = HouseholdContext.KeyOf(ctx.User);
        if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

        var (_, resolvedListId, items) = await EndpointHelpers.LoadListAsync(householdKey, listId, reads, links, opt, ct);
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

    }
}
