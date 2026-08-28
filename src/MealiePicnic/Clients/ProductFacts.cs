using System.Globalization;
using System.Text.RegularExpressions;

namespace MealiePicnic.Clients;

/// <summary>
/// Issue #6: show whether a product is organic, and how much salt it contains, so
/// the pick is an informed one.
///
/// Picnic's search results carry neither fact, and its product page exposes both
/// only as human-readable text: there is no "organic" boolean and the nutrition
/// table arrives as markdown. So both are recovered by reading text, and both are
/// therefore best-effort. The rule everywhere below: when unsure, say nothing.
/// A missing leaf is a small annoyance; a leaf on a non-organic product is a lie.
/// </summary>
public static class ProductFacts
{
    // Picnic wraps text in its own colour markup (#(#B40117)) and markdown bold.
    private static readonly Regex ColourMarkup = new(@"#\([A-Za-z0-9#_]+\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Strip Picnic colour markup, markdown emphasis and odd whitespace.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var s = ColourMarkup.Replace(text, "");
        s = s.Replace("**", "").Replace("__", "").Replace('\u00A0', ' ');
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    // ------------------------------------------------------------------ organic

    // "biologisch", "biologische", "bio", "organic", and the EU control-body code
    // that appears on certified packaging ("NL-BIO-01").
    private static readonly Regex OrganicClaim = new(
        @"(\bbiologische?\b|\bbio\b|\borganic\b|\b[A-Z]{2}-BIO-\d{2}\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The trap: household products are "biologisch afbreekbaar" (biodegradable),
    // which says nothing about organic farming. Also "bio-industrie" is the exact
    // opposite of a certification claim.
    private static readonly Regex NotAClaim = new(
        @"\bbiologisch(e)?\s+afbreekba|\bbio-?industrie\b|\bbiologisch(e)?\s+kwaliteit\s+niet\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Does any of this product's own text claim it is organic?
    ///
    /// Only ever called with text belonging to the product itself -- never the
    /// "similar products" block, whose names would otherwise leak an organic
    /// alternative's label onto a conventional product.
    /// </summary>
    public static bool IsOrganic(IEnumerable<string?> texts)
    {
        foreach (var raw in texts)
        {
            var text = Clean(raw);
            if (text.Length == 0) continue;

            // Remove the disqualifying phrases first, then look for a claim in
            // what is left: one sentence can contain both.
            var candidate = NotAClaim.Replace(text, " ");
            if (OrganicClaim.IsMatch(candidate)) return true;
        }
        return false;
    }

    // --------------------------------------------------------------------- salt

    /// <summary>Salt in grams per 100 g/ml, with the text it was read from.</summary>
    public readonly record struct Salt(double Grams, string Text);

    // "1,2 g", "0.35 gram", "480 mg". The unit is required: a bare number in a
    // nutrition table is as likely to be kJ as grams.
    private static readonly Regex Amount = new(
        @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>mg|gram|g)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SaltLabel = new(@"(?<![a-z])zout(?![a-z])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex SodiumLabel = new(@"\bnatrium\b|\bsodium\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Pull the salt figure out of a nutrition table that has been flattened to a
    /// list of text fragments.
    ///
    /// Picnic renders the table as markdown, and how it splits is not ours to
    /// control: the label and its value can share a fragment ("Zout | 1,2 g") or
    /// sit in adjacent ones ("Zout", then "1,2 g"). Both are handled, in document
    /// order, and the first hit wins -- tables list per-100 g before per-portion.
    ///
    /// Falls back to sodium x 2.5 (the standard conversion) when only natrium is
    /// listed, which some labels still do.
    /// </summary>
    public static Salt? ParseSalt(IReadOnlyList<string> fragments)
    {
        var lines = fragments.Select(Clean).Where(s => s.Length > 0).ToList();

        // Salt first, across the whole table, before falling back to sodium:
        // a table listing both must not be read as 2.5x its actual salt.
        return Find(lines, SaltLabel, 1.0) ?? Find(lines, SodiumLabel, 2.5);
    }

    private static Salt? Find(List<string> lines, Regex label, double factor)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!label.IsMatch(lines[i])) continue;

            // Value on the same line, but only after the label: "Zout 1,2 g".
            var tail = lines[i][label.Match(lines[i]).Index..];
            if (Read(tail) is { } here) return Scale(here, factor);

            // Otherwise the next fragment, which is the cell to its right.
            if (i + 1 < lines.Count && Read(lines[i + 1]) is { } next) return Scale(next, factor);
        }
        return null;
    }

    private static Salt Scale(Salt salt, double factor) =>
        factor == 1.0 ? salt : new Salt(salt.Grams * factor, salt.Text);

    // "Voedingswaarde per 100 g" sits inside these tables, and "100 g" matches the
    // amount pattern perfectly. Drop the basis phrase before reading a value.
    private static readonly Regex Basis = new(@"per\s*\d+\s*(ml|gram|g)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static Salt? Read(string text)
    {
        var match = Amount.Match(Basis.Replace(text, " "));
        if (!match.Success) return null;

        var value = double.Parse(match.Groups["value"].Value.Replace(',', '.'),
            CultureInfo.InvariantCulture);
        var grams = match.Groups["unit"].Value.Equals("mg", StringComparison.OrdinalIgnoreCase)
            ? value / 1000
            : value;

        // Nutrition is declared per 100 g, so a large number is a misread cell (a
        // portion size, a package weight) rather than salt. Table salt itself is
        // ~100 g/100 g, hence the cap sits above the edible range but far below
        // the weights and energies that share the table.
        return grams is >= 0 and <= 40 ? new Salt(grams, match.Value) : null;
    }
}
