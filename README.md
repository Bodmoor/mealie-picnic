# Mealie → Picnic

***DISCLAIMER: This project is largely vibecoded***

Links Mealie shopping list items to Picnic products, and fills the Picnic basket in one click.

The mapping is keyed on the Mealie **food** (not the recipe, not the list item), so it
is reused by every recipe that uses that ingredient — but scoped per **huishouden**
(Mealie household): each household maintains its own Picnic link for the same food,
independent of the others (issue #17). It is stored in this app's own data, one JSON
file per household under `DATA_DIR/households/{key}/links.json`, not on the Mealie food
itself — Mealie foods are group-scoped, so every household would otherwise see the same
link. Which household a signed-in user belongs to is resolved once at login, via the
OIDC email claim looked up against Mealie's admin API; see `MEALIE_TOKEN` below.

| field         | meaning                                          |
| ------------- | ------------------------------------------------ |
| `picnicUid`   | Picnic selling-unit id, e.g. `s1005080`          |
| `label`       | product name at time of linking, for diagnostics |
| `pack`        | Picnic's pack size at linking, e.g. `1 kg` or `6 stuks` |
| `excluded`    | never buy this food at Picnic                    |

Foods with no link at all for the current household are shown first as **nieuw**.
Excluded foods are hidden behind a toggle, where the setting can be reverted.

## Product detail

Each search result card carries a small **i** button in its corner. Clicking the
card still links the product, as it always did; the corner button opens a detail
view instead, with everything Picnic's product page gives us — the ingredient and
nutrition sections, the highlights, the description, the organic claim and the
salt figure — laid out in one place.

Its reason for existing is the allergen chips (issue #48). The detail view shows
**why** each chip is there: the word that matched and the ingredient sentence it
was found in. A *suspected* chip — our word list matched, but Picnic did not mark
the term — can then be confirmed or denied, and that verdict is stored in
`DATA_DIR/allergen-verdicts.json`.

That store is **global**, not per household: whether a product contains nuts is a
fact about the product, and the answer does not change because a different
household is asking. Verdicts record who made them and against which ingredient
text, so a verdict made before a reformulation is flagged as stale rather than
quietly trusted.

A **declared** allergen — one Picnic emphasises in the ingredient list, as EU
labelling requires — offers no deny button, and the endpoint rejects the attempt.
Overruling a legal allergen declaration by clicking would turn a safety label into
a preference; a declared mark that looks wrong is a parser bug, not a verdict.

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
| `APP_PASSWORD`         | yes*     | —              | *not required once `BOODSCHAPPEN_OIDC_AUTHORITY` is set; still works there as a break-glass fallback at `/login/admin` (unlinked from the UI) |
| `BOODSCHAPPEN_OIDC_AUTHORITY`     | no       | —              | this app's OWN Authentik application issuer URL (not Mealie's); setting this turns OIDC login on |
| `BOODSCHAPPEN_OIDC_CLIENT_ID`     | no*      | —              | *required once `BOODSCHAPPEN_OIDC_AUTHORITY` is set |
| `BOODSCHAPPEN_OIDC_CLIENT_SECRET` | no*      | —              | *required once `BOODSCHAPPEN_OIDC_AUTHORITY` is set; confidential client |
| `MEALIE_URL`           | yes      | —              | e.g. `https://mealie.local`               |
| `MEALIE_TOKEN`         | yes      | —              | Mealie → Profile → API Tokens; **must be a superuser/admin account** once `BOODSCHAPPEN_OIDC_AUTHORITY` is set — it also calls `/api/admin/users` to resolve a signed-in email to a household; user secret locally |
| `MEALIE_LIST`          | no       | `Boodschappen` | *default* list; the UI has a picker and remembers your household's choice server-side |
| `PICNIC_USER`          | no*      | —              | *needed until a token is cached           |
| `PICNIC_PASSWORD`      | no*      | —              |                                           |
| `PICNIC_COUNTRY`       | no       | `NL`           | `NL`, `DE`, `FR`                          |
| `LANGUAGE`             | no       | `nl`           | interface language: `nl` or `en` (`en-GB` and the like also select English). Independent of `PICNIC_COUNTRY` — the storefront stays whatever the country says, so an English interface on the Dutch storefront is a normal setup. Anything unrecognised falls back to Dutch. Also decides the decimal separator: `€2,69` / `1,2 g` in Dutch, `€2.69` / `1.2 g` in English |
| `PICNIC_API_VERSION`   | no       | `15`           | bump if Picnic starts returning 400s      |
| `SEARCH_CACHE_MINUTES` | no       | `30`           | in-memory cache for searches. Shared between users on purpose (a household orders from one address), but a cached result is only served to someone who actually holds a Picnic token |
| `PRODUCT_FACTS_TTL_HOURS` | no    | `168`          | how long organic, salt and allergens are kept per product. Persisted to `DATA_DIR/product-facts.json`, so a restart no longer costs a page fetch per card. A week rather than forever because a **reformulated** product is what a long expiry gets wrong: an added allergen would go unseen for this long. Do not raise it much past a month |
| `DATA_DIR`             | no       | `/data`        | volume for the token and device id        |
| `COOKIE_SECURE`        | no       | `true`         | Secure flag on the session cookie (needs TLS); set `false` for plain-HTTP loopback-only local dev |
| `TRUST_PROXY`          | no       | `true`         | honour X-Forwarded-* from a reverse proxy; set `false` only when nothing is actually in front |

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

* `MealieClientTests` — items load unclassified (classification is household-scoped
  now), free-text notes skipped, and the household lookup against `/api/admin/users`
  matches by email case-insensitively and surfaces a 403 from a non-admin token.
* `HouseholdLinkStoreTests` — link/exclude/clear classify a food correctly for one
  household without leaking into another's, `New` sorted first, and the merged pack
  size still feeds the basket's amount calculation (issue #5's fix, now downstream of
  the household merge).
* `HouseholdKeyTests` — the household-id-to-storage-key hash is stable and
  collision-free between households.
* `PicnicClientTests` — selling units collected from the layout tree in relevance order
  and deduplicated, 403 mapped to `PicnicAuthException`, bad credentials returned as
  HTTP 200 with an error body still treated as failure, and the refreshed
  `x-picnic-auth` header captured after 2FA.
* `ProductFactsTests` — the organic and salt readers, weighted towards the false
  positives: "biologisch afbreekbaar" is biodegradable packaging, not organic food,
  and a "per 100 g" heading is not a salt figure.
* `ProductFactsStoreTests` — what survives a restart and, more importantly, what must
  not: an expired record is dropped on load, and a file written under an older schema is
  discarded rather than reinterpreted. Also that a corrupt or unwritable file degrades to
  "no cached facts" instead of taking the app down with it.
* `MealieReadsTests` — one request reads a list once; a *new* request reads it again,
  because the shopping list is shared mutable data and must not be cached across
  requests; and `Invalidate()` really does force the re-read the basket run depends on
  after it checks items off.
* `AllergenTests` — the two groups, weighted towards the false positives that a
  positive-only display turns into lies on the card: `kokosmelk` and `melkdistel` are not
  dairy, `nootmuskaat` is not a nut, and `amandelmelk` is nuts but not milk. Also that
  emphasis inside a compound (`karne**melk**poeder`) still declares, and that the grid
  never words its explanation as a "free from" claim.
* `AppOptionsTests` — defaults, required keys, blank values falling through, and the
  OIDC-vs-`APP_PASSWORD` required-key interplay.
* `TokenStoreTests` — the token survives a restart, so 2FA stays rare; also that a
  `null` key keeps writing the original flat layout, and different keys get isolated
  `DATA_DIR/users/{key}/` slots.
* `AllergenVerdictStoreTests` — verdicts round-trip, survive a restart, are kept per
  product and per group, and a truncated file reads as "not reviewed" rather than
  throwing.
* `ProductDetailTests` — the card carries both actions without nesting one button
  inside another, the detail view shows the matched word and its source sentence, a
  suspected mark can be confirmed or denied while a declared one cannot, and a
  verdict made against different ingredient text is flagged as stale.
* `UserKeyTests` — the OIDC-`sub`-to-storage-key hash is stable, collision-free
  between subjects, and falls back to a fixed constant for the panic login.
* `OidcAuthorizationTests` — `/login` challenges Authentik silently, falls back to the
  button when that cannot succeed, stays on the button after a sign-out, and never
  renders the password form once OIDC is configured; `/login/admin` (the break-glass route)
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
* **Allergen chips are informational, and their absence proves nothing.** Two chips,
  `noten`/`nuts` (all tree nuts and peanuts together) and `melk`/`milk` (including
  `lactose`, `wei` and `caseïne`), read from the ingredient accordion. The chip labels
  follow `LANGUAGE`; the words matched against the page do not — those are Picnic's Dutch
  storefront text and stay Dutch. A tick means Picnic emphasised the
  allergen itself, which EU labelling requires of a declaration; a question mark means our
  own word list matched ordinary text. A card with no chip is not a claim that the product
  is free of anything — it may equally be a page that could not be read, and the app never
  renders a "free from" claim anywhere. **Do not rely on this if someone's health depends
  on it: read the packaging.** Three known limits. The emphasis rule is inferred from the
  labelling rules rather than confirmed against a captured product page (no fixture here
  contains an allergen section). Whole-word matching means `amandelmelk` raises
  neither chip on unemphasised text although almond is a tree nut. And only accordion
  sections whose title looks like ingredients or allergens are read at all — the
  description sits in that same accordion, and reading it put a milk chip on a loaf whose
  ingredients had no dairy (issue #45) — so if Picnic renames those sections the chips go
  quiet rather than wrong. That is the opposite trade-off from salt, which deliberately
  widens to every section: a missed salt figure is an annoyance, a wrong allergen chip is
  a lie.
* **What is cached, and for how long.** Picnic searches are held in memory for
  `SEARCH_CACHE_MINUTES`; product facts for `PRODUCT_FACTS_TTL_HOURS` in memory *and* on
  disk under `DATA_DIR`; product images for 4 h in memory and a week in the browser. The
  Mealie shopping list is deliberately **not** cached beyond a single request — it is
  shared mutable data, so someone else's edit shows up on your next request rather than
  after a timeout. Nothing here is a shared cache across containers: the deployment is a
  single instance, so in-process plus disk is the whole story.
* Quantities: a Mealie line's `quantity` only means "how many to buy" when the unit is
  countable (`stuks`, or no unit). For mass and volume the app buys one pack, unless the
  household's stored pack size is known and smaller than needed — 2 kg against 1 kg bags
  is two. Any line is capped at `Quantities.MaxPerItem` so a mis-parsed unit costs one
  basket slot rather than fifty.
* A line is only ticked off in Mealie after the product is *verified* present in the
  Picnic cart. Picnic answers some refusals with HTTP 200 and an error body, so a 2xx
  is not evidence of success — taking it as such used to tick the item off with nothing
  in the basket. If the add fails the line stays on the list; if the add succeeds but
  Mealie will not tick it off, that is reported separately rather than as a failure,
  because retrying would order it twice.
* Auth is a single shared password behind a rate-limited login, unless
  `BOODSCHAPPEN_OIDC_AUTHORITY` is set, in which case OIDC (e.g. Authentik, via this
  app's own dedicated application) is the primary login and `APP_PASSWORD`
  becomes an optional break-glass fallback at `/login/admin` (deliberately not linked
  from anywhere in the UI). Who may sign in via OIDC is controlled entirely by the
  application/provider's own group policy bindings in Authentik — this app performs
  no additional group/claim check of its own, and does not manage accounts. An OIDC
  sign-in is **persistent** (a 30-day sliding cookie the browser keeps across
  restarts); the `/login/admin` fallback deliberately stays session-scoped, so a
  shared operator password leaves nothing behind. `/login` also attempts a
  **silent** sign-in first (`prompt=none`), so an existing Authentik session opens
  the app with no click at all; the button appears only when that finds no session,
  for half an hour after an explicit sign-out, and for a minute after any attempt so
  a cookie that fails to stick cannot become a redirect loop. Every
  signed-in identity gets its own cached Picnic token under `DATA_DIR/users/{key}/`
  once OIDC is enabled; without OIDC the token stays in the original single, shared
  location. Either way, the compose file publishes on loopback only; `COOKIE_SECURE`
  and `TRUST_PROXY` both default to `true` (HTTPS enforced), so exposing this beyond
  loopback needs a TLS-terminating reverse proxy in front that forwards
  `X-Forwarded-Proto` — without `TRUST_PROXY`, the app cannot tell it's actually
  behind TLS and builds absolute URLs (e.g. the OIDC `redirect_uri`) as `http://`,
  which most identity providers then reject as a mismatch. Set both to `false`
  explicitly only when there truly is no proxy at all (e.g. plain-HTTP loopback-only
  local dev, which `docker-compose.yml` already does).
* Household resolution (issue #17) happens once, at sign-in, and is cached as a claim
  on the session cookie — not looked up again until the next login. If the signed-in
  email is not linked to any Mealie household, sign-in still succeeds, but every
  mapping endpoint (`/api/list`, `/api/link`, `/api/exclude`, `/api/include`,
  `/api/basket`) answers `422` naming the email, until an admin links it in Mealie.
  Without `BOODSCHAPPEN_OIDC_AUTHORITY` set, there is no email to resolve at all, so every session
  (including the `/login/admin` break-glass one) shares one fixed pseudo-household
  (`local`) — the same single-household behaviour as before this feature existed.
  There is no migration from the old food-extras links: existing links must be
  re-created per household after upgrading.
