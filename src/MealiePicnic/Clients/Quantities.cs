using System.Globalization;
using System.Text.RegularExpressions;

namespace MealiePicnic.Clients;

public enum Dimension
{
    /// <summary>Countable things: stuks, or no unit at all.</summary>
    Count,
    Mass,
    Volume,
    /// <summary>Recognised as a unit but not convertible (snufje, portie, teen).</summary>
    Unknown,
}

/// <summary>An amount reduced to a base unit: grams, millilitres, or pieces.</summary>
public readonly record struct Measure(Dimension Dimension, double Value);

/// <summary>
/// Works out how many of a Picnic product to add for a Mealie line.
///
/// The bug this exists to prevent: "500 gram bloem" was read as quantity 500 and
/// 500 packs were ordered. Quantity only means "how many to buy" when the unit is
/// countable; for mass and volume it describes how much of ONE product is needed.
/// </summary>
public static class Quantities
{
    /// <summary>
    /// Never order more than this of a single line, whatever the arithmetic says.
    /// A mis-parsed unit should waste one slot in the basket, not fifty.
    /// </summary>
    public const int MaxPerItem = 12;

    // Mealie units are free text, in Dutch or English, singular or plural, and
    // sometimes abbreviated. Everything is folded to grams / millilitres / pieces.
    private static readonly Dictionary<string, Measure> Units = new(StringComparer.OrdinalIgnoreCase)
    {
        // countable
        ["stuk"] = new(Dimension.Count, 1),
        ["stuks"] = new(Dimension.Count, 1),
        ["st"] = new(Dimension.Count, 1),
        ["stk"] = new(Dimension.Count, 1),
        ["piece"] = new(Dimension.Count, 1),
        ["pieces"] = new(Dimension.Count, 1),
        ["x"] = new(Dimension.Count, 1),

        // mass, in grams
        ["mg"] = new(Dimension.Mass, 0.001),
        ["g"] = new(Dimension.Mass, 1),
        ["gr"] = new(Dimension.Mass, 1),
        ["gram"] = new(Dimension.Mass, 1),
        ["grammen"] = new(Dimension.Mass, 1),
        ["ons"] = new(Dimension.Mass, 100),
        ["hg"] = new(Dimension.Mass, 100),
        ["pond"] = new(Dimension.Mass, 500),
        ["kg"] = new(Dimension.Mass, 1000),
        ["kilo"] = new(Dimension.Mass, 1000),
        ["kilogram"] = new(Dimension.Mass, 1000),

        // volume, in millilitres
        ["ml"] = new(Dimension.Volume, 1),
        ["milliliter"] = new(Dimension.Volume, 1),
        ["cl"] = new(Dimension.Volume, 10),
        ["dl"] = new(Dimension.Volume, 100),
        ["l"] = new(Dimension.Volume, 1000),
        ["ltr"] = new(Dimension.Volume, 1000),
        ["liter"] = new(Dimension.Volume, 1000),
        ["litre"] = new(Dimension.Volume, 1000),
        // spoons are volumes, and small enough that one pack always covers them
        ["tl"] = new(Dimension.Volume, 5),
        ["theelepel"] = new(Dimension.Volume, 5),
        ["tsp"] = new(Dimension.Volume, 5),
        ["el"] = new(Dimension.Volume, 15),
        ["eetlepel"] = new(Dimension.Volume, 15),
        ["tbsp"] = new(Dimension.Volume, 15),
    };

    /// <summary>
    /// Resolve a Mealie unit name. An empty unit is Count: "2 wraps" has no unit
    /// in Mealie and does mean two of them.
    /// </summary>
    public static Measure ResolveUnit(string? unit)
    {
        if (string.IsNullOrWhiteSpace(unit))
            return new Measure(Dimension.Count, 1);

        var key = unit.Trim().TrimEnd('.');
        if (Units.TryGetValue(key, out var known))
            return known;

        // Try the singular of a plural we do not have listed.
        if (key.EndsWith('s') && Units.TryGetValue(key[..^1], out var singular))
            return singular;

        return new Measure(Dimension.Unknown, 1);
    }

    // "500 gram", "1 kg", "6 stuks", "3 x 60 gram", "±500 g", "2-3 porties", "1,5 l"
    private static readonly Regex MultiPack = new(
        @"(?<count>\d+(?:[.,]\d+)?)\s*[x×]\s*(?<size>\d+(?:[.,]\d+)?)\s*(?<unit>[\p{L}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SinglePack = new(
        @"(?<size>\d+(?:[.,]\d+)?)\s*(?<unit>[\p{L}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse Picnic's unit_quantity into a total. Returns null when it cannot be
    /// understood ("2-3 porties", ""), which the caller treats as "assume one pack
    /// is enough" rather than guessing.
    /// </summary>
    public static Measure? ParsePack(string? unitQuantity)
    {
        if (string.IsNullOrWhiteSpace(unitQuantity))
            return null;

        var text = unitQuantity.Replace("±", " ").Trim();

        // A multipack is the total: "3 x 60 gram" is 180 gram.
        var match = MultiPack.Match(text);
        if (match.Success)
        {
            var count = Number(match.Groups["count"].Value);
            var size = Number(match.Groups["size"].Value);
            var unit = ResolveUnit(match.Groups["unit"].Value);
            return unit.Dimension is Dimension.Unknown
                ? null
                : new Measure(unit.Dimension, count * size * unit.Value);
        }

        match = SinglePack.Match(text);
        if (!match.Success)
            return null;

        var packUnit = ResolveUnit(match.Groups["unit"].Value);
        return packUnit.Dimension is Dimension.Unknown
            ? null
            : new Measure(packUnit.Dimension, Number(match.Groups["size"].Value) * packUnit.Value);
    }

    private static double Number(string raw) =>
        double.Parse(raw.Replace(',', '.'), CultureInfo.InvariantCulture);

    /// <summary>
    /// How many of the linked product to add.
    ///
    /// <para>Countable line ("3 stuks", or no unit): buy that many, unless the pack
    /// already contains enough (3 wraps out of a pack of 8 is one pack).</para>
    ///
    /// <para>Mass or volume line ("500 gram"): buy one, unless the pack size is known
    /// and smaller than needed -- 1 kg of flour against 500 g packs is two.</para>
    /// </summary>
    public static int Required(double quantity, string? mealieUnit, string? picnicPack)
    {
        if (quantity <= 0)
            return 1;

        var unit = ResolveUnit(mealieUnit);
        var pack = ParsePack(picnicPack);

        // Unit we do not understand: one is the safe answer.
        if (unit.Dimension is Dimension.Unknown)
            return 1;

        var needed = quantity * unit.Value;

        if (pack is { } p)
        {
            // Known pack, wrong dimension: no honest conversion (issue #10 --
            // "5 uien" against a "1 kilo" bag is not 5 bags). One is the safe
            // answer, whichever direction the mismatch runs.
            if (p.Dimension != unit.Dimension || p.Value <= 0)
                return 1;

            return Clamp(Math.Ceiling(needed / p.Value));
        }

        // No pack size known at all. Countable lines still mean what they say;
        // a weight or volume without a pack size tells us nothing about count.
        return unit.Dimension is Dimension.Count ? Clamp(Math.Ceiling(needed)) : 1;
    }

    private static int Clamp(double value) =>
        (int)Math.Clamp(value, 1, MaxPerItem);
}
