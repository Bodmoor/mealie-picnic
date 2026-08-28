using System.Reflection;

namespace MealiePicnic.Presentation;

/// <summary>
/// Third-party JS, embedded and self-served rather than loaded from a CDN. A
/// CDN script-src entry would be an external dependency and a wider CSP trust
/// boundary for an app that holds a supermarket login; embedding matches how
/// the PWA assets in <see cref="Icons"/> are already handled, and a version
/// bump is a deliberate file swap instead of silent CDN drift.
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
}
