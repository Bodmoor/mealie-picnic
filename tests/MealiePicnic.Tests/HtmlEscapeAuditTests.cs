using System.Text.RegularExpressions;

namespace MealiePicnic.Tests;

/// <summary>
/// Tripwire for the XSS class fixed in the security review: values interpolated
/// into single-quoted JS strings inside onclick attributes, or into attribute
/// values, without esc(). Ids from Mealie/Picnic look safe today, but that is
/// the upstream's invariant, not ours.
/// </summary>
public class HtmlEscapeAuditTests
{
    [Fact]
    public void No_interpolation_inside_a_js_string_without_esc()
    {
        // onclick="fn('${...}')" -- the ' breaks out unless the value is esc()-ed.
        var offenders = Regex.Matches(Html.AppPage, @"\('\$\{(?!esc\()")
            .Select(m => Context(m.Index)).ToList();

        Assert.True(offenders.Count == 0,
            "Unescaped interpolation inside a JS string:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void No_interpolation_as_attribute_value_without_esc()
    {
        // ="${...}" -- attribute injection unless esc()-ed (numbers via ${index} are
        // not written in this form anywhere).
        var offenders = Regex.Matches(Html.AppPage, @"=""\$\{(?!esc\()")
            .Select(m => Context(m.Index)).ToList();

        Assert.True(offenders.Count == 0,
            "Unescaped interpolation as attribute value:\n" + string.Join("\n", offenders));
    }

    private static string Context(int index)
    {
        var start = Math.Max(0, index - 40);
        var length = Math.Min(90, Html.AppPage.Length - start);
        return "..." + Html.AppPage.Substring(start, length).Replace('\n', ' ') + "...";
    }
}
