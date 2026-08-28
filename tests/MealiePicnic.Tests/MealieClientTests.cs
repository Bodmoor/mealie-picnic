using MealiePicnic.Clients;
using System.Net;
using System.Text.Json.Nodes;

namespace MealiePicnic.Tests;

public class MealieClientTests
{
    private const string Lists = """
        { "items": [ { "id": "dddddddd-1111-1111-1111-111111111111", "name": "Boodschappen" },
                     { "id": "dddddddd-2222-2222-2222-222222222222", "name": "Andere lijst" } ] }
        """;

    private static string Item(string id, string foodId, string name,
                               double quantity = 0, bool @checked = false) => $$"""
        {
          "id": "{{id}}", "shoppingListId": "dddddddd-1111-1111-1111-111111111111", "note": "", "display": "{{name}}",
          "checked": {{(@checked ? "true" : "false")}}, "position": 0, "quantity": {{quantity}},
          "labelId": "bbbbbbbb-1111-1111-1111-111111111111", "label": { "id": "bbbbbbbb-1111-1111-1111-111111111111", "name": "Zuivel" },
          "foodId": "{{foodId}}",
          "food": { "id": "{{foodId}}", "name": "{{name}}", "description": "", "aliases": [] },
          "unitId": null, "unit": null, "isFood": null, "disableAmount": null
        }
        """;

    [Fact]
    public async Task Resolves_list_id_by_name_case_insensitively()
    {
        var handler = new StubHandler().OnJson("shopping/lists", Lists);

        var id = await TestFactory.Mealie(handler).GetListIdAsync(default);

        Assert.Equal("dddddddd-1111-1111-1111-111111111111", id);
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
    public async Task Items_start_unclassified_pending_the_household_merge()
    {
        // Picnic link classification is household-scoped now (issue #17); the
        // Mealie client itself no longer knows about picnic_* extras at all.
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", $$"""
                { "items": [ {{Item("11111111-1111-1111-1111-111111111111", "aaaaaaaa-1111-1111-1111-111111111111", "melk")}} ] }
                """);

        var item = Assert.Single(await TestFactory.Mealie(handler).GetItemsAsync(null, default));

        Assert.Equal(LinkState.New, item.State);
        Assert.Null(item.PicnicUid);
        Assert.Null(item.PicnicLabel);
        Assert.Null(item.PicnicPack);
    }

    [Fact]
    public async Task Orders_items_alphabetically_by_food_name()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", $$"""
                { "items": [
                    {{Item("11111111-1111-1111-1111-111111111111", "aaaaaaaa-1111-1111-1111-111111111111", "zout")}},
                    {{Item("22222222-2222-2222-2222-222222222222", "aaaaaaaa-2222-2222-2222-222222222222", "appel")}}
                ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Equal(new[] { "appel", "zout" }, items.Select(i => i.FoodName));
    }

    [Fact]
    public async Task Skips_free_text_notes_without_a_food()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", """
                { "items": [ { "id": "n1", "shoppingListId": "dddddddd-1111-1111-1111-111111111111", "note": "kaarsjes",
                               "display": "kaarsjes", "checked": false, "position": 0,
                               "quantity": 0, "food": null } ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Empty(items);   // nothing to map a Picnic product against
    }

    [Fact]
    public async Task CheckItem_sets_checked_true_and_echoes_identity()
    {
        var handler = new StubHandler().OnJson("shopping/items/", """
            { "id": "11111111-1111-1111-1111-111111111111", "shoppingListId": "dddddddd-1111-1111-1111-111111111111", "note": "", "position": 3,
              "quantity": 2, "isFood": true, "disableAmount": false,
              "labelId": "bbbbbbbb-1111-1111-1111-111111111111", "foodId": "aaaaaaaa-1111-1111-1111-111111111111", "unitId": null, "checked": false }
            """);

        await TestFactory.Mealie(handler).CheckItemAsync("11111111-1111-1111-1111-111111111111", default);

        var body = JsonNode.Parse(handler.Sent.Single(s => s.Method == HttpMethod.Put).Body)!;

        Assert.True(body["checked"]!.GetValue<bool>());
        Assert.Equal("dddddddd-1111-1111-1111-111111111111", body["shoppingListId"]!.GetValue<string>());
        Assert.Equal("aaaaaaaa-1111-1111-1111-111111111111", body["foodId"]!.GetValue<string>());
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
        Assert.Equal("dddddddd-1111-1111-1111-111111111111", lists[0].Id);
    }

    [Fact]
    public async Task Explicit_listId_skips_the_lists_lookup()
    {
        var handler = new StubHandler().OnJson("shopping/items", """{ "items": [] }""");

        await TestFactory.Mealie(handler).GetItemsAsync("dddddddd-9999-9999-9999-999999999999", default);

        // Only the items call: MEALIE_LIST never needs resolving.
        var call = Assert.Single(handler.Sent);
        Assert.Contains("shoppingListId=dddddddd-9999-9999-9999-999999999999", call.Url);
    }

    [Fact]
    public async Task Keeps_checked_items_but_flags_them()
    {
        // The basket skips them; the UI shows them in a collapsed 'done' section.
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", $$"""
                { "items": [
                    {{Item("11111111-1111-1111-1111-111111111111", "aaaaaaaa-1111-1111-1111-111111111111", "melk", @checked: true)}},
                    {{Item("22222222-2222-2222-2222-222222222222", "aaaaaaaa-2222-2222-2222-222222222222", "wrap")}}
                ] }
                """);

        var items = await TestFactory.Mealie(handler).GetItemsAsync(null, default);

        Assert.Equal(2, items.Count);
        Assert.True(items.Single(i => i.FoodName == "melk").Checked);
        Assert.False(items.Single(i => i.FoodName == "wrap").Checked);
    }

    [Theory]
    [InlineData("x&perPage=1")]
    [InlineData("../../foods")]
    public async Task Rejects_non_guid_list_ids_before_any_request(string listId)
    {
        // B1: listId is interpolated into a query string; '&' would inject params.
        var handler = new StubHandler();

        await Assert.ThrowsAsync<ArgumentException>(
            () => TestFactory.Mealie(handler).GetItemsAsync(listId, default));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Rejects_non_guid_item_ids_before_any_request()
    {
        var handler = new StubHandler();

        await Assert.ThrowsAsync<ArgumentException>(
            () => TestFactory.Mealie(handler).CheckItemAsync("../../app/about", default));

        Assert.Empty(handler.Sent);
    }

    [Fact]
    public async Task Reads_the_unit_so_a_weight_is_not_a_pack_count()
    {
        // Issue #5: without the unit, quantity 500 became 500 packs.
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", """
                { "items": [ {
                    "id": "11111111-1111-1111-1111-111111111111",
                    "shoppingListId": "dddddddd-1111-1111-1111-111111111111",
                    "note": "", "display": "500 gram bloem", "checked": false,
                    "position": 0, "quantity": 500,
                    "unitId": "cccccccc-1111-1111-1111-111111111111",
                    "unit": { "id": "cccccccc-1111-1111-1111-111111111111", "name": "gram" },
                    "labelId": null, "label": null,
                    "foodId": "aaaaaaaa-1111-1111-1111-111111111111",
                    "food": { "id": "aaaaaaaa-1111-1111-1111-111111111111", "name": "bloem",
                              "description": "", "aliases": [] }
                } ] }
                """);

        var item = Assert.Single(await TestFactory.Mealie(handler).GetItemsAsync(null, default));

        Assert.Equal("gram", item.Unit);
    }

    [Fact]
    public async Task Falls_back_to_the_unit_abbreviation()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", """
                { "items": [ {
                    "id": "22222222-2222-2222-2222-222222222222",
                    "shoppingListId": "dddddddd-1111-1111-1111-111111111111",
                    "note": "", "display": "2 kg aardappels", "checked": false,
                    "position": 0, "quantity": 2,
                    "unit": { "id": "cccccccc-2222-2222-2222-222222222222", "abbreviation": "kg" },
                    "foodId": "aaaaaaaa-2222-2222-2222-222222222222",
                    "food": { "id": "aaaaaaaa-2222-2222-2222-222222222222", "name": "aardappel",
                              "description": "", "aliases": [] }
                } ] }
                """);

        var item = Assert.Single(await TestFactory.Mealie(handler).GetItemsAsync(null, default));

        Assert.Equal("kg", item.Unit);
    }

    // ------------------------------------------------------------ household lookup

    private static string AdminUser(string email, string householdId) => $$"""
        { "id": "cccccccc-0000-0000-0000-000000000000", "email": "{{email}}", "householdId": "{{householdId}}" }
        """;

    [Fact]
    public async Task Finds_the_household_id_for_a_matching_email()
    {
        var householdId = "eeeeeeee-1111-1111-1111-111111111111";
        var handler = new StubHandler().OnJson("admin/users", $$"""
            { "items": [ {{AdminUser("someone.else@example.com", "eeeeeeee-9999-9999-9999-999999999999")}},
                        {{AdminUser("alice@example.com", householdId)}} ] }
            """);

        var found = await TestFactory.Mealie(handler).FindHouseholdIdByEmailAsync("alice@example.com", default);

        Assert.Equal(Guid.Parse(householdId), found);
    }

    [Fact]
    public async Task Household_lookup_is_case_insensitive_on_email()
    {
        var householdId = "eeeeeeee-1111-1111-1111-111111111111";
        var handler = new StubHandler().OnJson("admin/users", $$"""
            { "items": [ {{AdminUser("Alice@Example.com", householdId)}} ] }
            """);

        var found = await TestFactory.Mealie(handler).FindHouseholdIdByEmailAsync("alice@example.com", default);

        Assert.Equal(Guid.Parse(householdId), found);
    }

    [Fact]
    public async Task Household_lookup_returns_null_when_email_not_found()
    {
        var handler = new StubHandler().OnJson("admin/users", """{ "items": [] }""");

        var found = await TestFactory.Mealie(handler).FindHouseholdIdByEmailAsync("nobody@example.com", default);

        Assert.Null(found);
    }

    [Fact]
    public async Task Household_lookup_surfaces_a_403_from_a_non_admin_token()
    {
        var handler = new StubHandler().OnJson("admin/users",
            """{"detail":"Not authenticated"}""", HttpStatusCode.Forbidden);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => TestFactory.Mealie(handler).FindHouseholdIdByEmailAsync("alice@example.com", default));

        Assert.Contains("403", ex.Message);
    }
}
