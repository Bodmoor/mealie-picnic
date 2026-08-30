# Traps in this repo

Failure modes that are silent, environment-dependent, or cost a CI cycle to
rediscover. Each one has actually happened here.

The README carries the architecture; this file carries only what the README, the
config and the compiler cannot tell you.

## Tests

**`AppText.Current` is process-wide, and one test flips it to English.** Any test
class that reads it — directly, or through `PriceText` / `AmountReason`, which
read it for their decimal separator — must carry `[Collection(AppTextCollection.Name)]`,
or it races and fails somewhere else. `AppTextCollectionAuditTests` enforces this;
if it fires, add the attribute rather than working around it. This passed locally
for months and failed in CI.

**`WebApplicationFactory` hosts in Development, which loads the developer's real
user secrets.** A machine with OIDC configured hosts a *different app* than CI
does — `/login/admin` exists on one and not the other, and `/login` challenges
instead of showing a form. Every factory must pin
`BOODSCHAPPEN_OIDC_AUTHORITY` (to `""` or a fake authority).
`FactoryConfigurationAuditTests` enforces it. This is the second CI-only failure
this repo has had, and it had the same shape as the first.

**A logging test that ignores scope state proves nothing about scopes.** The
capture provider in `RequestLoggingTests` records scopes *at write time*,
because that is when a console sink reads them — stringifying at scope-open time
hid a bug where the identity was resolved too early.

## Razor

**`x@r.Amount` renders as its own source text.** Razor leaves anything shaped
like an email address alone, so a word character immediately before `@` turns the
expression into a literal. It compiles, renders, and looks like text somebody
typed. Write `x@(r.Amount)`. `BasketLogTests` scans every `.cshtml` for the
pattern.

**Razor encodes non-ASCII to numeric entities.** Asserting on Dutch text with a
diaeresis needs `HtmlEncoder.Default.Encode(...)`, not the literal.

## The client

**Alpine is the CSP build**, which evaluates one expression per directive and
refuses `x-html` outright. Three bugs here have the same shape: the build refuses
something, the binding silently stays as it was, and nothing server-side is
wrong. `CspDirectiveAuditTests` guards the directive names and expression shape.

**htmx swaps `#view` and pushes history on the three navigations** (list → search
view → product detail). Actions — linking, excluding, verdicts — deliberately do
not push: Back should not land on a URL that no longer describes the screen.

**A view that pushes history must answer a non-htmx request with the whole
shell.** `EndpointHelpers.ShellFor` does this; a bare fragment served to a reload
or a shared link renders unstyled with no shell around it.

## Storage

**`ProductFactsStore` has a schema version, and changing what a record means
requires bumping it.** It is a lossy projection: it holds the derived facts and
the allergen evidence, never the product page text. A detail view asking for the
page text must call `GetDetailsAsync(..., full: true)`, which skips the disk
store — a stored record cannot satisfy it, and serving one renders an empty
detail page that looks like an unreadable product page.

**Every store writes via temp file + rename** and tolerates a truncated file by
starting empty. Follow that shape rather than inventing another.

## Deployment

The app runs on a Raspberry Pi behind nginx-proxy-manager, internet-facing, at
`boodschappen.bodmoor.nl`. Consequences worth holding: an unhandled exception is
a stack trace in a log someone will paste into a chat window; anonymous endpoints
are reached by scanners continuously; and `Microsoft.AspNetCore` logging is
turned down to `Warning` in `appsettings.json` *because* its request logging
writes the OIDC authorization code in full. Raising that level re-opens the leak.
