using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MealiePicnic.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace MealiePicnic.Clients;

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
///   POST /cart/products/add       body {product_id: count, ...}  -- batch add
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
        options.OidcEnabled ? Storage.UserKey.From(accessor.HttpContext?.User ?? new()) : null;

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

        var details = json is null ? new PicnicDetails(productId, false, null, null, [])
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
        // Kept apart as well as together (issue #48): the organic claim wants
        // every fragment in one list, while the detail view wants to show the
        // highlights and the description as the distinct blocks they are.
        var blocks = new Dictionary<string, List<string>>();
        foreach (var id in own)
        {
            if (FindById(page, id) is not { } node) continue;
            found = true;
            var block = new List<string>();
            Markdowns(node, block);
            blocks[id] = block;
            claims.AddRange(block);
        }

        // The accordion holds ingredients, nutrition and extra information. Its
        // nutrition item is where the salt is; the rest still counts as claim text
        // ("biologische tarwebloem" in an ingredient list means what it says).
        var nutrition = new List<string>();
        var accordionAll = new List<string>();
        var ingredients = new List<string>();
        var sections = new List<DetailSection>();
        var accordionSections = 0;
        if (FindById(page, "accordion-list") is { } accordion)
        {
            found = true;
            foreach (var (title, body) in Sections(accordion))
            {
                accordionSections++;
                accordionAll.AddRange(body);
                sections.Add(new DetailSection(title, body) { Ingredients = IsIngredientSection(title) });
                if (title.Contains("voedingswaarde", StringComparison.OrdinalIgnoreCase) ||
                    title.Contains("nutrition", StringComparison.OrdinalIgnoreCase))
                    nutrition.AddRange(body);
                else
                    claims.AddRange(body);

                // Allergens come from these sections alone (issue #45). The
                // accordion is not all ingredient text: it also carries a
                // description, and "heerlijk bij een glas melk" in prose put a
                // milk chip on a product whose ingredient list had no dairy in it.
                if (IsIngredientSection(title)) ingredients.AddRange(body);
            }
        }

        if (!found)
        {
            // Worth a warning: it means Picnic changed their page and both features
            // are silently off until the node ids here are updated.
            log.LogWarning("Product page for {Id} had none of the expected blocks", productId);
            return new PicnicDetails(productId, false, null, null, []);
        }

        // Falls back to every accordion section, not just the one titled
        // "Voedingswaarde"/"nutrition" (issue #33): that title match is Picnic's
        // wording, not ours, and a table sitting under a differently-worded or
        // untitled section must not read as "no salt" just because the header
        // didn't match. ParseSalt's own label regex ("zout"/"natrium" as a whole
        // word, immediately followed by a per-100g-shaped amount) is specific
        // enough that scanning ingredients/allergen text alongside it is safe.
        var salt = ProductFacts.ParseSalt(nutrition) ?? ProductFacts.ParseSalt(accordionAll);

        // Allergens come from the ingredient and allergy sections only -- not the
        // whole accordion, which is what #14 shipped and #45 corrects.
        //
        // Note this is the opposite choice from salt directly above, and
        // deliberately so. Salt widens to every section because a missed figure
        // is a small annoyance and ParseSalt's own label regex is specific enough
        // to be safe anywhere. An allergen chip is the other way round: under
        // positive-only display a chip that should not be there is a lie on the
        // card, so narrowing beats reaching. The cost is accepted: if Picnic
        // renames the section, allergens go quiet rather than wrong.
        //
        // Passed the raw fragments, before Clean() has run: the bold markers are
        // the whole signal separating a declared allergen from a word we spotted.
        if (accordionSections > 0 && ingredients.Count == 0)
        {
            // Debug, not a warning: plenty of products (loose produce, say) have
            // no ingredient section at all, so this is ordinary per product. It
            // would only be a signal in aggregate, and logging is the wrong
            // instrument for that -- the fixture tests are what guard the section
            // wordings we know about.
            log.LogDebug("Product page for {Id} had an accordion but no ingredient section", productId);
        }

        var main = blocks.GetValueOrDefault("product-details-page-root-main-container", []);

        return new PicnicDetails(
            Id: productId,
            Organic: ProductFacts.IsOrganic(claims),
            SaltGramsPer100: salt?.Grams,
            SaltText: salt?.Text,
            Allergens: ProductFacts.ReadAllergens(ingredients),
            // The first markdown of the main container is the product name; the
            // rest of that block is brand and unit, which the highlights repeat.
            Title: ProductFacts.Clean(main.FirstOrDefault()) is { Length: > 0 } name ? name : null,
            Highlights: blocks.GetValueOrDefault("product-page-highlights", []),
            Description: blocks.GetValueOrDefault("description", []),
            Sections: sections);
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

    /// <summary>
    /// Is this accordion section the ingredient list, or an allergy notice?
    ///
    /// Matched on stems rather than whole titles, because the wording is Picnic's
    /// and varies: "Ingredienten", "Ingrediënten" and "Allergie-informatie" have
    /// all been seen, and the stems cover the spelling with and without the
    /// diaeresis without a list of exact strings to keep in step.
    ///
    /// Kept deliberately narrow. A section this does not match contributes no
    /// allergens at all, which is the point: the description sits in this same
    /// accordion, and prose about what a product goes well with is not a
    /// declaration of what is in it (issue #45).
    /// </summary>
    public static bool IsIngredientSection(string title) =>
        title.Contains("ingredi", StringComparison.OrdinalIgnoreCase) ||
        title.Contains("allerg", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Add multiple products to the cart in a single request (issue #27). The
    /// caller used to add a whole basket by calling <see cref="AddToCartAsync"/>
    /// once per linked item, paced 250 ms apart to be gentle on an undocumented
    /// API -- dozens of round trips for a full shopping list. Picnic's own app
    /// uses this same batch endpoint (e.g. re-ordering a previous order), so one
    /// request for the whole basket is the intended shape, not a workaround.
    ///
    /// Unlike <see cref="AddToCartAsync"/> this does not throw when a product was
    /// refused: a partial failure (some products added, others not) must not
    /// fail the ones that worked, so the caller checks the returned cart itself
    /// with <see cref="CartHasProduct"/>, product by product.
    /// </summary>
    public Task<JsonNode?> AddProductsToCartAsync(IReadOnlyDictionary<string, int> quantities, CancellationToken ct) =>
        SendAsync(Build(HttpMethod.Post, "/cart/products/add", quantities), ct);

    /// <summary>Is this product verifiably present in a cart returned by the API?</summary>
    public static bool CartHasProduct(JsonNode cart, string productId) => ContainsProduct(cart, productId);

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
