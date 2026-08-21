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

## Configuration

All via environment variables.

| Variable               | Required | Default        | Notes                                     |
| ---------------------- | -------- | -------------- | ----------------------------------------- |
| `APP_PASSWORD`         | yes      | —              | password for the web UI                   |
| `MEALIE_URL`           | yes      | —              | e.g. `https://mealie.local`               |
| `MEALIE_TOKEN`         | yes      | —              | Mealie → Profile → API Tokens             |
| `MEALIE_LIST`          | no       | `Boodschappen` | shopping list name                        |
| `PICNIC_USER`          | no*      | —              | *needed until a token is cached           |
| `PICNIC_PASSWORD`      | no*      | —              |                                           |
| `PICNIC_COUNTRY`       | no       | `NL`           | `NL`, `DE`, `FR`                          |
| `PICNIC_API_VERSION`   | no       | `15`           | bump if Picnic starts returning 400s      |
| `SEARCH_CACHE_MINUTES` | no       | `30`           | in-memory cache for searches and details  |
| `DATA_DIR`             | no       | `/data`        | mounted volume for the token              |

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
