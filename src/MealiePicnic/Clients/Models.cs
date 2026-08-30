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
/// <param name="Term">
/// The word that actually matched, so the detail view can say why a chip is
/// there (issue #48). Null on marks built before the evidence existed.
/// </param>
/// <param name="Source">
/// The ingredient-list sentence the term was found in, cleaned and trimmed. This
/// is the "source of the claim" the detail view shows, and the text a
/// confirm/deny verdict is recorded against -- Picnic reformulates products, and
/// a verdict made against a different ingredient list is worth spotting.
/// </param>
public sealed record AllergenMark(string Group, bool Declared, string? Term = null, string? Source = null);

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
/// <remarks>
/// Everything from <see cref="Title"/> down is what the product page already
/// gave us and this record used to discard (issue #48): the parser read the
/// main container, the highlights, the description and every accordion section,
/// then kept only the two derived facts. The detail view needs the text itself,
/// and carrying it costs no extra request -- the page is fetched and cached once
/// either way.
/// </remarks>
public sealed record PicnicDetails(
    string Id,
    bool Organic,
    double? SaltGramsPer100,
    string? SaltText,
    IReadOnlyList<AllergenMark> Allergens,
    string? Title = null,
    IReadOnlyList<string>? Highlights = null,
    IReadOnlyList<string>? Description = null,
    IReadOnlyList<DetailSection>? Sections = null);

/// <summary>One accordion section of a Picnic product page: ingredients,
/// nutrition, storage advice, and whatever else the product carries.</summary>
public sealed record DetailSection(string Title, IReadOnlyList<string> Body)
{
    /// <summary>Whether this is the section allergens are read from -- the detail
    /// view marks it, since it is the one the chips can be checked against.</summary>
    public bool Ingredients { get; init; }
}

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
