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

    // ---------------------------------------------------------------- allergens

    // EU labelling requires a declared allergen to be emphasised inside the
    // ingredient list, and Picnic's page markdown carries that emphasis. This is
    // the one signal on the page that is Picnic asserting an allergen rather than
    // us matching a word, so it is read BEFORE Clean() strips the markers.
    //
    // NOTE, and it belongs in the code rather than only in the issue: that Picnic
    // does emphasise allergens is inferred from the labelling rules, not observed
    // in a captured product page -- no fixture here contains an allergen section.
    // If the inference is wrong, nothing breaks and nothing lies: every match
    // simply falls through to the unemphasised path and is marked uncertain.
    private static readonly Regex Emphasised = new(@"(?:\*\*|__)(?<inner>.+?)(?:\*\*|__)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// One allergen group: the terms that name it, and the words that contain a
    /// term without carrying the allergen.
    /// </summary>
    private sealed record Rule(string Group, Regex Terms, Regex? Excluded);

    private static Regex Terms(string alternation, bool wholeWord) => new(
        wholeWord ? $@"(?<![\p{{L}}])(?:{alternation})(?![\p{{L}}])" : $"(?:{alternation})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Peanuts and tree nuts in one group, per the household's choice.
    private const string NutTerms =
        @"noot|noten|nootjes|amandel|amandelen|hazelnoot|hazelnoten|walnoot|walnoten|" +
        @"cashew|cashewnoot|cashewnoten|pecan|pecannoot|pistache|pistachenoot|macadamia|" +
        @"paranoot|paranoten|pinda|pinda's|pindas|pindakaas|notenmix";

    // The dairy names that do not contain "melk" are the point of the group:
    // without lactose, wei and caseine the chip misses most processed products.
    private const string MilkTerms =
        @"melk|melkpoeder|melkeiwit|melkvet|karnemelk|karnemelkpoeder|" +
        @"lactose|wei|weipoeder|wei-eiwit|caseïne|caseine|caseïnaat|caseinaat";

    // Words that contain a term but carry no allergen. Handled per group, and
    // deliberately not shared: "amandelmelk" must be kept away from the milk
    // group while still being able to raise the nut one.
    private const string NotNuts = @"kokosnoot|kokosnoten|nootmuskaat|muskaatnoot";

    private const string NotMilk =
        @"kokosmelk|amandelmelk|sojamelk|havermelk|rijstmelk|" +
        @"melkdistel|melkzuur|melkzuursel";

    private static readonly Rule[] Rules =
    [
        new(AllergenGroups.Nuts, Terms(NutTerms, wholeWord: true), Terms(NotNuts, wholeWord: false)),
        new(AllergenGroups.Milk, Terms(MilkTerms, wholeWord: true), Terms(NotMilk, wholeWord: false)),
    ];

    private static readonly Dictionary<string, Regex> Anywhere = new()
    {
        [AllergenGroups.Nuts] = Terms(NutTerms, wholeWord: false),
        [AllergenGroups.Milk] = Terms(MilkTerms, wholeWord: false),
    };

    /// <summary>
    /// Which allergen groups this product's ingredient text names (issue #14).
    ///
    /// Two strengths of evidence, and the difference is what the card shows:
    ///
    /// <list type="bullet">
    /// <item><b>Emphasised</b> -- the term appears inside a bold run. Matched
    /// anywhere in that run, not only as a whole word, because the emphasis is
    /// placed on the allergen inside a compound: "karne<b>melk</b>poeder" marks
    /// milk exactly as "<b>melk</b>" does.</item>
    /// <item><b>Plain text</b> -- the term appears as a whole word. The whole-word
    /// requirement is what keeps "kokosmelk" and "melkdistel" from raising the
    /// milk chip on text Picnic never marked, and it is why a plain-text substring
    /// inside a longer word yields nothing at all.</item>
    /// </list>
    ///
    /// Only ever called with the product's own accordion text -- the ingredient
    /// and allergy sections -- never marketing copy or the "similar products"
    /// block, whose names would otherwise put a neighbouring product's allergens
    /// on this one.
    ///
    /// A group that matches nothing is simply absent. That is not a claim that
    /// the product is free of it: the parse is best-effort text scraping and will
    /// miss, and the UI says so once rather than per card.
    /// </summary>
    public static IReadOnlyList<AllergenMark> ReadAllergens(IReadOnlyList<string> fragments)
    {
        var found = new List<AllergenMark>();

        foreach (var rule in Rules)
        {
            // The strongest evidence seen so far, and what backs it up (issues
            // #48 and #58). Kept from the first match at each strength: the detail
            // view wants to show why a chip is there, not every place the word
            // occurs. Declared beats suspected beats traces, so a product whose
            // list names an allergen AND carries a traces line reads as the
            // stronger of the two.
            AllergenEvidence? best = null;
            string? term = null;
            string? source = null;

            void Record(AllergenEvidence evidence, string matched, string text, int at)
            {
                if (best is { } current && current <= evidence) return;
                best = evidence;
                term = matched;
                source = Snippet(text, at, matched.Length);
            }

            foreach (var raw in fragments)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;

                // Drop this group's false friends first, then look at what is
                // left -- the same shape as the organic claim's NotAClaim, and
                // for the same reason: one sentence can hold both.
                var text = rule.Excluded is null ? raw : rule.Excluded.Replace(raw, " ");
                var cleaned = Clean(text);
                var traces = TraceSpans(cleaned);

                foreach (Match emphasis in Emphasised.Matches(text))
                {
                    var inner = emphasis.Groups["inner"].Value;
                    var hit = Anywhere[rule.Group].Match(inner);
                    if (!hit.Success) continue;

                    // Located in the cleaned text so the offset lines up with the
                    // snippet and the trace spans; the emphasis markers are gone
                    // by then, so the raw index would be wrong.
                    var matched = Clean(hit.Value);
                    var at = cleaned.IndexOf(matched, StringComparison.OrdinalIgnoreCase);
                    Record(InTraces(traces, at) ? AllergenEvidence.Traces : AllergenEvidence.Declared,
                        matched, cleaned, at);
                    break;
                }

                if (best == AllergenEvidence.Declared) break;

                foreach (Match loose in rule.Terms.Matches(cleaned))
                {
                    Record(InTraces(traces, loose.Index) ? AllergenEvidence.Traces : AllergenEvidence.Suspected,
                        loose.Value, cleaned, loose.Index);
                    // A suspected match outside any traces line is the best this
                    // pass can do; keep looking only while everything so far has
                    // been a traces mention.
                    if (best == AllergenEvidence.Suspected) break;
                }
            }

            if (best is { } evidence)
                found.Add(new AllergenMark(rule.Group, evidence, term, source));
        }

        return found;
    }

    /// <summary>
    /// Precautionary cross-contamination statements: "kan sporen van noten
    /// bevatten", "bevat mogelijk sporen van pinda", and the English forms
    /// (issue #58).
    ///
    /// Anchored on the word "sporen"/"traces" rather than on sentence structure,
    /// because these lines are not reliably sentences: the real text that
    /// prompted this reads "...conserveermiddel: kaliumsorbaat Kan sporen van
    /// NOTEN en PINDA'S bevatten." with no full stop before "Kan". The span runs
    /// from the introducing verb to the end of that sentence, or to the end of
    /// the text where there is no full stop at all.
    /// </summary>
    private static readonly Regex Precautionary = new(
        @"\b(?:kan|kunnen|bevat|bevatten|may)\b[^.]{0,40}?\b(?:sporen|traces)\b[^.]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static List<(int Start, int End)> TraceSpans(string text) =>
        [.. Precautionary.Matches(text).Select(m => (m.Index, m.Index + m.Length))];

    private static bool InTraces(List<(int Start, int End)> spans, int at) =>
        at >= 0 && spans.Any(s => at >= s.Start && at < s.End);

    /// <summary>
    /// An ingredient list can run to several hundred characters, and the detail
    /// view is quoting it as evidence rather than reproducing the label.
    ///
    /// The window is centred on the match (issue #58). It used to be the first
    /// 240 characters of the fragment, which on a long list meant the quoted
    /// evidence reliably excluded the very word it was evidence for -- the
    /// panel showed "Gevonden in: NOTEN" above a passage with no "noten" in it.
    /// Either end that had to be cut says so, so nobody reads a fragment as a
    /// complete ingredient list.
    /// </summary>
    private static string Snippet(string text, int at, int length)
    {
        const int window = 240;
        text = text.Trim();
        if (text.Length <= window) return text;
        if (at < 0) at = 0;

        // Centre on the match, then pull back inside the string if that ran off
        // either end -- a term at the very start or very end still gets a full
        // window rather than half of one.
        var start = Math.Max(0, at + length / 2 - window / 2);
        start = Math.Min(start, text.Length - window);
        var end = start + window;

        // Prefer a clause boundary, but only when one is near enough that the
        // window does not lose the match or most of its context.
        var boundary = text.LastIndexOfAny(['.', ';', ','], start, Math.Min(start, 40));
        if (boundary > 0 && boundary < at) start = boundary + 1;

        var cut = text[start..Math.Min(end, text.Length)].Trim();
        return (start > 0 ? "…" : "") + cut + (end < text.Length ? "…" : "");
    }

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
