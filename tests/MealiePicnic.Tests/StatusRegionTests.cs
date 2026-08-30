using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MealiePicnic.Presentation;

namespace MealiePicnic.Tests;

/// <summary>
/// The failure region (issue #63). There is no JavaScript test runner here, so
/// these assert the properties the server owns and the ones that can be read off
/// app.js as source -- deliberately not "the markup contains what I just wrote",
/// which is what the first version of this class did.
/// </summary>
[Collection(AppTextCollection.Name)]
public class StatusRegionTests
{
    private static string Region() =>
        Regex.Match(Html.AppPage, "<div id=\"status\"[^>]*>").Value;

    private static string ClientScript([CallerFilePath] string thisFile = "")
    {
        var repo = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(repo, "src", "MealiePicnic", "assets", "app.js"));
    }

    [Fact]
    public void The_shell_carries_a_live_status_region()
    {
        var region = Region();

        Assert.NotEqual("", region);
        // Announced without stealing focus, and not shown until it has something
        // to say. Matched as whole attributes so `data-hidden="false"` could not
        // satisfy the last one.
        Assert.Matches(@"\baria-live=""polite""", region);
        Assert.Matches(@"\brole=""status""", region);
        Assert.Matches(@"\shidden(\s|>)", region);
    }

    [Fact]
    public void The_message_is_written_as_text_never_as_markup()
    {
        // The property that actually matters: whatever ends up in that element
        // cannot become markup. An upstream error body is attacker-influenced in
        // the general case, so the day someone writes a server message in here,
        // textContent is what keeps it inert.
        var writes = Regex.Matches(ClientScript(), @"\bel\.(textContent|innerHTML)\s*=")
            .Select(m => m.Value).ToList();

        Assert.Contains("el.textContent =", writes);
        Assert.DoesNotContain("el.innerHTML =", Regex.Match(
            ClientScript(), @"function showStatus[\s\S]*?\n\}").Value);
    }

    [Fact]
    public void Both_failure_messages_carry_real_text()
    {
        // The first version asserted the key names existed, which an empty string
        // satisfies -- and an empty string renders a blank red bar that says
        // nothing at all.
        Assert.NotEmpty(AppText.Dutch.JsUpstreamDown);
        Assert.NotEmpty(AppText.Dutch.JsSomethingWentWrong);
        Assert.Contains(AppText.Current.JsUpstreamDown, AppText.Current.ClientJson());
        Assert.Contains(AppText.Current.JsSomethingWentWrong, AppText.Current.ClientJson());
    }

    [Fact]
    public void A_successful_request_clears_a_previous_failure()
    {
        // Without this the region keeps reporting a failure that has since been
        // retried successfully, under a page that is now working.
        var script = ClientScript();

        Assert.Matches(@"htmx:afterRequest[\s\S]{0,200}successful[\s\S]{0,80}clearStatus\(\)", script);
    }

    [Fact]
    public void The_no_response_case_is_handled_too()
    {
        // htmx raises sendError, not responseError, when there is no response at
        // all -- so handling only the latter left the most total failure the
        // quietest.
        Assert.Contains("htmx:sendError", ClientScript());
    }
}
