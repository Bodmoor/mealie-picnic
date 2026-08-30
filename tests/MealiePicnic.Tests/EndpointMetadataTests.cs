using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MealiePicnic.Tests;

/// <summary>
/// What the routing table itself says, rather than what a request happens to do.
///
/// Written after the endpoints were split across eight files (issue #66): the
/// argument for that move was "the tests are the evidence", and they were not.
/// Nothing asserted that the brute-force limiter was still attached to the
/// credential routes, so the move could have dropped it from
/// /api/picnic/login -- the brute-force surface on a real supermarket account --
/// and the suite would have stayed green.
/// </summary>
[Collection(AppTextCollection.Name)]
public class EndpointMetadataTests
{
    private static WebApplicationFactory<Program> NewFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            builder.UseSetting("APP_PASSWORD", "correct-horse-battery-staple");
            builder.UseSetting("DATA_DIR", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            // Set explicitly, not left to the machine: WebApplicationFactory loads
            // the developer's real user secrets in Development, so whether OIDC is
            // configured -- and therefore whether /login/admin is mapped at all --
            // otherwise depends on whose laptop is running the suite. It passed
            // locally and failed in CI for exactly that reason.
            builder.UseSetting("BOODSCHAPPEN_OIDC_AUTHORITY", "https://authentik.test/application/o/boodschappen/");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_ID", "boodschappen");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_SECRET", "secret");
        });

    private static List<RouteEndpoint> Endpoints(WebApplicationFactory<Program> factory) =>
        [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];

    private static RouteEndpoint Route(List<RouteEndpoint> endpoints, string pattern, string method) =>
        Assert.Single(endpoints, e =>
            e.RoutePattern.RawText == pattern &&
            e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method));

    [Theory]
    // Everything that checks a password or triggers a 2FA send.
    [InlineData("/login", "POST")]
    [InlineData("/login/admin", "POST")]
    [InlineData("/api/picnic/login", "POST")]
    [InlineData("/api/picnic/2fa/generate", "POST")]
    [InlineData("/api/picnic/2fa/verify", "POST")]
    public void Credential_routes_carry_the_brute_force_limiter(string pattern, string method)
    {
        using var factory = NewFactory();
        var endpoint = Route(Endpoints(factory), pattern, method);

        var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        Assert.Equal("credentials", policy);
    }

    [Fact]
    public void The_health_probe_is_limited_too_but_not_as_a_credential_route()
    {
        // It is anonymous and writes to DATA_DIR, so it needs a ceiling -- but on
        // the credentials policy (5/minute) an ordinary proxy check would trip it.
        using var factory = NewFactory();
        var endpoint = Route(Endpoints(factory), "/health", "GET");

        Assert.Equal("health", endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName);
    }

    [Theory]
    // Fetched by the browser before there is any session to authenticate with.
    [InlineData("/login", "GET")]
    [InlineData("/health", "GET")]
    [InlineData("/favicon.ico", "GET")]
    [InlineData("/manifest.webmanifest", "GET")]
    [InlineData("/sw.js", "GET")]
    public void Routes_that_must_work_without_a_session_are_anonymous(string pattern, string method)
    {
        using var factory = NewFactory();
        var endpoint = Route(Endpoints(factory), pattern, method);

        Assert.NotNull(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>());
    }

    [Theory]
    // The reverse: these move real data or spend real money.
    [InlineData("/api/basket", "POST")]
    [InlineData("/api/link", "POST")]
    [InlineData("/api/products/{productId}", "GET")]
    [InlineData("/api/products/{productId}/allergens/{group}", "POST")]
    public void Routes_that_act_on_the_household_are_not_anonymous(string pattern, string method)
    {
        using var factory = NewFactory();
        var endpoint = Route(Endpoints(factory), pattern, method);

        Assert.Null(endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>());
    }
}
