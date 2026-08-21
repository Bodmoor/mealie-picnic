using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;

namespace MealiePicnic;

/// <summary>
/// Unofficial Picnic storefront API. Notes on the endpoints, since they are undocumented:
///
///   POST /user/login              body {key, secret=md5(password), client_id:30100}
///                                 token arrives in the x-picnic-auth RESPONSE header
///   POST /user/2fa/generate       body {channel:"SMS"|"EMAIL"}
///   POST /user/2fa/verify         body {otp}; returns 204 + a NEW x-picnic-auth header
///   GET  /pages/search-page-results?search_term=  a layout tree; products are the
///                                 "sellingUnit" objects, already in relevance order
///   GET  /pages/product-details-page-root?id=     full detail page for one product
///   POST /cart/add_product        body {product_id, count}
///
/// x-picnic-did must stay identical across calls: 2FA verification is bound to it.
/// </summary>
public sealed class PicnicClient(
    HttpClient http,
    AppOptions options,
    TokenStore tokens,
    IMemoryCache cache,
    ILogger<PicnicClient> log)
{
    private const string Agent = "30100;1.236.1-15553;";

    // x-picnic-did comes from the TokenStore: random per install, persisted, and
    // stable across restarts because Picnic binds 2FA verification to it.

    public bool HasToken => tokens.Token is { Length: > 0 };

    // ------------------------------------------------------------------ plumbing

    private HttpRequestMessage Build(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{options.PicnicBaseUrl}{path}");
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.9.0");
        request.Headers.TryAddWithoutValidation("Accept-Language",
            options.PicnicCountry.ToUpperInvariant() switch { "DE" => "de", "FR" => "fr", _ => "nl" });
        request.Headers.TryAddWithoutValidation("x-picnic-agent", Agent);
        request.Headers.TryAddWithoutValidation("x-picnic-did", tokens.DeviceId);

        if (tokens.Token is { Length: > 0 } token)
            request.Headers.TryAddWithoutValidation("x-picnic-auth", token);

        if (body is not null)
            request.Content = JsonContent.Create(body);

        return request;
    }

    private void CaptureToken(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-picnic-auth", out var values))
        {
            var fresh = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(fresh) && fresh != tokens.Token)
                tokens.Save(fresh);
        }
    }

    private async Task<JsonNode?> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct);
        CaptureToken(response);

        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
            throw new PicnicAuthException(
                "Picnic rejected the token (403/401). Log in again, and complete 2FA if asked.");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Picnic {(int)response.StatusCode} on {request.RequestUri?.PathAndQuery}: {body}");

        return body.Length == 0 ? null : JsonNode.Parse(body);
    }

    // ------------------------------------------------------------------ auth

    /// <summary>Is a username available from configuration (user secrets / env)?</summary>
    public bool HasConfiguredUser => options.PicnicUser.Length > 0;

    /// <summary>Is a password available from configuration (user secrets / env)?</summary>
    public bool HasConfiguredPassword => options.PicnicPassword.Length > 0;

    /// <summary>The configured username, safe to show in the UI so it can be prefilled.</summary>
    public string ConfiguredUser => options.PicnicUser;

    /// <summary>True when neither configuration nor a cached token can get us in.</summary>
    public bool NeedsCredentials => !HasConfiguredUser || !HasConfiguredPassword;

    /// <summary>
    /// Log in. Arguments win over the environment, so PICNIC_USER / PICNIC_PASSWORD
    /// can be left unset in development and typed into the UI instead. Nothing is
    /// stored: only the resulting auth token is persisted.
    /// </summary>
    /// <returns>true when a second factor is still required.</returns>
    public async Task<bool> LoginAsync(string? user, string? password, CancellationToken ct)
    {
        var key = string.IsNullOrWhiteSpace(user) ? options.PicnicUser : user.Trim();
        var secretSource = string.IsNullOrEmpty(password) ? options.PicnicPassword : password;

        if (key.Length == 0 || secretSource.Length == 0)
            throw new PicnicCredentialsException(
                "No Picnic credentials. Set PICNIC_USER / PICNIC_PASSWORD, or enter them in the UI.");

        var secret = Convert.ToHexStringLower(
            MD5.HashData(Encoding.UTF8.GetBytes(secretSource)));

        var request = Build(HttpMethod.Post, "/user/login", new
        {
            key,
            secret,
            client_id = 30100,
        });
        // A stale token must not ride along on a fresh login.
        request.Headers.Remove("x-picnic-auth");

        var json = await SendAsync(request, ct);

        // Picnic answers bad credentials with HTTP 200 and an error body, so the
        // status code alone is not enough to tell whether the login worked.
        if (json?["error"]?["code"]?.GetValue<string>() is { Length: > 0 } code)
            throw new PicnicAuthException($"Picnic login refused ({code}). Check PICNIC_USER / PICNIC_PASSWORD.");

        var needs2fa = json?["second_factor_authentication_required"]?.GetValue<bool>() ?? false;

        log.LogInformation("Picnic login ok, 2FA required: {Needs2fa}", needs2fa);
        return needs2fa;
    }

    public Task Generate2faAsync(string channel, CancellationToken ct) =>
        SendAsync(Build(HttpMethod.Post, "/user/2fa/generate", new { channel }), ct);

    public Task Verify2faAsync(string code, CancellationToken ct) =>
        SendAsync(Build(HttpMethod.Post, "/user/2fa/verify", new { otp = code }), ct);

    /// <summary>Cheap call to prove the cached token still works.</summary>
    public async Task<bool> IsUsableAsync(CancellationToken ct)
    {
        if (!HasToken) return false;
        try
        {
            await SendAsync(Build(HttpMethod.Get, "/user"), ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // "Is the token usable" is false on ANY failure -- an unreachable
            // Picnic must degrade the status line, not 500 the status endpoint.
            if (ex is not PicnicAuthException)
                log.LogWarning(ex, "Picnic status probe failed");
            return false;
        }
    }

    // ------------------------------------------------------------------ catalog

    public async Task<List<PicnicProduct>> SearchAsync(string term, CancellationToken ct)
    {
        var key = $"search:{term.ToLowerInvariant()}";
        if (cache.TryGetValue(key, out List<PicnicProduct>? hit) && hit is not null)
            return hit;

        var path = $"/pages/search-page-results?search_term={Uri.EscapeDataString(term)}";
        var json = await SendAsync(Build(HttpMethod.Get, path), ct);

        var products = new List<PicnicProduct>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (json is not null)
            Collect(json, products, seen);

        cache.Set(key, products, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(options.SearchCacheMinutes),
            Size = 64 * 1024,          // nominal: a parsed result list is small
        });
        log.LogInformation("Search '{Term}' -> {Count} products", term, products.Count);
        return products;
    }

    /// <summary>
    /// Walk the layout tree and pick up every "sellingUnit" object.
    /// Document order is Picnic's own relevance ranking, so it is preserved.
    /// </summary>
    private static void Collect(JsonNode node, List<PicnicProduct> into, HashSet<string> seen)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["sellingUnit"] is JsonObject unit &&
                    unit["id"]?.GetValue<string>() is { Length: > 0 } id &&
                    seen.Add(id))
                {
                    into.Add(new PicnicProduct(
                        Id: id,
                        Name: unit["name"]?.GetValue<string>() ?? "?",
                        DisplayPrice: unit["display_price"]?.GetValue<int>(),
                        UnitQuantity: unit["unit_quantity"]?.GetValue<string>() ?? "",
                        ImageId: unit["image_id"]?.GetValue<string>() ?? ""));
                }
                foreach (var pair in obj)
                    if (pair.Value is not null) Collect(pair.Value, into, seen);
                break;

            case JsonArray array:
                foreach (var child in array)
                    if (child is not null) Collect(child, into, seen);
                break;
        }
    }

    public async Task<byte[]> GetImageAsync(string imageId, string size, CancellationToken ct)
    {
        var key = $"img:{imageId}:{size}";
        if (cache.TryGetValue(key, out byte[]? hit) && hit is not null)
            return hit;

        // Defense in depth: the endpoint validates too, but this client must never
        // build a path from something that can contain '/' or '..'.
        if (!System.Text.RegularExpressions.Regex.IsMatch(imageId, "^[A-Za-z0-9_-]{1,128}$"))
            throw new ArgumentException("Invalid image id.", nameof(imageId));
        if (size is not ("small" or "medium" or "large"))
            throw new ArgumentException("Invalid image size.", nameof(size));

        var url = $"{options.PicnicImageBaseUrl}/{imageId}/{size}.png";
        var bytes = await http.GetByteArrayAsync(url, ct);
        cache.Set(key, bytes, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(4),
            Size = bytes.Length,       // real bytes: images dominate the cache
        });
        return bytes;
    }

    // ------------------------------------------------------------------ cart

    /// <summary>
    /// Add a product and CONFIRM it arrived.
    ///
    /// Picnic answers some refusals with HTTP 200 and an error object (the same
    /// pattern as /user/login), and add_product returns the updated cart. Taking
    /// 2xx as success meant a refused add looked fine, and the caller then ticked
    /// the item off in Mealie with nothing in the basket (issue #7).
    /// </summary>
    public async Task AddToCartAsync(string productId, int count, CancellationToken ct)
    {
        var cart = await SendAsync(Build(HttpMethod.Post, "/cart/add_product",
            new { product_id = productId, count }), ct);

        if (cart?["error"] is { } error)
        {
            var code = error["code"]?.GetValue<string>() ?? "unknown";
            throw new PicnicCartException($"Picnic refused to add {productId} ({code}).");
        }

        // The response is the new cart; if the product is not in it, the add did
        // not take effect however cheerful the status code was.
        if (cart is not null && !ContainsProduct(cart, productId))
            throw new PicnicCartException(
                $"Picnic accepted the request but {productId} is not in the cart.");
    }

    /// <summary>Depth-first search for a selling unit id anywhere in the cart tree.</summary>
    private static bool ContainsProduct(JsonNode node, string productId)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["id"]?.GetValue<string>() == productId)
                    return true;
                foreach (var pair in obj)
                    if (pair.Value is not null && ContainsProduct(pair.Value, productId))
                        return true;
                return false;

            case JsonArray array:
                foreach (var child in array)
                    if (child is not null && ContainsProduct(child, productId))
                        return true;
                return false;

            default:
                return false;
        }
    }

    public Task<JsonNode?> GetCartAsync(CancellationToken ct) =>
        SendAsync(Build(HttpMethod.Get, "/cart"), ct);

    // ------------------------------------------------------------------ logout

    /// <summary>
    /// Invalidate the session at Picnic and forget the cached token. The remote call
    /// is best-effort: if it fails the local token is dropped regardless, otherwise a
    /// broken token could never be cleared from the UI.
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct)
    {
        if (HasToken)
        {
            try
            {
                await SendAsync(Build(HttpMethod.Post, "/user/logout"), ct);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Picnic logout call failed; clearing the local token anyway");
            }
        }

        tokens.Clear();
        log.LogInformation("Picnic token cleared");
    }
}

public sealed class PicnicAuthException(string message) : Exception(message);

/// <summary>Raised when no credentials are available to log in with at all.</summary>
public sealed class PicnicCredentialsException(string message) : Exception(message);

/// <summary>
/// Raised when Picnic accepted the request but the cart did not change, or
/// returned an error body under a 2xx. Callers must NOT treat this as success:
/// ticking the Mealie item off would lose the line silently.
/// </summary>
public sealed class PicnicCartException(string message) : Exception(message);
