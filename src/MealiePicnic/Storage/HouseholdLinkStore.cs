using MealiePicnic.Clients;
using System.Text.Json;

namespace MealiePicnic.Storage;

/// <summary>
/// Per-household Picnic product links (issue #17). Mealie food extras used to
/// carry this link, but foods are scoped to the whole Mealie group, so every
/// household saw the same one -- linking "melk" in one household linked it for
/// all of them. This stores the mapping in the app's own storage instead, one
/// JSON file per household, following the same flat-file + atomic-write
/// pattern <see cref="TokenStore"/> already uses for per-user Picnic tokens.
/// </summary>
public sealed class HouseholdLinkStore
{
    private readonly string _dataDir;
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, FoodLink>> _cache = new();

    public HouseholdLinkStore(AppOptions options)
    {
        _dataDir = options.DataDir;
        Directory.CreateDirectory(_dataDir);
    }

    /// <summary>
    /// Apply a household's links onto items read from Mealie, classifying each
    /// as New/Linked/Excluded. Same ordering as before the household split:
    /// New first (those need a decision), then Linked, then Excluded, and
    /// alphabetically within each group.
    /// </summary>
    public List<ShoppingItem> Merge(string householdKey, IEnumerable<ShoppingItem> items)
    {
        Dictionary<string, FoodLink> links;
        lock (_lock) links = Get(householdKey);

        var merged = items.Select(item => links.TryGetValue(item.FoodId, out var link)
            ? link.Excluded
                ? item with { State = LinkState.Excluded }
                : item with { State = LinkState.Linked, PicnicUid = link.PicnicUid, PicnicLabel = link.Label, PicnicPack = link.Pack }
            : item with { State = LinkState.New });

        return merged
            .OrderBy(i => i.State switch { LinkState.New => 0, LinkState.Linked => 1, _ => 2 })
            .ThenBy(i => i.FoodName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Link(string householdKey, string foodId, string picnicUid, string? label, string? pack)
    {
        lock (_lock)
        {
            var links = Get(householdKey);
            links[foodId] = new FoodLink(picnicUid, label, pack, Excluded: false);
            Save(householdKey, links);
        }
    }

    public void Exclude(string householdKey, string foodId)
    {
        lock (_lock)
        {
            var links = Get(householdKey);
            links[foodId] = new FoodLink(null, null, null, Excluded: true);
            Save(householdKey, links);
        }
    }

    /// <summary>Revert an exclusion (or a link): the food goes back to 'New'.</summary>
    public void Clear(string householdKey, string foodId)
    {
        lock (_lock)
        {
            var links = Get(householdKey);
            if (links.Remove(foodId))
                Save(householdKey, links);
        }
    }

    private Dictionary<string, FoodLink> Get(string householdKey)
    {
        if (_cache.TryGetValue(householdKey, out var cached))
            return cached;

        var links = new Dictionary<string, FoodLink>();
        var path = FilePath(householdKey);
        if (File.Exists(path))
        {
            var json = File.ReadAllText(path).Trim();
            // A crash mid-write can leave a truncated file; treat it as an empty
            // household rather than throwing on malformed JSON.
            if (json.Length > 0)
            {
                try { links = JsonSerializer.Deserialize<Dictionary<string, FoodLink>>(json) ?? []; }
                catch (JsonException) { links = []; }
            }
        }

        _cache[householdKey] = links;
        return links;
    }

    private void Save(string householdKey, Dictionary<string, FoodLink> links)
    {
        var dir = Path.Combine(_dataDir, "households", householdKey);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "links.json");
        var tmp = path + ".tmp";

        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            JsonSerializer.Serialize(stream, links);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    private string FilePath(string householdKey) =>
        Path.Combine(_dataDir, "households", householdKey, "links.json");
}

/// <summary>One household's Picnic link for a single Mealie food.</summary>
public sealed record FoodLink(string? PicnicUid, string? Label, string? Pack, bool Excluded);
