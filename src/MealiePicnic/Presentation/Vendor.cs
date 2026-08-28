using System.Reflection;

namespace MealiePicnic.Presentation;

/// <summary>
/// JS served as static files rather than inlined into a page: the third-party
/// libraries, and this app's own <c>app.js</c>. A CDN script-src entry for the
/// libraries would be an external dependency and a wider CSP trust boundary for
/// an app that holds a supermarket login, so they are embedded and self-served
/// instead, the same as the PWA assets in <see cref="Icons"/> -- a version bump
/// is then a deliberate file swap rather than silent CDN drift. app.js is here
/// rather than inline in the page for the same CSP reason: a strict script-src
/// with no 'unsafe-inline' cannot run an inline &lt;script&gt; block at all.
///
/// htmx needs neither 'unsafe-inline' nor 'unsafe-eval'. Alpine is the
/// @alpinejs/csp build specifically (not the default build): it restricts
/// directive expressions to a safe subset instead of evaluating arbitrary JS
/// via `new Function()`, so it runs without 'unsafe-eval' either.
/// </summary>
public static class Vendor
{
    private static string Load(string fileName)
    {
        var name = $"MealiePicnic.assets.{fileName}";
        using var stream = typeof(Vendor).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' is missing. Available: " +
                string.Join(", ", typeof(Vendor).Assembly.GetManifestResourceNames()));

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>htmx 2.x, minified.</summary>
    public static string HtmxJs { get; } = Load("htmx.min.js");

    /// <summary>@alpinejs/csp 3.x, minified.</summary>
    public static string AlpineCspJs { get; } = Load("alpine-csp.min.js");

    /// <summary>This app's own Picnic-auth/2FA and product-facts client logic.</summary>
    public static string AppJs { get; } = Load("app.js");
}
