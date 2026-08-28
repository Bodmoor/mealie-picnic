namespace MealiePicnic;

/// <summary>All configuration comes from environment variables (see README).</summary>
public sealed class AppOptions
{
    public string MealieUrl { get; init; } = "";
    public string MealieToken { get; init; } = "";
    public string MealieList { get; init; } = "Boodschappen";

    public string PicnicUser { get; init; } = "";
    public string PicnicPassword { get; init; } = "";
    public string PicnicCountry { get; init; } = "NL";
    public string PicnicApiVersion { get; init; } = "15";

    /// <summary>Password for the web UI login screen. Optional once OIDC is
    /// configured, where it still works as a break-glass fallback at /login/admin.</summary>
    public string AppPassword { get; init; } = "";

    /// <summary>
    /// Authentik (or any OIDC provider) issuer URL, for this app's own dedicated
    /// application (BOODSCHAPPEN_OIDC_AUTHORITY) -- not Mealie's. Presence of this
    /// is what turns OIDC login on; leave unset to keep the single-password login.
    /// </summary>
    public string OidcAuthority { get; init; } = "";

    public string OidcClientId { get; init; } = "";

    public string OidcClientSecret { get; init; } = "";

    public bool OidcEnabled => OidcAuthority.Length > 0;

    /// <summary>Mounted volume for the cached Picnic auth token.</summary>
    public string DataDir { get; init; } = "/data";

    public int SearchCacheMinutes { get; init; } = 30;

    /// <summary>
    /// Force the Secure flag on the session cookie (requires TLS, directly or via
    /// a trusted proxy). Off by default so plain-HTTP local dev keeps working;
    /// turn it on for any deployment that is reachable beyond loopback.
    /// </summary>
    public bool CookieSecure { get; init; }

    /// <summary>
    /// Honour X-Forwarded-For / X-Forwarded-Proto from a reverse proxy. Only
    /// enable when a proxy is actually in front: it trusts those headers blindly.
    /// </summary>
    public bool TrustProxy { get; init; }

    public string PicnicBaseUrl =>
        $"https://storefront-prod.{PicnicCountry.ToLowerInvariant()}.picnicinternational.com/api/{PicnicApiVersion}";

    public string PicnicImageBaseUrl =>
        $"https://storefront-prod.{PicnicCountry.ToLowerInvariant()}.picnicinternational.com/static/images";

    /// <summary>
    /// Reads configuration in the standard ASP.NET Core order, so the same flat key
    /// names work everywhere:
    ///
    ///   appsettings.json  &lt;  user secrets (Development only)  &lt;  environment variables
    ///
    /// In Docker the env vars are the only source. Locally, keep secrets out of
    /// launchSettings.json (which is easy to commit by accident) and use:
    ///
    ///   dotnet user-secrets set "MEALIE_TOKEN" "..."   --project src/MealiePicnic
    ///
    /// Note env vars WIN over user secrets, so a key left in launchSettings.json
    /// will silently shadow the secret. Remove it there rather than blanking it.
    /// </summary>
    public static AppOptions FromConfiguration(IConfiguration configuration)
    {
        string Get(string key, string fallback = "") =>
            configuration[key] is { Length: > 0 } v ? v : fallback;

        var options = new AppOptions
        {
            MealieUrl = Get("MEALIE_URL").TrimEnd('/'),
            MealieToken = Get("MEALIE_TOKEN"),
            MealieList = Get("MEALIE_LIST", "Boodschappen"),
            PicnicUser = Get("PICNIC_USER"),
            PicnicPassword = Get("PICNIC_PASSWORD"),
            PicnicCountry = Get("PICNIC_COUNTRY", "NL"),
            PicnicApiVersion = Get("PICNIC_API_VERSION", "15"),
            AppPassword = Get("APP_PASSWORD"),
            OidcAuthority = Get("BOODSCHAPPEN_OIDC_AUTHORITY"),
            OidcClientId = Get("BOODSCHAPPEN_OIDC_CLIENT_ID"),
            OidcClientSecret = Get("BOODSCHAPPEN_OIDC_CLIENT_SECRET"),
            DataDir = Get("DATA_DIR", "/data"),
            SearchCacheMinutes = int.TryParse(Get("SEARCH_CACHE_MINUTES"), out var m) ? m : 30,
            CookieSecure = bool.TryParse(Get("COOKIE_SECURE"), out var cs) && cs,
            TrustProxy = bool.TryParse(Get("TRUST_PROXY"), out var tp) && tp,
        };

        // Picnic credentials are deliberately not required: they can be typed into
        // the UI instead. Mealie's are always required; APP_PASSWORD only when
        // OIDC is not configured (otherwise it is an optional break-glass extra).
        var missing = new List<string>();
        if (options.MealieUrl.Length == 0) missing.Add("MEALIE_URL");
        if (options.MealieToken.Length == 0) missing.Add("MEALIE_TOKEN");
        if (!options.OidcEnabled && options.AppPassword.Length == 0) missing.Add("APP_PASSWORD");
        if (options.OidcEnabled)
        {
            if (options.OidcClientId.Length == 0) missing.Add("BOODSCHAPPEN_OIDC_CLIENT_ID");
            if (options.OidcClientSecret.Length == 0) missing.Add("BOODSCHAPPEN_OIDC_CLIENT_SECRET");
        }
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Missing configuration: " + string.Join(", ", missing) +
                ". Set them as environment variables, or for local development with " +
                "`dotnet user-secrets set \"<KEY>\" \"<value>\" --project src/MealiePicnic`.");

        return options;
    }
}
