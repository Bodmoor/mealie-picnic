using MealiePicnic.Clients;
using MealiePicnic.Storage;

namespace MealiePicnic.Presentation;

/// <summary>Model for Slices/ListView.cshtml -- the whole shopping-list view.</summary>
public sealed record ListViewModel(
    List<ShoppingListSummary> Lists,
    string CurrentListId,
    List<ShoppingItem> Items);

/// <summary>
/// Model for Slices/SearchView.cshtml. ListId rides along in every link on the page
/// (back, search, exclude) purely so the eventual "back to the list" or "after
/// linking, show the list again" fragment knows which list to re-render -- the
/// server holds no per-request session, so nothing else remembers it.
/// </summary>
public sealed record SearchViewModel(ShoppingItem Item, string ListId);

/// <summary>Model for Slices/HitsGrid.cshtml -- a Picnic search result grid for one food.</summary>
public sealed record HitsModel(ShoppingItem Item, List<PicnicProduct> Products, string ListId);

/// <summary>Model for Slices/BasketLog.cshtml -- the outcome of an "add to basket" run.</summary>
public sealed record BasketLogModel(List<CartResult> Results, int Unmapped, bool Aborted, bool CheckOff);

/// <summary>
/// Model for Slices/ProductDetail.cshtml -- everything known about one Picnic
/// product (issue #48).
///
/// Name, pack and image id come along from the card that opened this rather than
/// from a second search: the card already has them, and re-running the search to
/// recover a name the user is looking at would be a round trip for nothing. The
/// product page supplies the rest.
/// </summary>
public sealed record ProductDetailModel(
    ShoppingItem Item,
    string ListId,
    string ProductId,
    string? Name,
    string? Pack,
    string? ImageId,
    PicnicDetails Details,
    IReadOnlyDictionary<string, AllergenVerdict> Verdicts)
{
    /// <summary>The verdict on one allergen group, if anyone has recorded one.</summary>
    public AllergenVerdict? VerdictFor(string group) =>
        Verdicts.TryGetValue(group, out var verdict) ? verdict : null;

    /// <summary>
    /// True when the mark's current evidence differs from the text the verdict was
    /// recorded against -- Picnic reformulated, so the verdict is about a recipe
    /// that no longer applies. Shown as a note rather than discarding the verdict:
    /// dropping it silently would be worse than saying it is old.
    /// </summary>
    public bool IsStale(AllergenMark mark, AllergenVerdict verdict) =>
        verdict.Against is { Length: > 0 } against
        && mark.Source is { Length: > 0 } now
        && !string.Equals(against, now, StringComparison.Ordinal);
}
