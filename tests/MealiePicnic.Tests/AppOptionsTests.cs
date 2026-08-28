using Microsoft.Extensions.Configuration;

namespace MealiePicnic.Tests;

public class AppOptionsTests
{
    private static AppOptions Build(params (string Key, string? Value)[] settings) =>
        AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build());

    private static (string, string?)[] Required =>
    [
        ("MEALIE_URL", "https://mealie.test"),
        ("MEALIE_TOKEN", "t"),
        ("APP_PASSWORD", "pw"),
    ];

    [Fact]
    public void Applies_defaults_for_optional_keys()
    {
        var options = Build(Required);

        Assert.Equal("Boodschappen", options.MealieList);
        Assert.Equal("NL", options.PicnicCountry);
        Assert.Equal("15", options.PicnicApiVersion);
        Assert.Equal("/data", options.DataDir);
        Assert.Equal(30, options.SearchCacheMinutes);
    }

    [Fact]
    public void Cookie_secure_and_trust_proxy_default_to_true()
    {
        // HTTPS must be assumed unless told otherwise, or a deployment left
        // unconfigured silently ships without either protection.
        var options = Build(Required);

        Assert.True(options.CookieSecure);
        Assert.True(options.TrustProxy);
    }

    [Fact]
    public void Cookie_secure_and_trust_proxy_can_be_opted_out_for_plain_http()
    {
        var options = Build([.. Required, ("COOKIE_SECURE", "false"), ("TRUST_PROXY", "false")]);

        Assert.False(options.CookieSecure);
        Assert.False(options.TrustProxy);
    }

    [Fact]
    public void Trims_trailing_slash_from_mealie_url()
    {
        // Paths are concatenated as $"{MealieUrl}/api/...", so a trailing
        // slash would produce a double slash.
        var options = Build([("MEALIE_URL", "https://mealie.test/"), ("MEALIE_TOKEN", "t"), ("APP_PASSWORD", "pw")]);

        Assert.Equal("https://mealie.test", options.MealieUrl);
    }

    [Theory]
    [InlineData("MEALIE_URL")]
    [InlineData("MEALIE_TOKEN")]
    [InlineData("APP_PASSWORD")]
    public void Reports_each_missing_required_key(string missing)
    {
        var settings = Required.Where(s => s.Item1 != missing).ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Picnic_credentials_are_optional()
    {
        // They can be typed into the UI instead, so startup must not fail.
        var options = Build(Required);

        Assert.Equal("", options.PicnicUser);
        Assert.Equal("", options.PicnicPassword);
    }

    [Fact]
    public void Blank_value_falls_back_to_default()
    {
        // Mirrors an env var set to "" shadowing a user secret.
        var options = Build([.. Required, ("MEALIE_LIST", "")]);

        Assert.Equal("Boodschappen", options.MealieList);
    }

    [Fact]
    public void Builds_country_specific_picnic_urls()
    {
        var options = Build([.. Required, ("PICNIC_COUNTRY", "DE"), ("PICNIC_API_VERSION", "17")]);

        Assert.Equal("https://storefront-prod.de.picnicinternational.com/api/17", options.PicnicBaseUrl);
        Assert.Equal("https://storefront-prod.de.picnicinternational.com/static/images", options.PicnicImageBaseUrl);
    }

    [Fact]
    public void Ignores_unparsable_cache_minutes()
    {
        var options = Build([.. Required, ("SEARCH_CACHE_MINUTES", "not-a-number")]);

        Assert.Equal(30, options.SearchCacheMinutes);
    }

    [Fact]
    public void Oidc_is_disabled_when_authority_is_unset()
    {
        var options = Build(Required);

        Assert.False(options.OidcEnabled);
    }

    [Fact]
    public void Oidc_requires_client_id_and_secret_once_authority_is_set()
    {
        var settings = Required
            .Append(("BOODSCHAPPEN_OIDC_AUTHORITY", "https://authentik.test/application/o/boodschappen/"))
            .ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => Build(settings));

        Assert.Contains("BOODSCHAPPEN_OIDC_CLIENT_ID", ex.Message);
        Assert.Contains("BOODSCHAPPEN_OIDC_CLIENT_SECRET", ex.Message);
    }

    [Fact]
    public void App_password_becomes_optional_once_oidc_is_configured()
    {
        var settings = Required.Where(s => s.Item1 != "APP_PASSWORD").Concat(
        [
            ("BOODSCHAPPEN_OIDC_AUTHORITY", "https://authentik.test/application/o/boodschappen/"),
            ("BOODSCHAPPEN_OIDC_CLIENT_ID", "mealie-picnic"),
            ("BOODSCHAPPEN_OIDC_CLIENT_SECRET", "secret"),
        ]).ToArray();

        var options = Build(settings);

        Assert.True(options.OidcEnabled);
        Assert.Equal("", options.AppPassword);
    }
}
