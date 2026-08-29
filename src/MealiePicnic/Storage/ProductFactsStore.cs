using System.Text.Json;
using System.Text.Json.Serialization;
using MealiePicnic.Clients;

namespace MealiePicnic.Storage;

/// <summary>
/// Keeps the product facts -- organic claim, salt per 100 g, allergens -- on the
/// mounted volume so they survive a restart.
///
/// The reason is the cost of rebuilding them, and it is not the parsing.
/// <see cref="ProductFacts"/> runs a handful of compiled regexes over a few
/// kilobytes; what costs is the upstream page fetch, one per product, and a
/// search grid can hold ninety cards. Before this the whole set was in-process
/// only, so every deploy -- and this project deploys often -- threw away every
/// page that had been read and paid for all of them again.
///
/// Two things make a long expiry safe to persist rather than merely long:
///
/// <list type="bullet">
/// <item>Every record carries the time it was fetched, and expired records are
/// dropped when the file is loaded. A restart can never resurrect a fact past
/// its TTL, which is what a naive "write it down forever" would do.</item>
/// <item>The file carries a schema version and is discarded wholesale on a
/// mismatch. #14 added a field to <see cref="PicnicDetails"/> and #45 changed
/// what the allergen parse means; a record written under the old rules must not
/// be read back under the new ones.</item>
/// </list>
///
/// It is a cache, not a database. Anything that goes wrong -- unreadable file,
/// unwritable volume, malformed JSON -- degrades to "no cached facts" and is
/// logged, never thrown: a product card losing its salt figure must not take the
/// search grid with it.
/// </summary>
public sealed class ProductFactsStore
{
    /// <summary>
    /// Bump when the shape or meaning of a stored record changes, including when
    /// the parse that produced it changes. Old files are then discarded rather
    /// than reinterpreted.
    /// </summary>
    private const int Schema = 1;

    /// <summary>
    /// Enough for a very large household's browsing history at roughly a hundred
    /// bytes a record, and small enough that the file stays trivial to write.
    /// </summary>
    private const int MaxEntries = 5000;

    /// <summary>
    /// Writing on every save would mean ninety writes while one grid scrolls.
    /// The in-memory dictionary is authoritative; the file catches up at most
    /// this often. Losing the last few seconds of a cache to an unclean kill is
    /// not worth the I/O to prevent.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    private readonly string _path;
    private readonly ILogger<ProductFactsStore> _log;
    private readonly TimeSpan _ttl;
    private readonly object _lock = new();
    private readonly Dictionary<string, Record> _entries = new(StringComparer.Ordinal);

    private bool _loaded;
    private bool _dirty;

    // Starts at construction, not at the epoch. Otherwise the very first Put of
    // a process always writes, which is exactly the burst worth batching: a cold
    // start loading one grid of ninety cards.
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;

    public ProductFactsStore(AppOptions options, ILogger<ProductFactsStore> log)
    {
        _log = log;
        _ttl = TimeSpan.FromHours(options.ProductFactsTtlHours);
        Directory.CreateDirectory(options.DataDir);
        _path = Path.Combine(options.DataDir, "product-facts.json");
    }

    /// <summary>The facts for a product, if they are stored and still fresh.</summary>
    public PicnicDetails? Get(string productId)
    {
        lock (_lock)
        {
            Load();
            if (!_entries.TryGetValue(productId, out var record))
                return null;

            // Checked on read as well as on load: the process can outlive the TTL
            // without ever restarting.
            if (DateTimeOffset.UtcNow - record.Fetched > _ttl)
            {
                _entries.Remove(productId);
                _dirty = true;
                return null;
            }

            return record.ToDetails(productId);
        }
    }

    public void Put(PicnicDetails details)
    {
        lock (_lock)
        {
            Load();
            _entries[details.Id] = Record.From(details, DateTimeOffset.UtcNow);
            _dirty = true;

            if (_entries.Count > MaxEntries) EvictOldest();
            if (DateTimeOffset.UtcNow - _lastFlush >= FlushInterval) Flush();
        }
    }

    /// <summary>Write pending changes now. Public so tests need not wait out the interval.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            _lastFlush = DateTimeOffset.UtcNow;
            _dirty = false;

            try
            {
                var file = new FileShape(Schema, _entries);
                WriteAtomic(_path, JsonSerializer.Serialize(file));
            }
            catch (Exception ex)
            {
                // A read-only or full volume must not break shopping.
                _log.LogWarning(ex, "Could not persist product facts to {Path}", _path);
            }
        }
    }

    private void EvictOldest()
    {
        foreach (var key in _entries.OrderBy(e => e.Value.Fetched)
                                    .Take(_entries.Count - MaxEntries)
                                    .Select(e => e.Key)
                                    .ToList())
            _entries.Remove(key);
    }

    private void Load()
    {
        if (_loaded) return;
        _loaded = true;

        if (!File.Exists(_path)) return;

        try
        {
            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path));
            if (file is null) return;

            if (file.Schema != Schema)
            {
                _log.LogInformation(
                    "Discarding product-facts cache written under schema {Old}; this build reads {Current}",
                    file.Schema, Schema);
                return;
            }

            var cutoff = DateTimeOffset.UtcNow - _ttl;
            var expired = 0;
            foreach (var (id, record) in file.Entries ?? [])
            {
                if (record.Fetched < cutoff) { expired++; continue; }
                _entries[id] = record;
            }

            // Anything dropped means the file on disk no longer matches memory.
            if (expired > 0) _dirty = true;
            _log.LogInformation("Loaded {Kept} cached product facts ({Expired} expired)",
                _entries.Count, expired);
        }
        catch (Exception ex)
        {
            // Truncated or hand-edited file: start empty rather than fail startup.
            _log.LogWarning(ex, "Could not read product facts from {Path}; starting empty", _path);
            _entries.Clear();
        }
    }

    /// <summary>Same temp-and-rename as TokenStore: a crash mid-write cannot leave a truncated file.</summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    // The stored shape is deliberately its own type rather than PicnicDetails.
    // The wire format is a compatibility surface: changing the domain record
    // should force a decision here (and a schema bump), not silently change what
    // older files mean.
    private sealed record FileShape(
        [property: JsonPropertyName("schema")] int Schema,
        [property: JsonPropertyName("entries")] Dictionary<string, Record>? Entries);

    private sealed record Record(
        [property: JsonPropertyName("fetched")] DateTimeOffset Fetched,
        [property: JsonPropertyName("organic")] bool Organic,
        [property: JsonPropertyName("salt")] double? SaltGramsPer100,
        [property: JsonPropertyName("saltText")] string? SaltText,
        [property: JsonPropertyName("allergens")] List<StoredAllergen>? Allergens)
    {
        public static Record From(PicnicDetails details, DateTimeOffset fetched) => new(
            fetched,
            details.Organic,
            details.SaltGramsPer100,
            details.SaltText,
            [.. details.Allergens.Select(a => new StoredAllergen(a.Group, a.Declared))]);

        public PicnicDetails ToDetails(string id) => new(
            id,
            Organic,
            SaltGramsPer100,
            SaltText,
            [.. (Allergens ?? []).Select(a => new AllergenMark(a.Group, a.Declared))]);
    }

    private sealed record StoredAllergen(
        [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("declared")] bool Declared);
}
