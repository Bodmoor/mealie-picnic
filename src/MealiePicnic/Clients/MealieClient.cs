using MealiePicnic.Storage;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealiePicnic.Clients;

/// <summary>
/// Talks to a self-hosted Mealie instance. Only the handful of endpoints we need.
/// </summary>
public sealed class MealieClient(HttpClient http, AppOptions options)
{
    /// <summary>
    /// All Mealie ids interpolated into paths or query strings are UUIDs. Enforcing
    /// that here (not only at the endpoints) means a '../..' or '&perPage=1' can
    /// never reach the URL, however this client ends up being called -- the request
    /// carries the MEALIE_TOKEN, so path traversal would be full API access.
    /// </summary>
    private static string RequireGuid(string value, string name) =>
        Guid.TryParse(value, out var parsed)
            ? parsed.ToString("D")
            : throw new ArgumentException($"{name} is not a valid Mealie id.", name);

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, $"{options.MealieUrl}/api/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.MealieToken);
        return request;
    }

    private async Task<JsonNode> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Mealie {(int)response.StatusCode} on {request.RequestUri?.PathAndQuery}: {body}");
        return JsonNode.Parse(body) ?? new JsonObject();
    }

    /// <summary>All shopping lists, so the UI can offer a picker.</summary>
    public async Task<List<ShoppingListSummary>> GetListsAsync(CancellationToken ct)
    {
        var json = await SendAsync(Request(HttpMethod.Get, "households/shopping/lists?perPage=-1"), ct);

        return [.. (json["items"]?.AsArray() ?? new JsonArray())
            .Where(l => l?["id"] is not null)
            .Select(l => new ShoppingListSummary(
                l!["id"]!.GetValue<string>(),
                l["name"]?.GetValue<string>() ?? "(naamloos)"))];
    }

    /// <summary>Resolve the configured default list name to its id.</summary>
    public async Task<string> GetListIdAsync(CancellationToken ct)
    {
        var lists = await GetListsAsync(ct);

        var match = lists.FirstOrDefault(l =>
            string.Equals(l.Name.Trim(), options.MealieList.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is not null)
            return match.Id;

        throw new InvalidOperationException(
            $"No shopping list named '{options.MealieList}'. Found: " +
            string.Join(", ", lists.Select(l => l.Name)));
    }

    /// <summary>
    /// Items on a list, with no Picnic link classification yet -- that is
    /// household-scoped now (issue #17) and applied afterwards by
    /// <see cref="HouseholdLinkStore.Merge"/>. Checked items are included and
    /// flagged via <see cref="ShoppingItem.Checked"/>; the basket skips them and
    /// the UI keeps them in a collapsed section. Pass a listId to override the
    /// configured default (MEALIE_LIST).
    /// </summary>
    public async Task<List<ShoppingItem>> GetItemsAsync(string? listId, CancellationToken ct)
    {
        listId = string.IsNullOrWhiteSpace(listId)
            ? await GetListIdAsync(ct)
            : RequireGuid(listId, nameof(listId));
        var path = $"households/shopping/items?queryFilter=shoppingListId={listId}" +
                   "&orderBy=position&orderDirection=asc&perPage=-1";

        var json = await SendAsync(Request(HttpMethod.Get, path), ct);
        var result = new List<ShoppingItem>();

        foreach (var node in json["items"]?.AsArray() ?? new JsonArray())
        {
            if (node is null) continue;

            var food = node["food"];
            // Free-text notes have no linked food, so there is nothing to map against.
            if (food is null) continue;

            result.Add(new ShoppingItem(
                ItemId: node["id"]!.GetValue<string>(),
                FoodId: food["id"]!.GetValue<string>(),
                Display: node["display"]?.GetValue<string>() ?? "",
                FoodName: food["name"]?.GetValue<string>() ?? "",
                Quantity: node["quantity"]?.GetValue<double>() ?? 0,
                // Without the unit, "500 gram" is indistinguishable from "500 items".
                Unit: node["unit"]?["name"]?.GetValue<string>()
                      ?? node["unit"]?["abbreviation"]?.GetValue<string>()
                      ?? "",
                Label: node["label"]?["name"]?.GetValue<string>() ?? "",
                Checked: node["checked"]?.GetValue<bool>() ?? false,
                State: LinkState.New,
                PicnicUid: null,
                PicnicLabel: null,
                PicnicPack: null));
        }

        return result.OrderBy(i => i.FoodName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Admin-only: look up the Mealie household a user belongs to by their OIDC
    /// email claim (issue #17). Requires MEALIE_TOKEN to belong to a Mealie
    /// superuser account -- a non-admin token gets a 403 from this endpoint.
    /// Returns null if no Mealie user has this email, which callers must treat
    /// as "household unresolved", not an error.
    /// </summary>
    public async Task<Guid?> FindHouseholdIdByEmailAsync(string email, CancellationToken ct)
    {
        var json = await SendAsync(Request(HttpMethod.Get, "admin/users?perPage=-1"), ct);

        foreach (var user in json["items"]?.AsArray() ?? new JsonArray())
        {
            if (user is null) continue;
            if (!string.Equals(user["email"]?.GetValue<string>(), email, StringComparison.OrdinalIgnoreCase))
                continue;
            if (user["householdId"]?.GetValue<string>() is { Length: > 0 } id && Guid.TryParse(id, out var parsed))
                return parsed;
        }

        return null;
    }

    /// <summary>Tick an item off the Mealie list. Same read-modify-write caveat as foods.</summary>
    public async Task CheckItemAsync(string itemId, CancellationToken ct)
    {
        itemId = RequireGuid(itemId, nameof(itemId));
        var item = await SendAsync(Request(HttpMethod.Get, $"households/shopping/items/{itemId}"), ct);

        var body = new JsonObject
        {
            ["id"] = itemId,
            ["shoppingListId"] = item["shoppingListId"]?.DeepClone(),
            ["checked"] = true,
            ["note"] = item["note"]?.DeepClone() ?? "",
            ["position"] = item["position"]?.DeepClone() ?? 0,
            ["quantity"] = item["quantity"]?.DeepClone() ?? 0,
            ["isFood"] = item["isFood"]?.DeepClone(),
            ["disableAmount"] = item["disableAmount"]?.DeepClone(),
            ["labelId"] = item["labelId"]?.DeepClone(),
            ["foodId"] = item["foodId"]?.DeepClone(),
            ["unitId"] = item["unitId"]?.DeepClone(),
        };

        var request = Request(HttpMethod.Put, $"households/shopping/items/{itemId}");
        request.Content = JsonContent.Create(body);
        await SendAsync(request, ct);
    }
}
