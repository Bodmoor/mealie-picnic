using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MealiePicnic.Tests;

/// <summary>
/// Request logging (issue #68). Two of these are about the log being safe rather
/// than the log being present: the framework's own request logging wrote the OIDC
/// callback's authorization code and state to the console in full, and a
/// percent-decoded path can otherwise write a second, invented line.
/// </summary>
[Collection(AppTextCollection.Name)]
public class RequestLoggingTests
{
    private const string Password = "correct-horse-battery-staple";

    /// <summary>
    /// Records what a real console sink would write: the message, and the scopes
    /// as they read AT WRITE TIME. Stringifying a scope when it opens instead
    /// would have hidden the bug this class now covers -- the signed-in identity
    /// is not known that early.
    /// </summary>
    private sealed class Capture : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public List<string> Scopes { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(this);

        public void Dispose() { }

        private sealed class Sink(Capture owner) : ILogger
        {
            private static readonly AsyncLocal<List<object>> Active = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull
            {
                Active.Value ??= [];
                Active.Value.Add(state);
                return new Pop(Active.Value, state);
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner.Lines)
                {
                    owner.Lines.Add(formatter(state, exception));
                    foreach (var scope in Active.Value ?? [])
                        owner.Scopes.Add(scope.ToString() ?? "");
                }
            }

            private sealed class Pop(List<object> active, object state) : IDisposable
            {
                public void Dispose() => active.Remove(state);
            }
        }
    }

    private static (WebApplicationFactory<Program> Factory, Capture Log) NewFactory()
    {
        var capture = new Capture();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            builder.UseSetting("APP_PASSWORD", Password);
            builder.UseSetting("DATA_DIR", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
            builder.UseSetting("BOODSCHAPPEN_OIDC_AUTHORITY", "");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_ID", "");
            builder.UseSetting("BOODSCHAPPEN_OIDC_CLIENT_SECRET", "");
            builder.ConfigureServices(services => services.AddSingleton<ILoggerProvider>(capture));
        });
        return (factory, capture);
    }

    [Fact]
    public async Task A_request_is_logged_once_with_method_path_and_status()
    {
        var (factory, log) = NewFactory();
        using (factory)
        {
            await factory.CreateClient().GetAsync("/login");

            Assert.Single(log.Lines, l => l.StartsWith("GET /login 200 in "));
        }
    }

    [Fact]
    public async Task The_query_string_is_never_logged()
    {
        // The OIDC callback carries `code` and `state` here. Nothing in the log
        // may contain them, which is only guaranteed while the logger works from
        // Request.Path rather than the full URL.
        var (factory, log) = NewFactory();
        using (factory)
        {
            await factory.CreateClient().GetAsync("/login?code=super-secret-code&state=super-secret-state");

            Assert.DoesNotContain(log.Lines, l => l.Contains("super-secret"));
            Assert.Contains(log.Lines, l => l.StartsWith("GET /login "));
        }
    }

    [Fact]
    public async Task A_path_cannot_forge_a_second_log_line()
    {
        // Paths arrive percent-DECODED, so without stripping control characters a
        // remote caller writes whatever it likes into the log — and a forged line
        // is exactly what misleads whoever is reading it while debugging.
        var (factory, log) = NewFactory();
        using (factory)
        {
            await factory.CreateClient().GetAsync("/login%0a2026-01-01%20FORGED");

            Assert.DoesNotContain(log.Lines, l => l.Contains('\n') || l.Contains('\r'));
            // The characters themselves still show; it is the line break that mattered.
            Assert.Contains(log.Lines, l => l.Contains("FORGED"));
        }
    }

    [Fact]
    public async Task A_signed_in_request_is_attributed_to_the_person_who_made_it()
    {
        // The scope opens before UseAuthentication runs — deliberately, so a
        // request rejected by the rate limiter still gets a line — so reading
        // ctx.User at that moment recorded every request as anonymous, signed in
        // or not. It is resolved when a line is written instead.
        var (factory, log) = NewFactory();
        using (factory)
        {
            var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var login = await client.PostAsync("/login",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("password", Password)]));
            var cookie = login.Headers.GetValues("Set-Cookie")
                .First(c => c.StartsWith("mealiepicnic=")).Split(';')[0];

            var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
            request.Headers.Add("Cookie", cookie);
            await client.SendAsync(request);

            Assert.Contains(log.Scopes, s => s == "user:owner");
        }
    }

    [Fact]
    public async Task An_anonymous_request_says_so()
    {
        var (factory, log) = NewFactory();
        using (factory)
        {
            await factory.CreateClient().GetAsync("/login");

            Assert.Contains(log.Scopes, s => s == "user:anonymous");
        }
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/assets/app.js")]
    public async Task Polling_and_static_files_are_not_logged(string path)
    {
        var (factory, log) = NewFactory();
        using (factory)
        {
            var response = await factory.CreateClient().GetAsync(path);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(log.Lines, l => l.Contains(path));
        }
    }
}
