using MealiePicnic.Clients;
using MealiePicnic.Storage;

namespace MealiePicnic.Tests;

public class HouseholdLinkStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private HouseholdLinkStore New() => new(TestFactory.Options(_dir));

    private static ShoppingItem RawItem(string foodId, string name, double quantity = 0, string unit = "") =>
        new(ItemId: Guid.NewGuid().ToString(), FoodId: foodId, Display: name, FoodName: name,
            Quantity: quantity, Unit: unit, Label: "", Checked: false,
            State: LinkState.New, PicnicUid: null, PicnicLabel: null, PicnicPack: null);

    [Fact]
    public void Unlinked_items_stay_new()
    {
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "melk");

        var merged = Assert.Single(New().Merge("household-a", [item]));

        Assert.Equal(LinkState.New, merged.State);
    }

    [Fact]
    public void Link_marks_the_food_linked_with_its_picnic_details()
    {
        var store = New();
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "melk");

        store.Link("household-a", item.FoodId, "s1005080", "Melk halfvol 1L", "1 l");
        var merged = Assert.Single(store.Merge("household-a", [item]));

        Assert.Equal(LinkState.Linked, merged.State);
        Assert.Equal("s1005080", merged.PicnicUid);
        Assert.Equal("Melk halfvol 1L", merged.PicnicLabel);
        Assert.Equal("1 l", merged.PicnicPack);
    }

    [Fact]
    public void Exclude_marks_the_food_excluded()
    {
        var store = New();
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "zout");

        store.Exclude("household-a", item.FoodId);
        var merged = Assert.Single(store.Merge("household-a", [item]));

        Assert.Equal(LinkState.Excluded, merged.State);
    }

    [Fact]
    public void Clear_reverts_a_link_or_exclusion_back_to_new()
    {
        var store = New();
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "zout");
        store.Exclude("household-a", item.FoodId);

        store.Clear("household-a", item.FoodId);
        var merged = Assert.Single(store.Merge("household-a", [item]));

        Assert.Equal(LinkState.New, merged.State);
    }

    [Fact]
    public void Persists_across_instances()
    {
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "melk");
        New().Link("household-a", item.FoodId, "s1005080", null, null);

        var merged = Assert.Single(New().Merge("household-a", [item]));

        Assert.Equal(LinkState.Linked, merged.State);
        Assert.Equal("s1005080", merged.PicnicUid);
    }

    [Fact]
    public void Different_households_are_isolated()
    {
        var store = New();
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "melk");
        store.Link("household-a", item.FoodId, "s1005080", null, null);

        var merged = Assert.Single(store.Merge("household-b", [item]));

        Assert.Equal(LinkState.New, merged.State);   // household-b never linked it
    }

    [Fact]
    public void Links_live_under_a_households_subdirectory()
    {
        var store = New();

        store.Link("household-a", "aaaaaaaa-1111-1111-1111-111111111111", "s1", null, null);

        Assert.True(File.Exists(Path.Combine(_dir, "households", "household-a", "links.json")));
    }

    [Fact]
    public void Merge_orders_new_first_then_linked_then_excluded_alphabetically()
    {
        var store = New();
        var fresh = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "zzz-new");
        var linkedA = RawItem("aaaaaaaa-2222-2222-2222-222222222222", "bbb-linked");
        var linkedB = RawItem("aaaaaaaa-3333-3333-3333-333333333333", "aaa-linked");
        var excluded = RawItem("aaaaaaaa-4444-4444-4444-444444444444", "mmm-excluded");

        store.Link("household-a", linkedA.FoodId, "s1", null, null);
        store.Link("household-a", linkedB.FoodId, "s2", null, null);
        store.Exclude("household-a", excluded.FoodId);

        var merged = store.Merge("household-a", [fresh, linkedA, linkedB, excluded]);

        Assert.Equal(new[] { "zzz-new", "aaa-linked", "bbb-linked", "mmm-excluded" },
            merged.Select(i => i.FoodName));
    }

    [Fact]
    public void Merged_pack_size_feeds_the_amount_calculation()
    {
        // Issue #5's fix now runs on the merged item: the pack size comes from
        // the household link, not from Mealie extras.
        var store = New();
        var item = RawItem("aaaaaaaa-1111-1111-1111-111111111111", "bloem", quantity: 500, unit: "gram");
        store.Link("household-a", item.FoodId, "s1", null, "1 kg");

        var merged = Assert.Single(store.Merge("household-a", [item]));

        Assert.Equal(1, merged.Amount);   // 500 g needed, 1 kg packs -> one pack
    }

    [Fact]
    public void Creates_the_data_directory()
    {
        New();

        Assert.True(Directory.Exists(_dir));
    }

    [Fact]
    public void Save_leaves_no_temp_file_behind()
    {
        New().Link("household-a", "aaaaaaaa-1111-1111-1111-111111111111", "s1", null, null);

        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "households", "household-a"), "*.tmp"));
    }

    [Fact]
    public void Selected_list_starts_unset()
    {
        Assert.Null(New().SelectedListId("household-a"));
    }

    [Fact]
    public void Selected_list_persists_across_instances()
    {
        New().SelectList("household-a", "dddddddd-1111-1111-1111-111111111111");

        Assert.Equal("dddddddd-1111-1111-1111-111111111111", New().SelectedListId("household-a"));
    }

    [Fact]
    public void Selected_list_is_isolated_per_household()
    {
        var store = New();
        store.SelectList("household-a", "dddddddd-1111-1111-1111-111111111111");

        Assert.Null(store.SelectedListId("household-b"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
