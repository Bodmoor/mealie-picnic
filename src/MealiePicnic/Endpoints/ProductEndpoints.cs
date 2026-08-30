using System.Security.Claims;
using System.Text.RegularExpressions;
using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using MealiePicnic.Storage;
using RazorSlices;

namespace MealiePicnic.Endpoints;

/// <summary>The product detail view, the allergen verdicts recorded from it, and product images.</summary>
internal static class ProductEndpoints
{
    internal static void MapProductEndpoints(this IEndpointRouteBuilder api)
    {
        // ------------------------------------------------------------ product detail
        // Issue #48. The card's small corner button opens this; the rest of the card
        // still links the product. Name, pack and image id ride along from the card
        // (hx-vals) rather than being looked up again -- the card already has them, and
        // the product page fetch below is the only upstream call this view makes.

        api.MapGet("/products/{productId}", async (
            string productId, string? listId, string? foodId, string? name, string? pack, string? imageId,
            PicnicClient picnic, MealieReads reads, HouseholdLinkStore links,
            AllergenVerdictStore verdicts, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
        {
            if (!Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
                return Results.BadRequest(new { error = "invalid_product_id" });

            if (EndpointHelpers.ShellFor(ctx) is { } shell) return shell;

            return await RenderProductAsync(productId, listId, foodId, name, pack, imageId,
                picnic, reads, links, verdicts, opt, ctx, ct);
        });

        // Confirm or deny a suspected allergen, globally (issue #48). Re-renders the same
        // panel rather than returning a fragment, so the verdict and the buttons around it
        // update together and the view cannot drift out of step with the store.
        api.MapPost("/products/{productId}/allergens/{group}", async (
            string productId, string group, string? verdict, string? listId,
            PicnicClient picnic, MealieReads reads, HouseholdLinkStore links,
            AllergenVerdictStore verdicts, AppOptions opt, HttpContext ctx, CancellationToken ct) =>
        {
            if (!Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
                return Results.BadRequest(new { error = "invalid_product_id" });

            var form = await ctx.Request.ReadFormAsync(ct);
            // Full: the verdict is recorded against the mark's current source text, and
            // that has to be the text the detail view is showing.
            var details = await picnic.GetDetailsAsync(productId, ct, full: true);
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
                picnic, reads, links, verdicts, opt, ctx, ct);
        });

        // Static: it captures nothing, and saying so keeps it honest -- everything it
        // needs arrives as an argument, the same rule that let the other helpers move
        // to EndpointHelpers.
        static async Task<IResult> RenderProductAsync(
            string productId, string? listId, string? foodId, string? name, string? pack, string? imageId,
            PicnicClient picnic, MealieReads reads, HouseholdLinkStore links,
            AllergenVerdictStore verdicts, AppOptions opt, HttpContext ctx, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(foodId) || !Guid.TryParse(foodId, out _) ||
                (!string.IsNullOrEmpty(listId) && !Guid.TryParse(listId, out _)))
                return Results.BadRequest(new { error = "invalid_request" });

            var householdKey = HouseholdContext.KeyOf(ctx.User);
            if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

            // The item is what the back button and the heading need; the detail view is
            // still reached from one food's search results, not from nowhere.
            var (_, resolvedListId, items) = await EndpointHelpers.LoadListAsync(householdKey, listId, reads, links, opt, ct);
            var item = items.FirstOrDefault(i => i.FoodId == foodId);
            if (item is null) return Results.NotFound();

            // Same rule as every other id that reaches a URL: never pass through an
            // unvalidated one. A bad image id simply drops the picture rather than
            // failing the whole view.
            var safeImage = !string.IsNullOrEmpty(imageId) && Regex.IsMatch(imageId, "^[A-Za-z0-9_-]{1,128}$")
                ? imageId
                : null;

            // Full: the page text is what this view exists to show, and the persistent
            // facts store deliberately does not keep it.
            var details = await picnic.GetDetailsAsync(productId, ct, full: true);
            var model = new ProductDetailModel(
                item, resolvedListId, productId,
                string.IsNullOrWhiteSpace(name) ? null : name,
                string.IsNullOrWhiteSpace(pack) ? null : pack,
                safeImage,
                details,
                verdicts.ForProduct(productId));

            return Results.Content(await ProductDetail.Create(model).RenderAsync(), "text/html");
    }

    api.MapGet("/image/{imageId}", async (string imageId, HttpContext ctx, PicnicClient picnic, CancellationToken ct) =>
    {
        // Route values arrive URL-decoded, so ..%2F.. would otherwise walk up the
        // Picnic storefront path (a limited SSRF). Image ids are long hex strings.
        if (!Regex.IsMatch(imageId, "^[A-Za-z0-9_-]{1,128}$"))
            return Results.BadRequest(new { error = "invalid_image_id" });

        // The server cached these all along; the browser did not, because nothing
        // set a header. Every htmx swap of the results grid therefore re-requested
        // each visible image. The id is Picnic's own content id, so the bytes at
        // this URL genuinely never change -- "immutable" here is the same claim
        // /assets makes with its content hash, and lets a repeat view of the same
        // grid skip the request entirely rather than merely revalidate it.
        //
        // Private, not public: these travel through an authenticated endpoint, and
        // a shared proxy has no business holding them.
        //
        // Set AFTER the fetch, deliberately. A Picnic auth failure here is turned
        // into a 401 by the middleware in Program.cs, and the response has not started,
        // so a header set beforehand would survive onto that 401 -- telling the
        // browser to cache "not authorised" for a week and leaving the grid
        // permanently broken for that image until site data was cleared.
        var bytes = await picnic.GetImageAsync(imageId, "medium", ct);
        ctx.Response.Headers.CacheControl = "private, max-age=604800, immutable";
        return Results.File(bytes, "image/png");
    });
    }
}
