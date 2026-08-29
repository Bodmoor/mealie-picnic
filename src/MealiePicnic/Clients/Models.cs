namespace MealiePicnic.Clients;

/// <summary>A Picnic selling unit as shown in search results.</summary>
public sealed record PicnicProduct(
    string Id,
    string Name,
    int? DisplayPrice,
    string UnitQuantity,
    string ImageId)
{
    // The host's culture must not decide this -- that was always the rule, and it
    // is why this used to be formatted invariantly. What changed with issue #13 is
    // who decides instead: the configured interface language, so Dutch reads
    // "€2,69" and English "€2.69". The salt badge in app.js follows the same
    // separator, which it did not before: it hard-coded a comma while this
    // hard-coded a point, and one card showed both.
    public string PriceText => PriceTextIn(AppText.Current);

    /// <summary>The price in a given language -- the overload tests use, so they
    /// can check both without reaching for the process-wide setting.</summary>
    public string PriceTextIn(AppText text) => DisplayPrice is { } c
        ? string.Format(text.Numbers, "€{0:0.00}", c / 100m)
        : "";
}

/// <summary>
/// The facts Picnic only publishes on the product page (issue #6): whether the
/// product is organic, and its salt content per 100 g.
///
/// Both are optional by design. <see cref="Organic"/> false means "no organic
/// claim found", not "conventional", and a null <see cref="SaltGramsPer100"/>
/// means the nutrition table could not be read -- neither is worth showing as a
/// negative, so the UI simply omits what it does not know.
/// </summary>
public sealed record PicnicDetails(
    string Id,
    bool Organic,
    double? SaltGramsPer100,
    string? SaltText);

/// <summary>How a shopping list item relates to Picnic, for the current household.</summary>
public enum LinkState
{
    /// <summary>No household link at all -- needs attention, shown first.</summary>
    New,
    /// <summary>Linked to a Picnic product, ready for the basket.</summary>
    Linked,
    /// <summary>Deliberately not bought at Picnic.</summary>
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
    public string AmountReason => AmountReasonIn(AppText.Current);

    /// <summary>The explanation in a given language, for tests.</summary>
    public string AmountReasonIn(AppText text)
    {
        if (Amount == 1 && Quantities.ResolveUnit(Unit).Dimension is not Dimension.Count)
            return string.IsNullOrWhiteSpace(PicnicPack)
                ? text.AmountOnePackUnknownSize
                : text.AmountOnePack(PicnicPack);
        return string.IsNullOrWhiteSpace(PicnicPack)
            ? text.AmountCount(Amount)
            : text.AmountCountOfPack(Amount, PicnicPack);
    }
}

/// <summary>A Mealie shopping list, for the picker.</summary>
public sealed record ShoppingListSummary(string Id, string Name);

/// <summary>
/// Outcome for one line. Ok covers the Picnic side only: CheckedOff is separate so
/// "in the basket but Mealie did not tick it" is not reported as a failed add,
/// which would invite a retry and a double order.
/// </summary>
public sealed record CartResult(
    string Name,
    string Uid,
    int Amount,
    bool Ok,
    string? Error,
    bool CheckedOff = false);
