using MealiePicnic.Clients;

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
