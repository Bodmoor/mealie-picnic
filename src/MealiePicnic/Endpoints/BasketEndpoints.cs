using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using MealiePicnic.Storage;
using RazorSlices;

namespace MealiePicnic.Endpoints;

/// <summary>Sending the list to the Picnic basket, and reading the basket back.</summary>
internal static class BasketEndpoints
{
    internal static void MapBasketEndpoints(this IEndpointRouteBuilder api)
    {
        // ----------------------------------------------------------------- basket

        // The run itself lives in BasketRun (issue #62): it is the only path in this app
        // that spends money, and inside an endpoint lambda it needed a
        // WebApplicationFactory and two stubbed upstreams to reach, so none of it was
        // tested. This handler now does what a handler should -- read the form, load the
        // list, hand off, render.
        api.MapPost("/basket", async (HttpContext ctx, MealieReads reads, BasketRun run,
                                      HouseholdLinkStore links, AppOptions opt,
                                      CancellationToken ct) =>
        {
            var form = await ctx.Request.ReadFormAsync(ct);
            var listId = form["listId"].ToString();
            var checkOff = form["checkOff"].ToString() == "true";
            if (!string.IsNullOrEmpty(listId) && !Guid.TryParse(listId, out _))
                return Results.BadRequest(new { error = "invalid_list_id" });

            var householdKey = HouseholdContext.KeyOf(ctx.User);
            if (householdKey is null) return EndpointHelpers.NoHousehold(ctx.User);

            var (_, resolvedListId, items) = await EndpointHelpers.LoadListAsync(householdKey, listId, reads, links, opt, ct);
            var selection = BasketSelection.From(items);

            var outcome = await run.ExecuteAsync(selection.ToAdd, checkOff, ct);
            var results = outcome.Results;
            var aborted = outcome.Aborted;

            var logHtml = await BasketLog.Create(
                new BasketLogModel(results, selection.Skipped, aborted, checkOff)).RenderAsync();
            if (!checkOff)
                return Results.Content(logHtml, "text/html");

            // checkOff can change item.Checked server-side (Mealie), so the list itself
            // needs to move too -- an out-of-band swap updates #view in the same
            // response the basket log arrives in, instead of a second round trip.
            //
            // The reload has to be a real one. MealieReads memoises for the life of the
            // request, and this handler has already read the items once above; without
            // dropping that memo the "fresh" list would be the pre-check-off one and the
            // swapped-in view would show every item still unticked.
            reads.Invalidate();
            var (freshLists, _, freshItems) = await EndpointHelpers.LoadListAsync(householdKey, resolvedListId, reads, links, opt, ct);
            var listHtml = await ListView.Create(new ListViewModel(freshLists, resolvedListId, freshItems)).RenderAsync();
            return Results.Content(logHtml + Html.AsOutOfBandSwap(listHtml), "text/html");
        });

        api.MapGet("/cart", async (PicnicClient picnic, CancellationToken ct) =>
            Results.Ok(await picnic.GetCartAsync(ct)));
    }
}
