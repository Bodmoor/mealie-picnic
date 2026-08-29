using System.Reflection;

namespace MealiePicnic.Presentation;

/// <summary>
/// PWA assets. The icons live as real PNGs in <c>assets/</c> and are compiled in as
/// embedded resources, so the app stays a single self-contained artifact -- no
/// wwwroot, no static-file middleware, nothing to copy next to the DLL.
///
/// The mark is Mealie's fork-and-knife on Mealie orange, torn diagonally against
/// Picnic's logo on Picnic red. To change it, replace the files in <c>assets/</c>.
/// The maskable variant needs its art inset by roughly 20%, or Android's circular
/// crop clips the wordmark.
/// </summary>
public static class Icons
{
    private static byte[] Load(string fileName)
    {
        // Matches the LogicalName set in MealiePicnic.csproj.
        var name = $"MealiePicnic.assets.{fileName}";
        using var stream = typeof(Icons).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}' is missing. Available: " +
                string.Join(", ", typeof(Icons).Assembly.GetManifestResourceNames()));

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // Read once at startup: a missing or renamed asset then fails immediately and
    // loudly, rather than 500-ing the first time a browser asks for the favicon.
    public static byte[] Png192 { get; } = Load("icon-192.png");
    public static byte[] Png512 { get; } = Load("icon-512.png");
    public static byte[] PngMaskable512 { get; } = Load("icon-maskable-512.png");

    /// <summary>
    /// The EU organic mark (twelve stars in a leaf, on the regulation green),
    /// shown on products whose page claims they are organic. SVG because it is
    /// rendered at badge size next to the price and must stay legible.
    /// </summary>
    public static byte[] EuOrganicSvg { get; } = Load("eu-organic.svg");

    /// <summary>
    /// Web app manifest. Served from a route (not a file) so it can be marked
    /// AllowAnonymous: Android fetches it before the user has logged in, and a
    /// 302-to-login would make the app look uninstallable.
    ///
    /// Localized (issue #13) even though it is fetched anonymously: LANGUAGE is a
    /// property of the deployment, not of the caller, so there is no per-user
    /// choice to be wrong about. The name is the product lockup and stays put.
    /// </summary>
    public static string Manifest => ManifestFor(AppText.Current);

    public static string ManifestFor(AppText text) => $$"""
        {
          "id": "/",
          "name": "Mealie → Picnic",
          "short_name": "{{text.AppShortName}}",
          "description": "{{text.AppDescription}}",
          "lang": "{{text.Lang}}",
          "start_url": "/",
          "scope": "/",
          "display": "standalone",
          "background_color": "#15171a",
          "theme_color": "#E58325",
          "icons": [
            { "src": "/icons/192.png", "sizes": "192x192", "type": "image/png" },
            { "src": "/icons/512.png", "sizes": "512x512", "type": "image/png" },
            { "src": "/icons/maskable-512.png", "sizes": "512x512", "type": "image/png",
              "purpose": "maskable" }
          ]
        }
        """;

    /// <summary>
    /// Minimal service worker. Chrome requires one with a fetch handler before it
    /// will offer to install the app.
    ///
    /// Deliberately pass-through with no caching: the UI is generated server-side
    /// and changes on every deploy, so a cached shell would serve stale markup
    /// against a newer API. There is no offline mode -- the app is useless without
    /// Mealie and Picnic anyway.
    /// </summary>
    public const string ServiceWorker = """
        self.addEventListener('install', () => self.skipWaiting());
        self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));
        self.addEventListener('fetch', () => { /* network only, by design */ });
        """;
}
