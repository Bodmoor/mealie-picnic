using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MealiePicnic.Tests;

/// <summary>
/// Request logging (issue #68). The assertion that matters is the redaction: the
/// framework's own request logging wrote the OIDC callback's authorization code
/// and state to the console in full, and the replacement must be unable to.
/// </summary>
[Collection(AppTextCollection.Name)]
public class RequestLoggingTests
{
    private sealed class Capture : ILoggerProvider
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Sink(Lines);

        public void Dispose() { }

        private sealed class Sink(List<string> lines) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => new Nothing();

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines) lines.Add(formatter(state, exception));
            }

            private sealed class Nothing : IDisposable { public void Dispose() { } }
        }
    }

    private static (WebApplicationFactory<Program> Factory, Capture Log) NewFactory()
    {
        var capture = new Capture();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MEALIE_URL", "https://mealie.test");
            builder.UseSetting("MEALIE_TOKEN", "test-token");
            builder.UseSetting("APP_PASSWORD", "correct-horse-battery-staple");
            builder.UseSetting("DATA_DIR", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
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
