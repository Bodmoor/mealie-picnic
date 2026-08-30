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

/// <summary>
/// Helpers shared by more than one endpoint group (issue #66). They were local
/// functions in Program.cs, which is why they had to live there: three groups use
/// LoadListAsync, and four answer with NoHousehold when the signed-in identity
/// resolved to no Mealie household at login.
///
/// Every one of them takes what it needs as a parameter and captures nothing,
/// which is what made moving them a rename rather than a refactor.
/// </summary>
internal static class EndpointHelpers
{
    // The signed-in identity resolved to no Mealie household at login (see the
    // OIDC OnTicketReceived handler above) -- e.g. the email is not linked to a
    // household in Mealie yet. Not a permission problem (403), a data/config one:
    // naming the email lets whoever sees it know exactly what to fix (issue #17).
    internal static IResult NoHousehold(ClaimsPrincipal user)
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

    internal static async Task<string> ResolveListIdAsync(
        string householdKey, List<ShoppingListSummary> lists, HouseholdLinkStore links, AppOptions opt)
    {
        var remembered = links.SelectedListId(householdKey);
        if (remembered is not null && lists.Any(l => l.Id == remembered))
            return remembered;

        var fallback = lists.FirstOrDefault(l =>
            l.Name.Trim().Equals(opt.MealieList.Trim(), StringComparison.OrdinalIgnoreCase));
        return (fallback ?? lists.FirstOrDefault())?.Id ?? "";
    }

    internal static async Task<(List<ShoppingListSummary> Lists, string ListId, List<ShoppingItem> Items)> LoadListAsync(
        string householdKey, string? listId, MealieReads reads, HouseholdLinkStore links, AppOptions opt,
        CancellationToken ct)
    {
        var lists = await reads.ListsAsync(ct);
        var resolvedListId = string.IsNullOrEmpty(listId)
            ? await ResolveListIdAsync(householdKey, lists, links, opt)
            : listId;
        var items = links.Merge(householdKey, await reads.ItemsAsync(
            string.IsNullOrEmpty(resolvedListId) ? null : resolvedListId, ct));
        return (lists, resolvedListId, items);
    }

    internal static async Task<IResult> RenderListAsync(
        string householdKey, string? listId, MealieReads reads, HouseholdLinkStore links, AppOptions opt,
        CancellationToken ct)
    {
        var (lists, resolvedListId, items) = await LoadListAsync(householdKey, listId, reads, links, opt, ct);
        var html = await ListView.Create(new ListViewModel(lists, resolvedListId, items)).RenderAsync();
        return Results.Content(html, "text/html");
    }
}
