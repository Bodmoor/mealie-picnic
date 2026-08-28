using System.Security.Claims;
using MealiePicnic.Clients;
using MealiePicnic.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MealiePicnic.Tests;

internal static class TestFactory
{
    public static AppOptions Options(string? dataDir = null, bool oidcEnabled = false) =>
        AppOptions.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MEALIE_URL"] = "https://mealie.test",
                ["MEALIE_TOKEN"] = "mealie-token",
                ["MEALIE_LIST"] = "Boodschappen",
                ["APP_PASSWORD"] = "pw",
                ["DATA_DIR"] = dataDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                ["OIDC_AUTHORITY"] = oidcEnabled ? "https://authentik.test/application/o/mealie/" : null,
                ["OIDC_CLIENT_ID"] = oidcEnabled ? "test-client" : null,
                ["OIDC_CLIENT_SECRET"] = oidcEnabled ? "test-secret" : null,
            })
            .Build());

    public static MealieClient Mealie(StubHandler handler, AppOptions? options = null) =>
        new(new HttpClient(handler), options ?? Options());

    public static PicnicClient Picnic(
        StubHandler handler, AppOptions? options = null, TokenStore? tokens = null, ClaimsPrincipal? user = null)
    {
        var opts = options ?? Options();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = user ?? new ClaimsPrincipal(new ClaimsIdentity()) },
        };
        return new PicnicClient(
            new HttpClient(handler),
            opts,
            tokens ?? new TokenStore(opts, NullLogger<TokenStore>.Instance),
            new MemoryCache(new MemoryCacheOptions()),
            accessor,
            NullLogger<PicnicClient>.Instance);
    }
}
