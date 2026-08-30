using System.Text.Json;
using MealiePicnic.Clients;
using MealiePicnic.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealiePicnic.Tests;

/// <summary>
/// The point of this store is that a deploy stops costing ninety product-page
/// fetches. The tests that matter are therefore the ones about what survives a
/// restart and, just as importantly, what must not: an expired fact, and a
/// record written under a parse this build no longer agrees with.
/// </summary>
public class ProductFactsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private ProductFactsStore Store(int ttlHours = 168) =>
        new(TestFactory.Options(_dir, productFactsTtlHours: ttlHours),
            NullLogger<ProductFactsStore>.Instance);

    private static PicnicDetails Details(string id = "s1002202", bool organic = true) =>
        new(id, organic, 0.11, "0,11 g", [new AllergenMark(AllergenGroups.Milk, AllergenEvidence.Declared)]);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Facts_survive_into_a_new_store_over_the_same_directory()
    {
        // "A new store over the same DATA_DIR" is a restart, which is the whole
        // reason this class exists.
        var first = Store();
        first.Put(Details());
        first.Flush();

        var restarted = Store().Get("s1002202");

        Assert.NotNull(restarted);
        Assert.True(restarted.Organic);
        Assert.Equal(0.11, restarted.SaltGramsPer100!.Value, 3);
        Assert.Equal("0,11 g", restarted.SaltText);
        var allergen = Assert.Single(restarted.Allergens);
        Assert.Equal(AllergenGroups.Milk, allergen.Group);
        Assert.True(allergen.Declared);
    }

    [Fact]
    public void A_graceful_shutdown_writes_what_is_still_in_memory()
    {
        // Issue #64. The store only writes every 30 seconds from inside Put, so
        // without a shutdown hook a `docker compose down` discards whatever was
        // parsed since the last write. This asserts the wiring, not Flush itself:
        // the test above already proves Flush works, and proved nothing about
        // whether production ever calls it.
        var dir = Path.Combine(_dir, "hosted");
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            builder.UseSetting("APP_PASSWORD", "correct-horse-battery-staple");
            builder.UseSetting("DATA_DIR", dir);
        });

        // Resolving from the host is what starts it, so the lifetime exists.
        factory.Services.GetRequiredService<ProductFactsStore>().Put(Details("s3000000"));
        factory.Dispose();   // stops the host, which runs ApplicationStopping

        var restarted = new ProductFactsStore(TestFactory.Options(dir),
            NullLogger<ProductFactsStore>.Instance);

        Assert.NotNull(restarted.Get("s3000000"));
    }

    [Fact]
    public void The_strength_of_an_allergen_mark_survives_a_restart()
    {
        // Issue #58. Storing this as declared/not-declared would have restored a
        // factory warning as an ingredient claim, which is the bug itself.
        var first = Store();
        first.Put(new PicnicDetails("s1002202", false, null, null,
            [new AllergenMark(AllergenGroups.Nuts, AllergenEvidence.Traces, "NOTEN",
                "Kan sporen van NOTEN en PINDA'S bevatten.")]));
        first.Flush();

        var mark = Assert.Single(Store().Get("s1002202")!.Allergens);

        Assert.Equal(AllergenEvidence.Traces, mark.Evidence);
        Assert.False(mark.Declared);
        Assert.Equal("NOTEN", mark.Term);
    }

    [Fact]
    public void An_unknown_product_is_simply_absent()
    {
        Assert.Null(Store().Get("s0000000"));
    }

    [Fact]
    public void Expired_records_are_dropped_when_the_file_is_loaded()
    {
        // Without this, persisting a week-long TTL would mean a restart could
        // resurrect a fact that had already aged out -- the failure mode that
        // makes "write it down forever" the wrong design.
        var written = Store();
        written.Put(Details());
        written.Flush();

        // A store whose TTL has since been shortened past the record's age is
        // indistinguishable, from the file's point of view, from time passing.
        Rewrite(fetched: DateTimeOffset.UtcNow.AddHours(-200));

        Assert.Null(Store(ttlHours: 168).Get("s1002202"));
    }

    [Fact]
    public void A_record_still_inside_the_ttl_is_kept()
    {
        var written = Store();
        written.Put(Details());
        written.Flush();

        Rewrite(fetched: DateTimeOffset.UtcNow.AddHours(-100));

        Assert.NotNull(Store(ttlHours: 168).Get("s1002202"));
    }

    [Fact]
    public void A_file_from_an_older_schema_is_discarded_rather_than_read()
    {
        // #14 added a field to PicnicDetails and #45 changed what the allergen
        // parse means. A record written under the old rules must not be read
        // back under the new ones just because its shape still deserializes.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(
            Path.Combine(_dir, "product-facts.json"),
            """
            {"schema":0,"entries":{"s1002202":{"fetched":"2099-01-01T00:00:00+00:00",
             "organic":true,"salt":9.9,"saltText":"9,9 g","allergens":[]}}}
            """);

        Assert.Null(Store().Get("s1002202"));
    }

    [Fact]
    public void A_corrupt_file_starts_empty_instead_of_throwing()
    {
        // A cache must never be able to stop the app from starting.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "product-facts.json"), "{ this is not json");

        Assert.Null(Store().Get("s1002202"));
    }

    [Fact]
    public void An_unwritable_location_does_not_throw()
    {
        // Losing the salt figure on a card is a nuisance; losing the search grid
        // to a full or read-only volume is not acceptable.
        var store = Store();
        store.Put(Details());

        // A directory sitting where the temp file wants to be: the write fails
        // for real rather than the test merely asserting a healthy flush works.
        Directory.CreateDirectory(Path.Combine(_dir, "product-facts.json.tmp"));

        var recorded = Record.Exception(() => store.Flush());

        Assert.Null(recorded);
    }

    [Fact]
    public void Writes_are_batched_rather_than_one_per_product()
    {
        // A grid of ninety cards would otherwise be ninety file writes. The
        // in-memory dictionary is authoritative; the file catches up.
        var store = Store();
        var path = Path.Combine(_dir, "product-facts.json");

        for (var i = 0; i < 20; i++)
            store.Put(Details($"s100000{i}"));

        Assert.False(File.Exists(path), "no write should have happened yet");

        store.Flush();

        Assert.True(File.Exists(path));
        Assert.Equal(20, Entries().Count);
    }

    [Fact]
    public void Every_stored_product_comes_back_after_a_restart()
    {
        var store = Store();
        for (var i = 0; i < 20; i++)
            store.Put(Details($"s100000{i}", organic: i % 2 == 0));
        store.Flush();

        var restarted = Store();

        for (var i = 0; i < 20; i++)
            Assert.Equal(i % 2 == 0, restarted.Get($"s100000{i}")!.Organic);
    }

    // ------------------------------------------------------------------ helpers

    private Dictionary<string, JsonElement> Entries() =>
        JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(_dir, "product-facts.json")))
            .GetProperty("entries")
            .Deserialize<Dictionary<string, JsonElement>>()!;

    /// <summary>Rewrite the one stored record with a different fetch time.</summary>
    private void Rewrite(DateTimeOffset fetched)
    {
        var path = Path.Combine(_dir, "product-facts.json");
        var json = File.ReadAllText(path);
        var current = JsonSerializer.Deserialize<JsonElement>(json)
            .GetProperty("entries").GetProperty("s1002202").GetProperty("fetched").GetString();

        File.WriteAllText(path, json.Replace(current!, fetched.ToString("O")));
    }
}
