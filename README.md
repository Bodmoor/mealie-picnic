# Mealie → Picnic

***DISCLAIMER: This project is largely vibecoded***

Links Mealie shopping list items to Picnic products, and fills the Picnic basket in one click.

The mapping lives on the Mealie **food** (not the recipe, not the list item), so it is
reused by every recipe that uses that ingredient:

| extras key     | meaning                                          |
| -------------- | ------------------------------------------------ |
| `picnic_uid`   | Picnic selling-unit id, e.g. `s1005080`          |
| `picnic`       | `"true"` linked · `"false"` never buy at Picnic   |
| `picnic_label` | product name at time of linking, for diagnostics |
| `picnic_pack`  | Picnic's pack size at linking, e.g. `1 kg` or `6 stuks`   |

Items with no picnic extras at all are shown first as **nieuw**. Items with
`picnic == "false"` are hidden behind a toggle, where the setting can be reverted.

## Run

### From the published image (GHCR)

Every merge to `main` publishes `ghcr.io/bodmoor/mealie-picnic:latest` (plus `:main`,
and `:x.y.z` for `v*.*.*` tags), for **linux/amd64 and linux/arm64**. While the repository is private the package is too,
so the Docker host needs a login with a `read:packages` PAT:

```bash
cp .env.example .env                                   # fill in the values
echo $GHCR_PAT | docker login ghcr.io -u <github-user> --password-stdin
docker compose -f docker-compose.deploy.yml pull
docker compose -f docker-compose.deploy.yml up -d
```

Pin a version instead of tracking `latest` with `TAG=0.1.0` in `.env`.

### Building locally

```bash
cp .env.example .env
docker compose up -d --build
```

Open <http://localhost:8080>, log in with `APP_PASSWORD`, then press **Picnic login**.
The first login triggers 2FA: pick SMS or e-mail, type the code, done. The resulting
token is cached in `DATA_DIR` (the `picnic-data` volume under Docker) and is valid for months, so this is rare.

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
than blanking it. `launchSettings.json` is gitignored.

The Picnic credentials are optional even as secrets: leave them unset and the web UI
asks for them at login instead. Only the resulting auth token is persisted, to `DATA_DIR`.

## Configuration

All via environment variables.

| Variable               | Required | Default        | Notes                                     |
| ---------------------- | -------- | -------------- | ----------------------------------------- |
| `APP_PASSWORD`         | yes*     | —              | *not required once `OIDC_AUTHORITY` is set; still works there as a break-glass fallback at `/login/admin` (unlinked from the UI) |
| `OIDC_AUTHORITY`       | no       | —              | Authentik issuer URL; setting this turns OIDC login on |
| `OIDC_CLIENT_ID`       | no*      | —              | *required once `OIDC_AUTHORITY` is set     |
| `OIDC_CLIENT_SECRET`   | no*      | —              | *required once `OIDC_AUTHORITY` is set; confidential client |
| `MEALIE_URL`           | yes      | —              | e.g. `https://mealie.local`               |
| `MEALIE_TOKEN`         | yes      | —              | Mealie → Profile → API Tokens; user secret locally |
| `MEALIE_LIST`          | no       | `Boodschappen` | *default* list; the UI has a picker and remembers your choice |
| `PICNIC_USER`          | no*      | —              | *needed until a token is cached           |
| `PICNIC_PASSWORD`      | no*      | —              |                                           |
| `PICNIC_COUNTRY`       | no       | `NL`           | `NL`, `DE`, `FR`                          |
| `PICNIC_API_VERSION`   | no       | `15`           | bump if Picnic starts returning 400s      |
| `SEARCH_CACHE_MINUTES` | no       | `30`           | in-memory cache for searches; product pages are held 12 h |
| `DATA_DIR`             | no       | `/data`        | volume for the token and device id        |
| `COOKIE_SECURE`        | no       | `false`        | force Secure on the session cookie (needs TLS) |
| `TRUST_PROXY`          | no       | `false`        | honour X-Forwarded-* from a reverse proxy |

## Icons

The three PNGs and `eu-organic.svg` in `src/MealiePicnic/assets/` are
compiled into the assembly as embedded resources, so there is no `wwwroot` to deploy.
Replace the files to change the icon; the maskable one needs its art inset by about
20% or Android's circular crop clips it.

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
* `ProductFactsTests` — the organic and salt readers, weighted towards the false
  positives: "biologisch afbreekbaar" is biodegradable packaging, not organic food,
  and a "per 100 g" heading is not a salt figure.
* `AppOptionsTests` — defaults, required keys, blank values falling through, and the
  OIDC-vs-`APP_PASSWORD` required-key interplay.
* `TokenStoreTests` — the token survives a restart, so 2FA stays rare; also that a
  `null` key keeps writing the original flat layout, and different keys get isolated
  `DATA_DIR/users/{key}/` slots.
* `UserKeyTests` — the OIDC-`sub`-to-storage-key hash is stable, collision-free
  between subjects, and falls back to a fixed constant for the panic login.
* `OidcAuthorizationTests` — `/login` challenges Authentik instead of rendering the
  password form once OIDC is configured, and `/login/admin` (the break-glass route)
  exists only alongside `APP_PASSWORD`.

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
* **Organic mark and salt are read from text, not from data.** Picnic publishes neither
  as a field: the product page carries an organic claim in prose and the nutrition table
  as markdown, so `ProductFacts` recovers both by reading. Consequences to keep in mind:
  a product with no visible leaf may still be organic (nothing on its page said so), and
  the salt figure is only as good as the table it was parsed from. Both are scoped to the
  product's own page blocks, deliberately excluding the "similar products" strip — a leaf
  borrowed from a suggested organic alternative would be worse than no leaf at all. When
  Picnic renames those blocks, both facts go quiet rather than wrong, and a warning is
  logged.
* Quantities: a Mealie line's `quantity` only means "how many to buy" when the unit is
  countable (`stuks`, or no unit). For mass and volume the app buys one pack, unless
  `picnic_pack` is known and smaller than needed — 2 kg against 1 kg bags is two. Any
  line is capped at `Quantities.MaxPerItem` so a mis-parsed unit costs one basket slot
  rather than fifty.
* A line is only ticked off in Mealie after the product is *verified* present in the
  Picnic cart. Picnic answers some refusals with HTTP 200 and an error body, so a 2xx
  is not evidence of success — taking it as such used to tick the item off with nothing
  in the basket. If the add fails the line stays on the list; if the add succeeds but
  Mealie will not tick it off, that is reported separately rather than as a failure,
  because retrying would order it twice.
* Auth is a single shared password behind a rate-limited login, unless `OIDC_AUTHORITY`
  is set, in which case OIDC (e.g. Authentik) is the primary login and `APP_PASSWORD`
  becomes an optional break-glass fallback at `/login/admin` (deliberately not linked
  from anywhere in the UI). Who may sign in via OIDC is controlled entirely by the
  application/provider's own group policy bindings in Authentik — this app performs
  no additional group/claim check of its own, and does not manage accounts. Every
  signed-in identity gets its own cached Picnic token under `DATA_DIR/users/{key}/`
  once OIDC is enabled; without OIDC the token stays in the original single, shared
  location. Either way, the compose file publishes on loopback only; for ANY exposure
  beyond that, a TLS-terminating reverse proxy is required, with `COOKIE_SECURE=true`
  and `TRUST_PROXY=true` set — otherwise the session cookie and any password travel
  the network in cleartext.
