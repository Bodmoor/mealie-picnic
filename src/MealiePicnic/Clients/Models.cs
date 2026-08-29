namespace MealiePicnic.Clients;

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

/// <summary>The allergen groups this app reads (issue #14).</summary>
public static class AllergenGroups
{
    /// <summary>
    /// All tree nuts and peanuts together. EU labelling law lists them as two
    /// separate allergens, and one chip covering both is a deliberate choice for
    /// this app rather than an oversight: it is what the household asked for.
    /// </summary>
    public const string Nuts = "nuts";

    /// <summary>Milk and the dairy names that do not contain the word.</summary>
    public const string Milk = "milk";
}

/// <summary>
/// One allergen group found on a product page (issue #14).
///
/// <see cref="Declared"/> is the difference between Picnic saying so and us
/// noticing: true when the term was emphasised in the ingredient list, which EU
/// labelling requires of a declared allergen, and false when our own word list
/// matched ordinary text. The card shows the first with a tick and the second
/// with a question mark, because they are not the same claim.
/// </summary>
public sealed record AllergenMark(string Group, bool Declared);

/// <summary>
/// The facts Picnic only publishes on the product page: whether the product is
/// organic and its salt content per 100 g (issue #6), and which allergens its
/// ingredient list names (issue #14).
///
/// All of them are optional by design. <see cref="Organic"/> false means "no
/// organic claim found", not "conventional"; a null <see cref="SaltGramsPer100"/>
/// means the nutrition table could not be read; and an empty
/// <see cref="Allergens"/> means nothing matched, which is emphatically not the
/// same as "contains none of them". None is worth showing as a negative, so the
/// UI omits what it does not know and never renders a "free from" claim.
/// </summary>
public sealed record PicnicDetails(
    string Id,
    bool Organic,
    double? SaltGramsPer100,
    string? SaltText,
    IReadOnlyList<AllergenMark> Allergens);

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
