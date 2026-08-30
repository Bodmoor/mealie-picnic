using System.Text.Json;

namespace MealiePicnic.Storage;

/// <summary>
/// Human verdicts on suspected allergen marks (issue #48).
///
/// Deliberately **global**, not per household: "does this product contain nuts"
/// is a fact about the product, and the answer does not change because a
/// different household is asking. That makes this the first store here that is
/// not scoped by household or user -- links live under
/// <c>households/{key}/links.json</c> and Picnic tokens under
/// <c>users/{key}/</c>, both for reasons that simply do not apply to a statement
/// about a supermarket product.
///
/// Whose warning it becomes is a separate question, and belongs to the household
/// allergy profile (issue #40). This store only records what was checked.
///
/// One file, read once and held in memory: there are at most a few hundred
/// verdicts, and every write is atomic (temp file + rename), the same discipline
/// <see cref="TokenStore"/> and <see cref="HouseholdLinkStore"/> use.
///
/// Deliberately no entry cap, unlike <see cref="ProductFactsStore"/> next door,
/// which trims itself at 5000. That store fills on its own as a side effect of
/// browsing; every row here is a person deciding something and pressing a button,
/// so it grows at human speed and cannot run away. The asymmetry is the point,
/// not an oversight.
/// </summary>
public sealed class AllergenVerdictStore
{
    private readonly string _path;
    private readonly object _lock = new();
    private Dictionary<string, Dictionary<string, AllergenVerdict>>? _all;

    public AllergenVerdictStore(AppOptions options)
    {
        Directory.CreateDirectory(options.DataDir);
        _path = Path.Combine(options.DataDir, "allergen-verdicts.json");
    }

    /// <summary>Every verdict recorded for one product, keyed by allergen group.</summary>
    public IReadOnlyDictionary<string, AllergenVerdict> ForProduct(string productId)
    {
        lock (_lock)
            return All().TryGetValue(productId, out var verdicts)
                ? new Dictionary<string, AllergenVerdict>(verdicts)
                : new Dictionary<string, AllergenVerdict>();
    }

    /// <param name="against">
    /// The ingredient text the verdict was made against. Stored so a later reader
    /// can tell that a product has been reformulated since -- a denial made
    /// against last year's recipe should not silently outlive it.
    /// </param>
    public void Set(string productId, string group, VerdictState state, string? by, string? against)
    {
        lock (_lock)
        {
            var all = All();
            if (!all.TryGetValue(productId, out var verdicts))
                all[productId] = verdicts = [];

            verdicts[group] = new AllergenVerdict(state, by, DateTimeOffset.UtcNow, against);
            Save(all);
        }
    }

    /// <summary>Withdraw a verdict, returning the mark to unreviewed.</summary>
    public void Clear(string productId, string group)
    {
        lock (_lock)
        {
            var all = All();
            if (!all.TryGetValue(productId, out var verdicts) || !verdicts.Remove(group))
                return;

            if (verdicts.Count == 0) all.Remove(productId);
            Save(all);
        }
    }

    private Dictionary<string, Dictionary<string, AllergenVerdict>> All()
    {
        if (_all is not null) return _all;

        _all = [];
        if (File.Exists(_path))
        {
            var json = File.ReadAllText(_path).Trim();
            // A crash mid-write can leave a truncated file. An unreadable verdict
            // file must not take the whole product view down with it: start empty
            // and let the marks show as unreviewed, which is the honest state.
            if (json.Length > 0)
            {
                try
                {
                    _all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AllergenVerdict>>>(json)
                           ?? [];
                }
                catch (JsonException) { _all = []; }
            }
        }

        return _all;
    }

    private void Save(Dictionary<string, Dictionary<string, AllergenVerdict>> all)
    {
        var tmp = _path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(all));
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>What a person decided about a suspected allergen mark.</summary>
public enum VerdictState
{
    /// <summary>Checked, and the allergen really is in the product.</summary>
    Confirmed,
    /// <summary>Checked, and our word list was wrong about this product.</summary>
    Denied,
}

/// <param name="By">Who recorded it, for the detail view. Null for the shared
/// password identity, which has no email to show.</param>
public sealed record AllergenVerdict(
    VerdictState State,
    string? By,
    DateTimeOffset At,
    string? Against);
