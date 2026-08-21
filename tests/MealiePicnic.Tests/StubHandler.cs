using System.Net;
using System.Text;

namespace MealiePicnic.Tests;

/// <summary>
/// Fake HTTP transport. Routes on a substring of the request URI so tests can
/// script both the Mealie and Picnic APIs without a network.
/// </summary>
internal class StubHandler : HttpMessageHandler
{
    private readonly List<(string Match, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = [];

    /// <summary>Every request that went through, in order. Bodies are captured eagerly.</summary>
    public List<(HttpMethod Method, string Url, string Body)> Sent { get; } = [];

    public StubHandler On(string uriContains, Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        _routes.Add((uriContains, reply));
        return this;
    }

    public StubHandler OnJson(string uriContains, string json, HttpStatusCode status = HttpStatusCode.OK) =>
        On(uriContains, _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        });

    public StubHandler OnStatus(string uriContains, HttpStatusCode status) =>
        On(uriContains, _ => new HttpResponseMessage(status) { Content = new StringContent("") });

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        var body = request.Content is null
            ? ""
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Sent.Add((request.Method, url, body));

        // Last matching route wins, so a test can override a default.
        for (var i = _routes.Count - 1; i >= 0; i--)
            if (url.Contains(_routes[i].Match, StringComparison.OrdinalIgnoreCase))
                return _routes[i].Reply(request);

        throw new InvalidOperationException($"No stub route matches {url}");
    }
}
