using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MealiePicnic;
using Microsoft.AspNetCore.Authentication;          // SignInAsync / SignOutAsync
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

var options = AppOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddMemoryCache();

builder.Services.AddHttpClient<MealieClient>();
builder.Services.AddHttpClient<PicnicClient>();

// Single-password cookie auth in front of everything. Simple on purpose: this app
// holds credentials for a supermarket account, so it must not be open.
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.LoginPath = "/login";
        cookie.LogoutPath = "/logout";
        cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
        cookie.SlidingExpiration = true;
        cookie.Cookie.Name = "mealiepicnic";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Turn a Picnic auth failure into a 401 the UI can react to, instead of a 500.
// The browser then knows to open the login / 2FA dialog rather than showing a stack trace.
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (PicnicAuthException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await ctx.Response.WriteAsJsonAsync(new { error = "picnic_auth", message = ex.Message });
    }
    catch (PicnicCredentialsException ex)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsJsonAsync(new { error = "picnic_credentials", message = ex.Message });
    }
});

// Static files (the SPA) are behind auth too, hence no UseStaticFiles() before this.
app.MapGet("/login", () => Results.Content(Html.LoginPage, "text/html"))
   .AllowAnonymous();

app.MapPost("/login", async (HttpContext ctx, AppOptions opt) =>
{
    var form = await ctx.Request.ReadFormAsync();
    var supplied = form["password"].ToString();

    // Hash both sides so the comparison is fixed-length and fixed-time.
    static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
    var ok = CryptographicOperations.FixedTimeEquals(Hash(supplied), Hash(opt.AppPassword));

    if (!ok)
        return Results.Content(Html.LoginPage.Replace("<!--ERROR-->",
            "<p class=\"err\">Onjuist wachtwoord</p>"), "text/html");

    var identity = new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, "owner")],
        CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(identity));

    return Results.Redirect("/");
}).AllowAnonymous();

app.MapGet("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/", () => Results.Content(Html.AppPage, "text/html")).RequireAuthorization();

var api = app.MapGroup("/api").RequireAuthorization();

// ----------------------------------------------------------------- picnic auth

api.MapGet("/picnic/status", async (PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(new
    {
        authenticated = await picnic.IsUsableAsync(ct),
        // When true the UI asks for user/password instead of relying on env vars.
        needsCredentials = picnic.NeedsCredentials,
    }));

api.MapPost("/picnic/login", async (LoginRequest? body, PicnicClient picnic, CancellationToken ct) =>
{
    var needs2fa = await picnic.LoginAsync(body?.User, body?.Password, ct);
    return Results.Ok(new { needs2fa });
});

api.MapPost("/picnic/2fa/generate", async (TwoFactorChannel body, PicnicClient picnic, CancellationToken ct) =>
{
    await picnic.Generate2faAsync(string.IsNullOrWhiteSpace(body.Channel) ? "EMAIL" : body.Channel, ct);
    return Results.Ok(new { sent = true });
});

api.MapPost("/picnic/2fa/verify", async (Otp body, PicnicClient picnic, CancellationToken ct) =>
{
    await picnic.Verify2faAsync(body.Code, ct);
    return Results.Ok(new { verified = true });
});

// ----------------------------------------------------------------- shopping list

api.MapGet("/list", async (MealieClient mealie, CancellationToken ct) =>
    Results.Ok(await mealie.GetItemsAsync(ct)));

api.MapGet("/search", async (string term, PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(await picnic.SearchAsync(term, ct)));

api.MapGet("/product/{id}", async (string id, PicnicClient picnic, CancellationToken ct) =>
{
    var page = await picnic.GetProductPageAsync(id, ct);
    return Results.Ok(new { id, raw = page });
});

api.MapGet("/image/{imageId}", async (string imageId, PicnicClient picnic, CancellationToken ct) =>
{
    var bytes = await picnic.GetImageAsync(imageId, "medium", ct);
    return Results.File(bytes, "image/png");
});

// ----------------------------------------------------------------- mapping

api.MapPost("/link", async (LinkRequest body, MealieClient mealie, CancellationToken ct) =>
{
    var extras = await mealie.SetFoodExtrasAsync(body.FoodId, new Dictionary<string, string>
    {
        [MealieClient.ExtraUid] = body.PicnicUid,
        [MealieClient.ExtraFlag] = "true",
        [MealieClient.ExtraLabel] = body.Label ?? "",
    }, ct);
    return Results.Ok(new { ok = true, extras });
});

api.MapPost("/exclude", async (FoodRef body, MealieClient mealie, CancellationToken ct) =>
{
    var extras = await mealie.SetFoodExtrasAsync(body.FoodId, new Dictionary<string, string>
    {
        [MealieClient.ExtraFlag] = "false",
    }, ct);
    return Results.Ok(new { ok = true, extras });
});

// Revert an exclusion: clear the flag so the item shows up as 'new' again.
api.MapPost("/include", async (FoodRef body, MealieClient mealie, CancellationToken ct) =>
{
    var extras = await mealie.SetFoodExtrasAsync(body.FoodId, new Dictionary<string, string>
    {
        [MealieClient.ExtraFlag] = "",
    }, ct);
    return Results.Ok(new { ok = true, extras });
});

// ----------------------------------------------------------------- basket

api.MapPost("/basket", async (BasketRequest body, MealieClient mealie, PicnicClient picnic,
                              CancellationToken ct) =>
{
    var items = await mealie.GetItemsAsync(ct);
    var results = new List<CartResult>();

    foreach (var item in items.Where(i => i.State == LinkState.Linked && !i.Checked))
    {
        try
        {
            await picnic.AddToCartAsync(item.PicnicUid!, item.Amount, ct);
            results.Add(new CartResult(item.FoodName, item.PicnicUid!, item.Amount, true, null));

            if (body.CheckOff)
                await mealie.CheckItemAsync(item.ItemId, ct);
        }
        catch (Exception ex)
        {
            results.Add(new CartResult(item.FoodName, item.PicnicUid!, item.Amount, false, ex.Message));
        }
    }

    var skipped = items.Count(i => i.State == LinkState.New);
    return Results.Ok(new { results, unmapped = skipped });
});

api.MapGet("/cart", async (PicnicClient picnic, CancellationToken ct) =>
    Results.Ok(await picnic.GetCartAsync(ct)));

app.Run();

// ----------------------------------------------------------------- dto records

// NB: a record must not have a member with the same name as the type,
// so this is TwoFactorChannel rather than Channel.
internal record TwoFactorChannel(string Channel);
internal record Otp(string Code);
internal record FoodRef(string FoodId);
internal record LinkRequest(string FoodId, string PicnicUid, string? Label);
internal record BasketRequest(bool CheckOff);
internal record LoginRequest(string? User, string? Password);
