using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealiePicnic.Tests;

internal static class TestFactory
{
    public static AppOptions Options(string? dataDir = null) =>
        AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MEALIE_URL"] = "https://mealie.test",
                ["MEALIE_TOKEN"] = "mealie-token",
                ["MEALIE_LIST"] = "Boodschappen",
                ["APP_PASSWORD"] = "pw",
                ["DATA_DIR"] = dataDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            })
            .Build());

    public static MealieClient Mealie(StubHandler handler, AppOptions? options = null) =>
        new(new HttpClient(handler), options ?? Options(), NullLogger<MealieClient>.Instance);

    public static PicnicClient Picnic(
        StubHandler handler, AppOptions? options = null, TokenStore? tokens = null)
    {
        var opts = options ?? Options();
        return new PicnicClient(
            new HttpClient(handler),
            opts,
            tokens ?? new TokenStore(opts, NullLogger<TokenStore>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<PicnicClient>.Instance);
    }
}
