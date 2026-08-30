using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MealiePicnic.Tests;

/// <summary>
/// A tripwire for the flake that let ModelTests fail in CI after passing locally
/// for months.
///
/// <see cref="AppText.Current"/> is process-wide, and one test switches it to
/// English and back. xUnit runs classes in parallel, so any class that *reads*
/// the global has to be in the same collection as the class that writes it —
/// otherwise it is a race that scheduling hides until the day it does not.
///
/// The rule is easy to state and easy to forget, which is exactly the kind of
/// rule worth asserting rather than documenting.
/// </summary>
public class AppTextCollectionAuditTests
{
    /// <summary>
    /// Reaching the global: directly, or through one of the members that read it
    /// for their number format. The "In" overloads beside each of those take an
    /// AppText explicitly and touch nothing shared, so they are excluded rather
    /// than flagged -- using them is the fix this audit wants people to reach for.
    /// </summary>
    private static readonly Regex TouchesTheGlobal = new(
        @"AppText.Current|PriceText(?!In)|AmountReason(?!In)",
        RegexOptions.Compiled);

    [Fact]
    public void Every_test_class_that_touches_the_shared_AppText_is_in_its_collection()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(TestsDirectory(), "*.cs"))
        {
            var name = Path.GetFileName(file);
            // The collection definition itself, and this audit, only name it.
            if (name is "AppTextCollection.cs" or "AppTextCollectionAuditTests.cs") continue;

            var text = File.ReadAllText(file);
            // Comments name these members while explaining them, which is not a read.
            var code = Regex.Replace(text, @"//[^
]*", "");
            if (!TouchesTheGlobal.IsMatch(code)) continue;
            if (Regex.IsMatch(text, @"\[Collection\(AppTextCollection\.Name\)\]")) continue;

            offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "These read the process-wide AppText but are not in its collection, so they race "
            + "with the test that switches it to English:\n" + string.Join("\n", offenders));
    }

    private static string TestsDirectory([CallerFilePath] string thisFile = "") =>
        Path.GetDirectoryName(thisFile)!;
}
