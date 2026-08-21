using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MealiePicnic;

/// <summary>
/// Talks to a self-hosted Mealie instance. Only the handful of endpoints we need.
/// </summary>
public sealed class MealieClient(HttpClient http, AppOptions options, ILogger<MealieClient> log)
{
    public const string ExtraUid = "picnic_uid";
    public const string ExtraFlag = "picnic";
    public const string ExtraLabel = "picnic_label";

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

    /// <summary>Resolve the configured list name to its id.</summary>
    public async Task<string> GetListIdAsync(CancellationToken ct)
    {
        var json = await SendAsync(Request(HttpMethod.Get, "households/shopping/lists?perPage=-1"), ct);
        var lists = json["items"]?.AsArray() ?? new JsonArray();

        foreach (var list in lists)
        {
            var name = list?["name"]?.GetValue<string>() ?? "";
            if (string.Equals(name.Trim(), options.MealieList.Trim(), StringComparison.OrdinalIgnoreCase))
                return list!["id"]!.GetValue<string>();
        }

        var found = string.Join(", ", lists.Select(l => l?["name"]?.GetValue<string>()));
        throw new InvalidOperationException($"No shopping list named '{options.MealieList}'. Found: {found}");
    }

    /// <summary>Unchecked items on the list, classified by their picnic extras.</summary>
    public async Task<List<ShoppingItem>> GetItemsAsync(CancellationToken ct)
    {
        var listId = await GetListIdAsync(ct);
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

            var extras = food["extras"] as JsonObject;
            var uid = extras?[ExtraUid]?.GetValue<string>();
            var flag = extras?[ExtraFlag]?.ToString();

            var state = string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase)
                ? LinkState.Excluded
                : !string.IsNullOrWhiteSpace(uid)
                    ? LinkState.Linked
                    : LinkState.New;

            result.Add(new ShoppingItem(
                ItemId: node["id"]!.GetValue<string>(),
                FoodId: food["id"]!.GetValue<string>(),
                Display: node["display"]?.GetValue<string>() ?? "",
                FoodName: food["name"]?.GetValue<string>() ?? "",
                Quantity: node["quantity"]?.GetValue<double>() ?? 0,
                Label: node["label"]?["name"]?.GetValue<string>() ?? "",
                Checked: node["checked"]?.GetValue<bool>() ?? false,
                State: state,
                PicnicUid: uid,
                PicnicLabel: extras?[ExtraLabel]?.GetValue<string>()));
        }

        // 'New' first: those are the ones needing a decision.
        return result
            .OrderBy(i => i.State switch { LinkState.New => 0, LinkState.Linked => 1, _ => 2 })
            .ThenBy(i => i.FoodName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Merge keys into a food's extras.
    /// Mealie has no PATCH on /api/foods/{id} and PUT REPLACES the whole object,
    /// so every field we want to keep must be echoed back -- notably 'aliases',
    /// which are silently destroyed otherwise.
    /// </summary>
    public async Task<JsonObject> SetFoodExtrasAsync(
        string foodId, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var food = await SendAsync(Request(HttpMethod.Get, $"foods/{foodId}"), ct);

        var extras = food["extras"] as JsonObject ?? new JsonObject();
        var merged = new JsonObject();
        foreach (var pair in extras)
            merged[pair.Key] = pair.Value?.DeepClone();
        foreach (var (key, value) in values)
            merged[key] = value;

        var body = new JsonObject
        {
            ["id"] = foodId,
            ["name"] = food["name"]?.DeepClone(),
            ["pluralName"] = food["pluralName"]?.DeepClone(),
            ["description"] = food["description"]?.DeepClone() ?? "",
            ["labelId"] = food["label"]?["id"]?.DeepClone() ?? food["labelId"]?.DeepClone(),
            ["aliases"] = food["aliases"]?.DeepClone() ?? new JsonArray(),
            ["extras"] = merged.DeepClone(),
        };

        var request = Request(HttpMethod.Put, $"foods/{foodId}");
        request.Content = JsonContent.Create(body);
        await SendAsync(request, ct);

        log.LogInformation("Food {FoodId} extras updated: {Keys}", foodId, string.Join(",", values.Keys));
        return merged;
    }

    /// <summary>Tick an item off the Mealie list. Same read-modify-write caveat as foods.</summary>
    public async Task CheckItemAsync(string itemId, CancellationToken ct)
    {
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
