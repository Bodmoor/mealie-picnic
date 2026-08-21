namespace MealiePicnic;

/// <summary>A Picnic selling unit as shown in search results.</summary>
public sealed record PicnicProduct(
    string Id,
    string Name,
    int? DisplayPrice,
    string UnitQuantity,
    string ImageId)
{
    // Formatted invariantly on purpose: the display string must not depend on the
    // host's culture (a nl-NL host would otherwise render "€2,69").
    public string PriceText => DisplayPrice is { } c
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "€{0:0.00}", c / 100m)
        : "";
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

/// <summary>A Mealie shopping list, for the picker.</summary>
public sealed record ShoppingListSummary(string Id, string Name);

public sealed record CartResult(string Name, string Uid, int Amount, bool Ok, string? Error);
