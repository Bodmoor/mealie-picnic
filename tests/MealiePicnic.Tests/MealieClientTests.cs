using System.Net;
using System.Text.Json.Nodes;

namespace MealiePicnic.Tests;

public class MealieClientTests
{
    private const string Lists = """
        { "items": [ { "id": "list-1", "name": "Boodschappen" },
                     { "id": "list-2", "name": "Andere lijst" } ] }
        """;

    private static string Item(string id, string foodId, string name, object extras,
                               double quantity = 0, bool @checked = false) => $$"""
        {
          "id": "{{id}}", "shoppingListId": "list-1", "note": "", "display": "{{name}}",
          "checked": {{(@checked ? "true" : "false")}}, "position": 0, "quantity": {{quantity}},
          "labelId": "lab-1", "label": { "id": "lab-1", "name": "Zuivel" },
          "foodId": "{{foodId}}",
          "food": { "id": "{{foodId}}", "name": "{{name}}", "description": "",
                    "aliases": [], "extras": {{extras}} },
          "unitId": null, "unit": null, "isFood": null, "disableAmount": null
        }
        """;

    [Fact]
    public async Task Resolves_list_id_by_name_case_insensitively()
    {
        var handler = new StubHandler().OnJson("shopping/lists", Lists);

        var id = await TestFactory.Mealie(handler).GetListIdAsync(default);

        Assert.Equal("list-1", id);
    }

    [Fact]
    public async Task Throws_with_available_names_when_list_missing()
    {
        var handler = new StubHandler().OnJson("shopping/lists",
            """{ "items": [ { "id": "x", "name": "Weekend" } ] }""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestFactory.Mealie(handler).GetListIdAsync(default));

        Assert.Contains("Boodschappen", ex.Message);
        Assert.Contains("Weekend", ex.Message);   // tells the user what does exist
    }

    [Fact]
    public async Task Classifies_items_by_picnic_extras()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", $$"""
                { "items": [
                    {{Item("i1", "f1", "melk", """{ "picnic_uid": "s1", "picnic": "true" }""")}},
                    {{Item("i2", "f2", "zout", """{ "picnic": "false" }""")}},
                    {{Item("i3", "f3", "wrap", "{}")}}
                ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Equal(LinkState.Linked, items.Single(i => i.FoodName == "melk").State);
        Assert.Equal(LinkState.Excluded, items.Single(i => i.FoodName == "zout").State);
        Assert.Equal(LinkState.New, items.Single(i => i.FoodName == "wrap").State);
    }

    [Fact]
    public async Task Puts_new_items_first()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", $$"""
                { "items": [
                    {{Item("i1", "f1", "aaa-linked", """{ "picnic_uid": "s1" }""")}},
                    {{Item("i2", "f2", "zzz-excluded", """{ "picnic": "false" }""")}},
                    {{Item("i3", "f3", "mmm-new", "{}")}}
                ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Equal(new[] { "mmm-new", "aaa-linked", "zzz-excluded" }, items.Select(i => i.FoodName));
    }

    [Fact]
    public async Task Skips_free_text_notes_without_a_food()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", """
                { "items": [ { "id": "n1", "shoppingListId": "list-1", "note": "kaarsjes",
                               "display": "kaarsjes", "checked": false, "position": 0,
                               "quantity": 0, "food": null } ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Empty(items);   // nothing to map a Picnic product against
    }

    [Fact]
    public async Task SetFoodExtras_merges_and_preserves_aliases()
    {
        // Regression: Mealie has no PATCH and PUT replaces the object, so omitting
        // aliases from the body silently destroys them.
        var handler = new StubHandler()
            .OnJson("foods/f1", """
                {
                  "id": "f1", "name": "wrap", "pluralName": "wraps", "description": "",
                  "aliases": [ { "name": "witte tortilla" } ],
                  "label": { "id": "lab-9", "name": "Brood" },
                  "extras": { "keep_me": "yes" }
                }
                """);

        var merged = await TestFactory.Mealie(handler)
            .SetFoodExtrasAsync("f1", new Dictionary<string, string> { ["picnic_uid"] = "s1005080" }, default);

        Assert.Equal("yes", merged["keep_me"]!.GetValue<string>());
        Assert.Equal("s1005080", merged["picnic_uid"]!.GetValue<string>());

        var put = handler.Sent.Single(s => s.Method == HttpMethod.Put);
        var body = JsonNode.Parse(put.Body)!;

        Assert.Equal("witte tortilla", body["aliases"]![0]!["name"]!.GetValue<string>());
        Assert.Equal("wraps", body["pluralName"]!.GetValue<string>());
        Assert.Equal("lab-9", body["labelId"]!.GetValue<string>());
        Assert.Equal("yes", body["extras"]!["keep_me"]!.GetValue<string>());
    }

    [Fact]
    public async Task CheckItem_sets_checked_true_and_echoes_identity()
    {
        var handler = new StubHandler().OnJson("shopping/items/i1", """
            { "id": "i1", "shoppingListId": "list-1", "note": "", "position": 3,
              "quantity": 2, "isFood": true, "disableAmount": false,
              "labelId": "lab-1", "foodId": "f1", "unitId": null, "checked": false }
            """);

        await TestFactory.Mealie(handler).CheckItemAsync("i1", default);

        var body = JsonNode.Parse(handler.Sent.Single(s => s.Method == HttpMethod.Put).Body)!;

        Assert.True(body["checked"]!.GetValue<bool>());
        Assert.Equal("list-1", body["shoppingListId"]!.GetValue<string>());
        Assert.Equal("f1", body["foodId"]!.GetValue<string>());
        Assert.Equal(3, body["position"]!.GetValue<int>());
    }

    [Fact]
    public async Task Surfaces_mealie_errors_with_status_and_body()
    {
        var handler = new StubHandler().OnJson("shopping/lists",
            """{"detail":"Not authenticated"}""", HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => TestFactory.Mealie(handler).GetListIdAsync(default));

        Assert.Contains("401", ex.Message);
        Assert.Contains("Not authenticated", ex.Message);
    }

    [Fact]
    public async Task GetLists_returns_every_list()
    {
        var handler = new StubHandler().OnJson("shopping/lists", Lists);

        var lists = await TestFactory.Mealie(handler).GetListsAsync(default);

        Assert.Equal(new[] { "Boodschappen", "Andere lijst" }, lists.Select(l => l.Name));
        Assert.Equal("list-1", lists[0].Id);
    }

    [Fact]
    public async Task Explicit_listId_skips_the_lists_lookup()
    {
        var handler = new StubHandler().OnJson("shopping/items", """{ "items": [] }""");

        await TestFactory.Mealie(handler).GetItemsAsync("list-9", default);

        // Only the items call: MEALIE_LIST never needs resolving.
        var call = Assert.Single(handler.Sent);
        Assert.Contains("shoppingListId=list-9", call.Url);
    }

    [Fact]
    public async Task Keeps_checked_items_but_flags_them()
    {
        // The basket skips them; the UI shows them in a collapsed 'done' section.
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", $$"""
                { "items": [
                    {{Item("i1", "f1", "melk", """{ "picnic_uid": "s1" }""", @checked: true)}},
                    {{Item("i2", "f2", "wrap", """{ "picnic_uid": "s2" }""")}}
                ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.FoodName == "melk").Checked);
        Assert.False(items.Single(i => i.FoodName == "wrap").Checked);
    }
}
