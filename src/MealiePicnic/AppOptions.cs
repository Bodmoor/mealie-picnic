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

    public static AppOptions FromEnvironment()
    {
        string Get(string key, string fallback = "") =>
            Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : fallback;

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

        var missing = new List<string>();
        if (options.MealieUrl.Length == 0) missing.Add("MEALIE_URL");
        if (options.MealieToken.Length == 0) missing.Add("MEALIE_TOKEN");
        if (options.AppPassword.Length == 0) missing.Add("APP_PASSWORD");
        if (missing.Count > 0)
            throw new InvalidOperationException("Missing env vars: " + string.Join(", ", missing));

        return options;
    }
}
