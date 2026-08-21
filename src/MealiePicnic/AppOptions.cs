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

    /// <summary>Password for the web UI login screen.</summary>
    public string AppPassword { get; init; } = "";

    /// <summary>Mounted volume for the cached Picnic auth token.</summary>
    public string DataDir { get; init; } = "/data";

    public int SearchCacheMinutes { get; init; } = 30;

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
            DataDir = Get("DATA_DIR", "/data"),
            SearchCacheMinutes = int.TryParse(Get("SEARCH_CACHE_MINUTES"), out var m) ? m : 30,
        };

        // Picnic credentials are deliberately not required: they can be typed into
        // the UI instead. These three have no such fallback.
        var missing = new List<string>();
        if (options.MealieUrl.Length == 0) missing.Add("MEALIE_URL");
        if (options.MealieToken.Length == 0) missing.Add("MEALIE_TOKEN");
        if (options.AppPassword.Length == 0) missing.Add("APP_PASSWORD");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                "Missing configuration: " + string.Join(", ", missing) +
                ". Set them as environment variables, or for local development with " +
                "`dotnet user-secrets set \"<KEY>\" \"<value>\" --project src/MealiePicnic`.");

        return options;
    }
}
