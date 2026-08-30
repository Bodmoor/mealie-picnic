using MealiePicnic.Clients;
using MealiePicnic.Storage;

namespace MealiePicnic.Tests;

/// <summary>
/// Verdicts on suspected allergen marks (issue #48). The store is global by
/// design -- whether a product contains nuts is not a per-household question --
/// so these tests deliberately never mention a household key.
/// </summary>
public class AllergenVerdictStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    private AllergenVerdictStore New() => new(TestFactory.Options(_dir));

    [Fact]
    public void A_product_with_no_verdicts_reports_none()
    {
        Assert.Empty(New().ForProduct("s1005080"));
    }

    [Fact]
    public void A_verdict_records_its_state_author_and_evidence()
    {
        var store = New();
        store.Set("s1005080", AllergenGroups.Nuts, VerdictState.Denied, "paul@example.com", "Volle melk, cacao");

        var verdict = Assert.Contains(AllergenGroups.Nuts, store.ForProduct("s1005080"));

        Assert.Equal(VerdictState.Denied, verdict.State);
        Assert.Equal("paul@example.com", verdict.By);
        Assert.Equal("Volle melk, cacao", verdict.Against);
        Assert.True(verdict.At <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Verdicts_survive_a_restart()
    {
        // The whole point of a store rather than a cache: a decision someone made
        // last week must still be there after a deploy.
        New().Set("s1005080", AllergenGroups.Milk, VerdictState.Confirmed, null, "Volle melk");

        var verdict = Assert.Contains(AllergenGroups.Milk, New().ForProduct("s1005080"));

        Assert.Equal(VerdictState.Confirmed, verdict.State);
        Assert.Null(verdict.By);
    }

    [Fact]
    public void A_second_verdict_replaces_the_first()
    {
        var store = New();
        store.Set("s1005080", AllergenGroups.Nuts, VerdictState.Denied, null, "old text");
        store.Set("s1005080", AllergenGroups.Nuts, VerdictState.Confirmed, null, "new text");

        var verdict = Assert.Contains(AllergenGroups.Nuts, store.ForProduct("s1005080"));

        Assert.Equal(VerdictState.Confirmed, verdict.State);
        Assert.Equal("new text", verdict.Against);
    }

    [Fact]
    public void Clearing_returns_the_mark_to_unreviewed()
    {
        var store = New();
        store.Set("s1005080", AllergenGroups.Nuts, VerdictState.Confirmed, null, null);
        store.Clear("s1005080", AllergenGroups.Nuts);

        Assert.Empty(store.ForProduct("s1005080"));
        Assert.Empty(New().ForProduct("s1005080"));
    }

    [Fact]
    public void Verdicts_are_kept_per_product_and_per_group()
    {
        var store = New();
        store.Set("s1005080", AllergenGroups.Nuts, VerdictState.Denied, null, null);
        store.Set("s1005080", AllergenGroups.Milk, VerdictState.Confirmed, null, null);
        store.Set("s2000000", AllergenGroups.Nuts, VerdictState.Confirmed, null, null);

        Assert.Equal(2, store.ForProduct("s1005080").Count);
        Assert.Equal(VerdictState.Denied, store.ForProduct("s1005080")[AllergenGroups.Nuts].State);
        Assert.Equal(VerdictState.Confirmed, store.ForProduct("s2000000")[AllergenGroups.Nuts].State);
    }

    [Fact]
    public void A_truncated_file_reads_as_no_verdicts_rather_than_throwing()
    {
        // Same tolerance as the link store: a crash mid-write must not take the
        // product view down, and "not reviewed" is the honest fallback.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "allergen-verdicts.json"), "{\"s1005080\": {\"nuts\":");

        Assert.Empty(New().ForProduct("s1005080"));
    }

    [Fact]
    public void The_returned_map_is_a_copy()
    {
        // Callers get a snapshot to render; mutating it must not reach the store.
        var store = New();
        store.Set("s1005080", AllergenGroups.Nuts, VerdictState.Confirmed, null, null);

        var snapshot = (IDictionary<string, AllergenVerdict>)store.ForProduct("s1005080");
        snapshot.Remove(AllergenGroups.Nuts);

        Assert.Single(store.ForProduct("s1005080"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }
}
