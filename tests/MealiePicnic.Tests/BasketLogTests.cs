using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MealiePicnic.Clients;
using MealiePicnic.Presentation;
using MealiePicnic.Slices;
using RazorSlices;

namespace MealiePicnic.Tests;

/// <summary>
/// The basket log, and a tripwire for the Razor trap that broke it (issue #52).
/// </summary>
[Collection(AppTextCollection.Name)]
public class BasketLogTests
{
    private static ValueTask<string> Log(params CartResult[] results) =>
        BasketLog.Create(new BasketLogModel([.. results], Unmapped: 0, Aborted: false, CheckOff: false))
            .RenderAsync();

    [Fact]
    public async Task A_successful_line_shows_how_many_packs_went_in()
    {
        var html = await Log(new CartResult("Volle melk", "s1002202", 2, Ok: true, Error: null));

        Assert.Contains("Volle melk x2", html);
        // What it printed before: the template's own source, because Razor read
        // `x@r.Amount` as an email address and left it alone.
        Assert.DoesNotContain("r.Amount", html);
    }

    [Fact]
    public async Task A_failed_line_shows_the_error_and_no_amount()
    {
        var html = await Log(new CartResult("Volle melk", "s1002202", 1, Ok: false, Error: "uitverkocht"));

        Assert.Contains("uitverkocht", html);
        Assert.DoesNotContain("r.Amount", html);
    }

    // ------------------------------------------------------------------ tripwire

    [Fact]
    public void No_slice_puts_an_expression_where_razor_will_read_an_address()
    {
        // The failure this catches is silent: `x@r.Amount` compiles, renders, and
        // looks like text somebody typed. Razor's email heuristic fires whenever a
        // word character sits immediately before the `@`, so the rule here is that
        // an `@` in that position must open an explicit expression -- `@(` -- or be
        // an escaped literal `@@`.
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SlicesDirectory(), "*.cshtml"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, @"\w@(?![@(])"))
            {
                // An escaped `@@` is two characters, and the second one is matched
                // here as if it began an expression; skip that case.
                if (match.Index > 0 && text[match.Index] == '@') continue;
                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}  {Excerpt(text, match.Index)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Razor will render these verbatim as email addresses:\n" + string.Join("\n", offenders));
    }

    private static string Excerpt(string text, int index)
    {
        var start = Math.Max(0, index - 20);
        var length = Math.Min(60, text.Length - start);
        return "..." + text.Substring(start, length).Replace('\n', ' ').Replace('\r', ' ') + "...";
    }

    /// <summary>
    /// The slices are compiled into the assembly, so there is no copy beside the
    /// test binary to scan. Locating them from this file's own path is what makes
    /// the tripwire possible at all.
    /// </summary>
    private static string SlicesDirectory([CallerFilePath] string thisFile = "")
    {
        var testsDir = Path.GetDirectoryName(thisFile)!;                       // tests/MealiePicnic.Tests
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", ".."));       // repository root
        return Path.Combine(repo, "src", "MealiePicnic", "Slices");
    }
}
