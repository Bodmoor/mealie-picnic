using MealiePicnic.Clients;

namespace MealiePicnic.Tests;

/// <summary>
/// The waste this removes: several handlers call LoadListAsync, which fetches
/// every list and every item, and the search view re-ran that per debounced
/// keystroke purely to find one item by id. One keystroke cost two Mealie round
/// trips before it reached Picnic.
///
/// What must NOT be removed is freshness across requests. The shopping list is
/// shared mutable data, so the memo is deliberately scoped to one request; the
/// tests below pin both halves of that.
/// </summary>
public class MealieReadsTests
{
    private const string Lists = """
        { "items": [ { "id": "11111111-1111-1111-1111-111111111111", "name": "Boodschappen" } ] }
        """;

    private const string Items = """
        { "items": [ { "id": "22222222-2222-2222-2222-222222222222",
                       "display": "melk", "quantity": 1,
                       "food": { "id": "33333333-3333-3333-3333-333333333333", "name": "melk" } } ] }
        """;

    private static (MealieReads Reads, StubHandler Handler) Subject()
    {
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", Items);
        return (new MealieReads(TestFactory.Mealie(handler)), handler);
    }

    private static int Count(StubHandler handler, string fragment) =>
        handler.Sent.Count(x => x.Url.Contains(fragment));

    [Fact]
    public async Task Repeated_reads_within_one_request_hit_mealie_once()
    {
        var (reads, handler) = Subject();

        for (var i = 0; i < 5; i++)
        {
            await reads.ListsAsync(default);
            await reads.ItemsAsync("11111111-1111-1111-1111-111111111111", default);
        }

        Assert.Equal(1, Count(handler, "shopping/lists"));
        Assert.Equal(1, Count(handler, "shopping/items"));
    }

    [Fact]
    public async Task Concurrent_readers_share_one_in_flight_call()
    {
        // The Task is memoised, not its result, so two callers inside one request
        // cannot race into two upstream calls.
        var (reads, handler) = Subject();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => reads.ListsAsync(default)));

        Assert.Equal(1, Count(handler, "shopping/lists"));
    }

    [Fact]
    public async Task Different_lists_are_read_separately()
    {
        var (reads, handler) = Subject();

        await reads.ItemsAsync("11111111-1111-1111-1111-111111111111", default);
        await reads.ItemsAsync("44444444-4444-4444-4444-444444444444", default);

        Assert.Equal(2, Count(handler, "shopping/items"));
    }

    [Fact]
    public async Task A_new_request_reads_mealie_again()
    {
        // The scoped lifetime is the point: someone else editing the list in
        // Mealie must be visible on the next request, not after a TTL.
        var handler = new StubHandler()
            .OnJson("shopping/lists", Lists)
            .OnJson("shopping/items", Items);

        await new MealieReads(TestFactory.Mealie(handler)).ListsAsync(default);
        await new MealieReads(TestFactory.Mealie(handler)).ListsAsync(default);

        Assert.Equal(2, Count(handler, "shopping/lists"));
    }

    [Fact]
    public async Task Invalidate_forces_a_re_read_after_this_app_writes()
    {
        // What the basket run needs after checking items off: its deliberate
        // re-read has to see the write, not the memo taken before it.
        var (reads, handler) = Subject();

        await reads.ListsAsync(default);
        await reads.ItemsAsync("11111111-1111-1111-1111-111111111111", default);

        reads.Invalidate();

        await reads.ListsAsync(default);
        await reads.ItemsAsync("11111111-1111-1111-1111-111111111111", default);

        Assert.Equal(2, Count(handler, "shopping/lists"));
        Assert.Equal(2, Count(handler, "shopping/items"));
    }
}
