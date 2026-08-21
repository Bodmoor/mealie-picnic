# Mealie → Picnic

Links Mealie shopping list items to Picnic products, and fills the Picnic basket in one click.

The mapping lives on the Mealie **food** (not the recipe, not the list item), so it is
reused by every recipe that uses that ingredient:

| extras key     | meaning                                          |
| -------------- | ------------------------------------------------ |
| `picnic_uid`   | Picnic selling-unit id, e.g. `s1005080`          |
| `picnic`       | `"true"` linked · `"false"` never buy at Picnic   |
| `picnic_label` | product name at time of linking, for diagnostics |

Items with no picnic extras at all are shown first as **nieuw**. Items with
`picnic == "false"` are hidden behind a toggle, where the setting can be reverted.

## Run

```bash
cp .env.example .env      # fill in the values
docker compose up -d --build
```

Open <http://localhost:8080>, log in with `APP_PASSWORD`, then press **Picnic login**.
The first login triggers 2FA: pick SMS or e-mail, type the code, done. The resulting
token is cached in `./data/picnic-token` and is valid for months, so this is rare.

## Local development

Configuration is read in the standard ASP.NET Core order:

```
appsettings.json  <  user secrets (Development only)  <  environment variables
```

So the same flat keys work in Docker (env vars) and locally (user secrets), and no
secret needs to live in `launchSettings.json` — which is easy to commit by accident.

```bash
cd src/MealiePicnic
dotnet user-secrets set "MEALIE_TOKEN"    "paste-a-mealie-api-token"
dotnet user-secrets set "PICNIC_USER"     "you@example.com"
dotnet user-secrets set "PICNIC_PASSWORD" "your-picnic-password"
dotnet user-secrets list
```

These land in `%APPDATA%\Microsoft\UserSecrets\mealie-picnic\secrets.json`, outside
the repository. The `UserSecretsId` is already set in `MealiePicnic.csproj`.

**Environment variables win over user secrets.** A key left in `launchSettings.json`
silently shadows the secret, even when set to an empty string — remove it there rather
than blanking it. `launchSettings.json` is gitignored; `launchSettings.example.json`
shows the non-secret keys that belong in it.

The Picnic credentials are optional even as secrets: leave them unset and the web UI
asks for them at login instead. Only the resulting auth token is persisted, to `DATA_DIR`.

## Configuration

All via environment variables.

| Variable               | Required | Default        | Notes                                     |
| ---------------------- | -------- | -------------- | ----------------------------------------- |
| `APP_PASSWORD`         | yes      | —              | password for the web UI                   |
| `MEALIE_URL`           | yes      | —              | e.g. `https://mealie.local`               |
| `MEALIE_TOKEN`         | yes      | —              | Mealie → Profile → API Tokens; user secret locally |
| `MEALIE_LIST`          | no       | `Boodschappen` | shopping list name                        |
| `PICNIC_USER`          | no*      | —              | *needed until a token is cached           |
| `PICNIC_PASSWORD`      | no*      | —              |                                           |
| `PICNIC_COUNTRY`       | no       | `NL`           | `NL`, `DE`, `FR`                          |
| `PICNIC_API_VERSION`   | no       | `15`           | bump if Picnic starts returning 400s      |
| `SEARCH_CACHE_MINUTES` | no       | `30`           | in-memory cache for searches and details  |
| `DATA_DIR`             | no       | `/data`        | mounted volume for the token              |

## Tests

```bash
dotnet test
```

xUnit, no mocking framework. Both APIs are faked with a `StubHandler` that routes on a
URI substring and records every request, so the tests assert on the exact JSON bodies
sent to Mealie — which is where the sharp edges are:

* `MealieClientTests` — item classification by picnic extras, `New` sorted first,
  free-text notes skipped, and the regression that matters: `SetFoodExtras` must echo
  `aliases` back or a PUT destroys them.
* `PicnicClientTests` — selling units collected from the layout tree in relevance order
  and deduplicated, 403 mapped to `PicnicAuthException`, bad credentials returned as
  HTTP 200 with an error body still treated as failure, and the refreshed
  `x-picnic-auth` header captured after 2FA.
* `AppOptionsTests` — defaults, required keys, and blank values falling through.
* `TokenStoreTests` — the token survives a restart, so 2FA stays rare.

## How it works

Mealie is a normal REST API. Picnic is not: there is no official API, and the current
storefront returns a **layout tree** rather than a product list. Search products are the
`sellingUnit` objects scattered through that tree, and document order is Picnic's own
relevance ranking — so the app collects them in order and does no scoring of its own.

Notable endpoints (all undocumented, subject to change without notice):

```
POST /user/login          {key, secret: md5(password), client_id: 30100}
                          -> token in the x-picnic-auth RESPONSE header
POST /user/2fa/generate   {channel: "SMS"|"EMAIL"}
POST /user/2fa/verify     {otp}  -> 204 + a NEW x-picnic-auth header
GET  /pages/search-page-results?search_term=...
GET  /pages/product-details-page-root?id=s1005080
POST /cart/add_product    {product_id, count}
```

`x-picnic-did` must stay identical across calls — 2FA verification is bound to it.

### Two Mealie gotchas baked in

* There is **no PATCH** on `/api/foods/{id}`. PUT replaces the whole object, so any
  omitted field is destroyed — `aliases` in particular. The client reads the food,
  merges extras, and echoes everything back.
* Food `extras` are API-only; Mealie's UI has no editor for them (only *recipe* extras
  have a UI panel, which is the wrong level and isn't readable per ingredient).

## Caveats

* Everything Picnic-side is unofficial and can break when they change their app.
* Quantities: Mealie items added without an amount have `quantity: 0`, so the app adds
  1 of each. Pack sizes are not reconciled — 300 g of flour and a 1 kg bag both count as one.
* Auth is a single shared password. Fine behind your own network; put a reverse proxy
  with TLS in front of it if you expose it.
