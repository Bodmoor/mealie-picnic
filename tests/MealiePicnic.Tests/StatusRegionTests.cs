using System.Text.RegularExpressions;
using MealiePicnic.Presentation;

namespace MealiePicnic.Tests;

/// <summary>
/// The failure region (issue #63). There is no JavaScript test runner here, so
/// these assert the two things the server owns and app.js depends on: the element
/// exists with the attributes that make it announce itself, and both messages
/// actually reach the client.
/// </summary>
[Collection(AppTextCollection.Name)]
public class StatusRegionTests
{
    [Fact]
    public void The_shell_carries_a_live_status_region()
    {
        var region = Regex.Match(Html.AppPage, "<div id=\"status\"[^>]*>").Value;

        Assert.NotEqual("", region);
        // Announced without stealing focus, and hidden until something fails.
        Assert.Contains("aria-live=\"polite\"", region);
        Assert.Contains("role=\"status\"", region);
        Assert.Contains("hidden", region);
    }

    [Fact]
    public void Both_failure_messages_reach_the_client()
    {
        // app.js looks them up by key; a missing one would render "undefined" in
        // the place where the explanation should be.
        var json = AppText.Current.ClientJson();

        Assert.Contains("upstreamDown", json);
        Assert.Contains("somethingWentWrong", json);
    }

    [Fact]
    public void The_status_region_is_not_an_htmx_target()
    {
        // It is written from app.js only. A stray hx-swap into it would let a
        // failed request's own error body land in the place reserved for a
        // sentence explaining that request failed.
        var region = Regex.Match(Html.AppPage, "<div id=\"status\"[^>]*>").Value;

        Assert.DoesNotContain("hx-", region);
    }
}
