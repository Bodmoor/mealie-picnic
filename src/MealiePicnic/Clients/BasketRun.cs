using System.Text.Json.Nodes;

namespace MealiePicnic.Clients;

/// <summary>
/// One "add everything to the Picnic basket" run (issue #62).
///
/// Extracted from the endpoint it used to live inside, unchanged, because this
/// is the only path in the app that spends money and it was the only one that
/// needed a WebApplicationFactory and two stubbed upstreams to reach. Four
/// behaviours in here are worth pinning and none of them was:
///
/// <list type="bullet">
/// <item>two Mealie items linked to the same Picnic product are summed into one
/// basket line rather than sent twice (issue #27)</item>
/// <item>an item counts as added only if it is in the cart Picnic returns, not
/// because the call answered 200 (issue #7)</item>
/// <item>a failed Mealie check-off leaves a successful add reported as
/// successful -- the goods are in the basket, and a line marked failed invites a
/// retry and a double order</item>
/// <item>an auth failure and a cancellation are rethrown, never folded into a
/// per-line error: the first becomes the login dialog, the second must stop the
/// run</item>
/// </list>
/// </summary>
public sealed class BasketRun(PicnicClient picnic, MealieClient mealie, ILogger<BasketRun> log)
{
    public async Task<BasketOutcome> ExecuteAsync(
        IReadOnlyList<ShoppingItem> linked, bool checkOff, CancellationToken ct)
    {
        // One request for the whole basket (issue #27), keyed by Picnic product
        // id: two Mealie items can link to the same product, so their amounts are
        // summed rather than sent as separate lines.
        var quantities = new Dictionary<string, int>();
        foreach (var item in linked)
            quantities[item.PicnicUid!] = quantities.GetValueOrDefault(item.PicnicUid!) + item.Amount;

        if (quantities.Count == 0)
            return new BasketOutcome([], Aborted: false);

        JsonNode? cart = null;
        var aborted = false;
        var abortReason = "";

        try
        {
            cart = await picnic.AddProductsToCartAsync(quantities, ct);
        }
        // These must not be swallowed: the 401 middleware turns the auth failure
        // into the login dialog, and cancellation must stop here.
        catch (PicnicAuthException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Full detail to the log; only a short, stable message to the client
            // (upstream error bodies can contain internal paths and payloads).
            log.LogWarning(ex, "Batch basket add failed for {Count} products", quantities.Count);
            aborted = true;
            abortReason = ex is HttpRequestException
                ? AppText.Current.BasketUpstreamFailed
                : AppText.Current.BasketInternalError;
        }

        var results = new List<CartResult>();
        foreach (var item in linked)
        {
            ct.ThrowIfCancellationRequested();

            // Verifiably in the cart, not just a cheerful status code (issue #7):
            // still true for the batch call, which answers with the updated cart.
            if (aborted || cart is null || !PicnicClient.CartHasProduct(cart, item.PicnicUid!))
            {
                results.Add(new CartResult(item.FoodName, item.PicnicUid!, item.Amount, false,
                    aborted ? abortReason : AppText.Current.BasketPicnicRefused));
                continue;
            }

            var checkedOff = false;
            if (checkOff)
            {
                try
                {
                    await mealie.CheckItemAsync(item.ItemId, ct);
                    checkedOff = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // The basket add DID succeed. Reporting this as a failed line
                    // would invite a retry and a double order, so it is recorded as
                    // a successful add that is still on the Mealie list.
                    log.LogWarning(ex, "Checked off failed for {Food} ({ItemId})",
                        item.FoodName, item.ItemId);
                }
            }

            results.Add(new CartResult(
                item.FoodName, item.PicnicUid!, item.Amount, true, null, checkedOff));
        }

        return new BasketOutcome(results, aborted);
    }
}

/// <summary>What a run produced, in the shape BasketLog already renders.</summary>
public sealed record BasketOutcome(List<CartResult> Results, bool Aborted);
