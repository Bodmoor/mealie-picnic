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
    string Unit,
    string Label,
    bool Checked,
    LinkState State,
    string? PicnicUid,
    string? PicnicLabel,
    string? PicnicPack)
{
    /// <summary>
    /// How many packs to add. Quantity alone is not enough: "500 gram" must not
    /// become 500 packs, which is what happened before Unit was taken into
    /// account (issue #5).
    /// </summary>
    public int Amount => Quantities.Required(Quantity, Unit, PicnicPack);

    /// <summary>Short explanation of the amount, for the UI.</summary>
    public string AmountReason
    {
        get
        {
            if (Amount == 1 && Quantities.ResolveUnit(Unit).Dimension is not Dimension.Count)
                return string.IsNullOrWhiteSpace(PicnicPack)
                    ? "1 pak (geen pakgrootte bekend)"
                    : $"1 x {PicnicPack}";
            return string.IsNullOrWhiteSpace(PicnicPack)
                ? $"{Amount} x"
                : $"{Amount} x {PicnicPack}";
        }
    }
}

/// <summary>A Mealie shopping list, for the picker.</summary>
public sealed record ShoppingListSummary(string Id, string Name);

public sealed record CartResult(string Name, string Uid, int Amount, bool Ok, string? Error);
