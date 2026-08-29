namespace MealiePicnic.Clients;

/// <summary>
/// Memoises Mealie reads for the life of ONE request.
///
/// The shopping list is shared mutable data -- someone else can edit it in
/// Mealie while you shop -- so it deliberately gets no TTL and no cross-request
/// cache. What it had instead was pure waste: several handlers call
/// <c>LoadListAsync</c>, which fetches every list and every item, and the search
/// view re-runs that on each debounced keystroke purely to find one item by id.
/// One keystroke cost two Mealie round trips before it reached Picnic.
///
/// Registered scoped, so the memo lives exactly as long as the request that
/// filled it. A second request sees Mealie as it is now, which is the whole
/// point of not giving this a TTL.
///
/// Note the Task itself is memoised, not its result. Two consequences, both
/// wanted: concurrent callers within one request share the single in-flight
/// call rather than racing to start a second, and a failure is shared too --
/// one handler must not silently retry an upstream that just failed for
/// another. The cancellation token of the first caller is the one that governs;
/// within a request they are all the same token.
/// </summary>
public sealed class MealieReads(MealieClient mealie)
{
    private Task<List<ShoppingListSummary>>? _lists;
    private readonly Dictionary<string, Task<List<ShoppingItem>>> _items = new(StringComparer.Ordinal);

    public Task<List<ShoppingListSummary>> ListsAsync(CancellationToken ct) =>
        _lists ??= mealie.GetListsAsync(ct);

    public Task<List<ShoppingItem>> ItemsAsync(string? listId, CancellationToken ct)
    {
        // Keyed by list: one request can legitimately touch more than one.
        var key = listId ?? "";
        if (_items.TryGetValue(key, out var pending))
            return pending;

        return _items[key] = mealie.GetItemsAsync(listId, ct);
    }

    /// <summary>
    /// Forget everything read so far, because this request has just changed it.
    ///
    /// Only writes through THIS app need this, and today that means checking
    /// items off during a basket run: that handler deliberately re-reads the
    /// list afterwards to swap the updated view back into the page, and without
    /// this it would be handed the pre-write items it had already read.
    /// </summary>
    public void Invalidate()
    {
        _lists = null;
        _items.Clear();
    }
}
