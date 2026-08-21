namespace MealiePicnic;

/// <summary>A Picnic selling unit as shown in search results.</summary>
public sealed record PicnicProduct(
    string Id,
    string Name,
    int? DisplayPrice,
    string UnitQuantity,
    string ImageId)
{
    public string PriceText => DisplayPrice is { } c ? $"€{c / 100m:0.00}" : "";
}

/// <summary>How a shopping list item relates to Picnic.</summary>
public enum LinkState
{
    /// <summary>No picnic extras at all -- needs attention, shown first.</summary>
    New,
    /// <summary>Has picnic_uid, ready for the basket.</summary>
    Linked,
    /// <summary>picnic == "false": deliberately not bought at Picnic.</summary>
    Excluded
}

public sealed record ShoppingItem(
    string ItemId,
    string FoodId,
    string Display,
    string FoodName,
    double Quantity,
    string Label,
    bool Checked,
    LinkState State,
    string? PicnicUid,
    string? PicnicLabel)
{
    public int Amount => Quantity >= 1 ? (int)Math.Ceiling(Quantity) : 1;
}

public sealed record CartResult(string Name, string Uid, int Amount, bool Ok, string? Error);
