using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
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
    IHttpContextAccessor accessor,
    ILogger<PicnicClient> log)
{
    private const string Agent = "30100;1.236.1-15553;";

    // x-picnic-did comes from the TokenStore: random per install, persisted, and
    // stable across restarts because Picnic binds 2FA verification to it.

    // Null (the shared, legacy slot) unless OIDC is enabled, in which case every
    // signed-in identity -- OIDC or the /login/admin panic fallback -- gets its
    // own TokenStore slot. See UserKey and TokenStore for the storage layout.
    private string? UserKey =>
        options.OidcEnabled ? MealiePicnic.UserKey.From(accessor.HttpContext?.User ?? new()) : null;

    public bool HasToken => tokens.Token(UserKey) is { Length: > 0 };

    // ------------------------------------------------------------------ plumbing

    private HttpRequestMessage Build(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, $"{options.PicnicBaseUrl}{path}");
        request.Headers.TryAddWithoutValidation("User-Agent", "okhttp/4.9.0");
        request.Headers.TryAddWithoutValidation("Accept-Language",
            options.PicnicCountry.ToUpperInvariant() switch { "DE" => "de", "FR" => "fr", _ => "nl" });
        request.Headers.TryAddWithoutValidation("x-picnic-agent", Agent);
        request.Headers.TryAddWithoutValidation("x-picnic-did", tokens.DeviceId(UserKey));

        if (tokens.Token(UserKey) is { Length: > 0 } token)
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
            if (!string.IsNullOrWhiteSpace(fresh) && fresh != tokens.Token(UserKey))
                tokens.Save(fresh, UserKey);
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

    /// <summary>
    /// Read the product page for the facts search results do not carry: organic
    /// claim and salt per 100 g (issue #6).
    ///
    /// The page is a PML layout tree, so this looks up the blocks that belong to
    /// THIS product by their node ids and reads only those. That scoping is the
    /// point: the same page also carries "similar products", and a nearby organic
    /// alternative must not lend its leaf to a conventional product. When Picnic
    /// renames those ids the result is empty facts, never wrong ones.
    /// </summary>
    public async Task<PicnicDetails> GetDetailsAsync(string productId, CancellationToken ct)
    {
        var key = $"details:{productId}";
        if (cache.TryGetValue(key, out PicnicDetails? hit) && hit is not null)
            return hit;

        // Same rule as the image id: never build a path from an unvalidated value.
        if (!System.Text.RegularExpressions.Regex.IsMatch(productId, "^[A-Za-z0-9_-]{1,32}$"))
            throw new ArgumentException("Invalid product id.", nameof(productId));

        var path = $"/pages/product-details-page-root?id={Uri.EscapeDataString(productId)}";
        var json = await SendAsync(Build(HttpMethod.Get, path), ct);

        var details = json is null ? new PicnicDetails(productId, false, null, null)
                                   : ReadDetails(productId, json, log);

        cache.Set(key, details, new MemoryCacheEntryOptions
        {
            // Nutrition and certification do not change between deliveries.
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12),
            Size = 1024,
        });
        return details;
    }

    /// <summary>Parse a product page. Static and internal so tests can feed it a tree.</summary>
    internal static PicnicDetails ReadDetails(string productId, JsonNode page, ILogger log)
    {
        // Blocks belonging to the product itself, in the order the app shows them.
        var own = new[]
        {
            "product-details-page-root-main-container",   // name, brand, unit
            "product-page-highlights",
            "description",
        };

        var claims = new List<string>();
        var found = false;
        foreach (var id in own)
        {
            if (FindById(page, id) is not { } node) continue;
            found = true;
            Markdowns(node, claims);
        }

        // The accordion holds ingredients, nutrition and extra information. Its
        // nutrition item is where the salt is; the rest still counts as claim text
        // ("biologische tarwebloem" in an ingredient list means what it says).
        var nutrition = new List<string>();
        if (FindById(page, "accordion-list") is { } accordion)
        {
            found = true;
            foreach (var (title, body) in Sections(accordion))
            {
                if (title.Contains("voedingswaarde", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("nutrition", StringComparison.OrdinalIgnoreCase))
                    nutrition.AddRange(body);
                else
                    claims.AddRange(body);
            }
        }

        if (!found)
        {
            // Worth a warning: it means Picnic changed their page and both features
            // are silently off until the node ids here are updated.
            log.LogWarning("Product page for {Id} had none of the expected blocks", productId);
            return new PicnicDetails(productId, false, null, null);
        }

        var salt = ProductFacts.ParseSalt(nutrition);
        return new PicnicDetails(
            Id: productId,
            Organic: ProductFacts.IsOrganic(claims),
            SaltGramsPer100: salt?.Grams,
            SaltText: salt?.Text);
    }

    /// <summary>Depth-first search for the node carrying a given "id".</summary>
    private static JsonNode? FindById(JsonNode node, string id)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["id"]?.GetValue<string>() == id) return obj;
                foreach (var pair in obj)
                    if (pair.Value is not null && FindById(pair.Value, id) is { } inObject)
                        return inObject;
                break;

            case JsonArray array:
                foreach (var child in array)
                    if (child is not null && FindById(child, id) is { } inArray)
                        return inArray;
                break;
        }
        return null;
    }

    /// <summary>Every "markdown" string under a node, in document order.</summary>
    private static void Markdowns(JsonNode node, List<string> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (pair.Key == "markdown" && pair.Value is JsonValue value &&
                        value.TryGetValue<string>(out var text))
                        into.Add(text);
                    else if (pair.Value is not null)
                        Markdowns(pair.Value, into);
                }
                break;

            case JsonArray array:
                foreach (var child in array)
                    if (child is not null) Markdowns(child, into);
                break;
        }
    }

    /// <summary>The accordion's (header, body-text) pairs.</summary>
    private static IEnumerable<(string Title, List<string> Body)> Sections(JsonNode accordion)
    {
        var items = FindItems(accordion);
        if (items is null) yield break;

        foreach (var item in items)
        {
            if (item is not JsonObject obj) continue;

            var header = new List<string>();
            if (obj["header"] is { } h) Markdowns(h, header);

            var body = new List<string>();
            if (obj["body"] is { } b) Markdowns(b, body);

            yield return (ProductFacts.Clean(header.FirstOrDefault()), body);
        }
    }

    private static JsonArray? FindItems(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["items"] is JsonArray items) return items;
                foreach (var pair in obj)
                    if (pair.Value is not null && FindItems(pair.Value) is { } inObject)
                        return inObject;
                break;

            case JsonArray array:
                foreach (var child in array)
                    if (child is not null && FindItems(child) is { } inArray)
                        return inArray;
                break;
        }
        return null;
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

        tokens.Clear(UserKey);
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
