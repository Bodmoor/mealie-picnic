namespace MealiePicnic.Presentation;

/// <summary>
/// The app shell, served as a string. No build step, no npm -- the login pages
/// moved to Slices/LoginPage.cshtml and Slices/OidcLoginPage.cshtml (RazorSlices
/// auto-escapes, so nothing here hand-encodes user-influenced values any more),
/// but this one page has no per-request data model, so there is nothing a slice
/// would buy it.
/// </summary>
public static class Html
{
    /// <summary>
    /// Marks a rendered ListView so htmx swaps it out of band -- the basket
    /// response then carries the log AND the refreshed list, instead of costing a
    /// second round trip (issue #70).
    ///
    /// Throws rather than returning the html untouched when the root element is
    /// not where it expects. This was a plain string Replace inside the handler,
    /// so an attribute reordered in ListView.cshtml would have quietly produced a
    /// response whose second half the browser ignored: the list simply would not
    /// refresh after a check-off, with nothing anywhere saying why.
    /// </summary>
    public static string AsOutOfBandSwap(string listHtml)
    {
        const string marker = "<div id=\"view\"";
        var at = listHtml.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
        {
            throw new InvalidOperationException(
                $"ListView no longer starts with {marker}, so it cannot be swapped out of band.");
        }

        var end = at + marker.Length;
        return listHtml[..end] + " hx-swap-oob=\"true\"" + listHtml[end..];
    }

    private sealed record Rendered(AppText Text, string Html);

    // Built once per language rather than per request: the shell carries no
    // per-request data, and LANGUAGE is fixed for the process (see AppText). The
    // holder is a single reference so a racing reader sees a matched
    // text/markup pair rather than one from each side.
    private static Rendered? rendered;

    /// <summary>The shell in the configured language (issue #13).</summary>
    public static string AppPage
    {
        get
        {
            var text = AppText.Current;
            if (rendered is { } current && ReferenceEquals(current.Text, text))
                return current.Html;

            var built = new Rendered(text, PageFor(text));
            rendered = built;
            return built.Html;
        }
    }

    /// <summary>
    /// The shell in a given language. Public so tests can render both without
    /// reaching for the process-wide setting.
    ///
    /// The raw template's script URLs get a ?v={content hash} appended (see
    /// Vendor.cs) so a week-long client cache on /assets/{name} cannot keep
    /// serving a stale app.js past a deploy.
    /// </summary>
    public static string PageFor(AppText text) => Template(text)
        .Replace("/assets/htmx.min.js", $"/assets/htmx.min.js?v={Vendor.HtmxVersion}")
        .Replace("/assets/alpine-csp.min.js", $"/assets/alpine-csp.min.js?v={Vendor.AlpineCspVersion}")
        .Replace("/assets/app.js", $"/assets/app.js?v={Vendor.AppJsVersion}");

    // $$""" so the CSS keeps its single braces and only {{...}} interpolates.
    //
    // The #i18n block is a data block, not a script: its type is not a JavaScript
    // MIME type, so the browser never prepares it for execution and the strict
    // script-src (no 'unsafe-inline', see Program.cs) does not apply to it. That
    // is what lets app.js have translated strings without either inlining
    // executable JS or spending a round trip on a /api/strings fetch. The JSON is
    // serialized with the HTML-escaping encoder, so a value containing
    // "</script>" cannot close the block.
    //
    // Note there is no x-init anywhere below. The status line used to carry
    // x-init="$store.picnic.refresh(); $store.me.refresh()", and the
    // @alpinejs/csp build evaluates exactly one expression per directive: its
    // parser takes one expression plus an optional trailing semicolon and throws
    // on anything after that, and its interpreter has no node type for a
    // statement sequence at all. So the directive threw on parse and NEITHER
    // store was ever refreshed -- the signed-in account never appeared on the
    // sign-out button and the Picnic status was stuck on its initial value
    // (issue #43). Both stores now refresh from their own init(), which Alpine
    // calls when the store is registered, so there is no expression to parse.
    //
    // This is the third bug of that shape here, after #26 and #33: the CSP build
    // refuses something, the binding silently stays as it was, and nothing
    // server-side is wrong. CspDirectiveAuditTests now guards the expression
    // shape as well as the directive names.
    private static string Template(AppText text) => $$"""
        <!doctype html>
        <html lang="{{text.Lang}}"><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <link rel="icon" href="/favicon.ico" type="image/png">
        <link rel="icon" href="/icons/192.png" sizes="192x192" type="image/png">
        <link rel="apple-touch-icon" href="/icons/192.png">
        <link rel="manifest" href="/manifest.webmanifest">
        <meta name="theme-color" content="#1d2024">
        <meta name="mobile-web-app-capable" content="yes">
        <title>Mealie &rarr; Picnic</title>
        <style>
          :root { color-scheme: dark }
          * { box-sizing: border-box }
          body { font:15px/1.45 system-ui,sans-serif; margin:0; padding:20px 22px 60px;
                 background:#15171a; color:#e8eaed; max-width:1000px }
          h1 { font-size:18px; margin:0 }
          /* The whole lockup is the link home; keep it looking like a heading,
             not like body-text link. */
          .brand { display:flex; align-items:center; gap:10px; margin-bottom:3px;
                   text-decoration:none; color:inherit; width:fit-content }
          .brand:hover h1 { color:#7fb2e5 }
          .brand img { border-radius:7px; flex:0 0 auto }
          h2 { font-size:15px; margin:26px 0 10px; color:#9aa0a6; font-weight:600 }
          a { color:#7fb2e5 }
          .bar { display:flex; gap:9px; flex-wrap:wrap; align-items:center; margin:16px 0 6px }
          /* The controls above the list (issue #56). Three groups -- which list,
             what to do with it, what to show -- so a break lands between them
             rather than through one. */
          .toolbar { display:flex; flex-wrap:wrap; align-items:center;
                     gap:10px 14px; margin:16px 0 6px }
          .toolbar .group { display:flex; align-items:center; gap:9px }
          .toolbar .group + .group { padding-left:14px; border-left:1px solid #2a2d31 }
          /* Below this width the groups stack, one per row. The dividers turn
             horizontal with them: a left border on a group that has wrapped onto
             its own line reads as a stray mark rather than a separator. */
          @media (max-width:620px) {
            .toolbar { flex-direction:column; align-items:stretch; gap:0 }
            .toolbar .group { justify-content:space-between; padding:10px 0 }
            .toolbar .group + .group { padding-left:0; border-left:0; border-top:1px solid #2a2d31 }
            .toolbar .group select { flex:1 1 auto; min-width:0 }
          }
          select { border:1px solid #3a3d41; background:#1d2024; color:#e8eaed;
                   border-radius:7px; padding:7px 9px; font-size:14px }
          button { border:1px solid #3a3d41; background:#1d2024; color:#e8eaed;
                   border-radius:7px; padding:7px 13px; font-size:14px; cursor:pointer }
          button:hover { border-color:#5b9dd9 }
          button.primary { background:#3f7fbf; border-color:#3f7fbf; color:#fff }
          button.primary:hover { background:#4a8fd4 }
          button.danger:hover { border-color:#c86a6a; color:#e59a9a }
          .muted { color:#9aa0a6; font-size:13px }
          /* Status and actions, each a whole unit, so a wrap happens between them
             and never inside one (issue #56). The gap replaces the separator
             character that used to be able to strand at a line end. */
          .account { display:flex; flex-wrap:wrap; align-items:center; gap:6px 16px;
                     margin-top:4px }
          .account .status { white-space:nowrap }
          .account-actions { display:flex; align-items:center; gap:8px; flex-wrap:wrap }
          /* The form is only there to make the app sign-out a POST -- a GET that
             signs you out can be fired by an <img> tag. It must not become a
             layout box of its own. */
          .account-actions form { display:contents }
          /* Secondary next to the toolbar's controls, but still a button:
             these are the two actions with consequences on this page. */
          button.small { padding:5px 11px; font-size:13px }
          .linklike { border:0; background:none; padding:0; color:#7fb2e5;
                      text-decoration:underline; cursor:pointer; font-size:inherit }
          .item { display:flex; align-items:center; gap:12px; padding:10px 12px;
                  border:1px solid #2a2d31; border-radius:9px; margin-bottom:7px;
                  background:#1a1d21; cursor:pointer }
          .item:hover { border-color:#5b9dd9 }
          .item .name { font-weight:500 }
          .item .sub { font-size:12px; color:#9aa0a6 }
          .item .tag { margin-left:auto; font-size:11px; padding:3px 8px; border-radius:20px;
                       border:1px solid #3a3d41; color:#9aa0a6; white-space:nowrap }
          .tag.new { border-color:#c9a227; color:#e0be55 }
          .tag.linked { border-color:#4f8a5c; color:#7fc08a }
          details.done-block { margin-top:26px; border-top:1px solid #2a2d31; padding-top:14px }
          details.done-block summary { cursor:pointer; color:#9aa0a6; font-size:14px;
                                       font-weight:600; padding:4px 0 }
          details.done-block summary:hover { color:#e8eaed }
          details.done-block .item { cursor:default; opacity:.6; margin-top:7px }
          details.done-block .item:hover { border-color:#2a2d31 }
          .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:11px }
          .card { border:1px solid #2a2d31; border-radius:10px; padding:10px; background:#1a1d21;
                  cursor:pointer; text-align:left; position:relative }
          .card:hover { border-color:#5b9dd9 }
          /* Issue #48 split the card into two buttons. The linking one still fills
             the whole card, so the tap target is what it always was; `all:unset`
             strips the browser's button chrome that the card used to override. */
          .card .pick { all:unset; display:block; width:100%; cursor:pointer }
          /* Issue #54: four times the area it started at, and flush into the
             corner rather than floating 6px inside it. At 22px it sat over the
             product image and a miss ran the OTHER action -- linking the product,
             which then has to be undone. 44px is the minimum touch target both
             mobile platforms ask for. The radius follows the card's own 10px on
             the outer corner, and rounds the inner one so it reads as a corner
             tile rather than a square dropped on top. */
          .card .info { position:absolute; top:0; right:0; z-index:2;
                        width:44px; height:44px; padding:0; line-height:44px;
                        border-radius:0 10px 0 10px;
                        border:0; border-left:1px solid #2a2d31; border-bottom:1px solid #2a2d31;
                        background:#15171a;
                        color:#9aa0a6; font-size:17px; font-style:italic; font-weight:700;
                        text-align:center; cursor:pointer }
          .card .info:hover { border-color:#5b9dd9; color:#e8eaed; background:#1d2126 }
          .card.linked { border-color:#4f8a5c; background:#1a2420 }
          .card.linked:hover { border-color:#7fc08a }
          .badge { display:inline-block; font-size:11px; color:#7fc08a;
                   border:1px solid #4f8a5c; border-radius:20px;
                   padding:1px 7px; margin-bottom:6px }
          .card img { width:100%; height:104px; object-fit:contain; background:#fff;
                      border-radius:7px; margin-bottom:8px }
          .card .n { font-size:12.5px; line-height:1.3; height:50px; overflow:hidden }
          .card .p { font-size:12px; color:#9aa0a6; margin-top:6px }
          .card .p b { color:#e8eaed }
          /* Product facts (issue #6): reserve the height so cards do not jump
             when the lazily fetched leaf and salt figure arrive. Wraps since the
             allergen chips joined the row (issue #14) -- a card can now carry the
             leaf, two chips and the salt figure. */
          .card .f { min-height:19px; margin-top:5px; display:flex; align-items:center;
                     flex-wrap:wrap; gap:6px 6px }
          .card .f img { width:auto; height:15px; margin:0; background:none; border-radius:2px }
          .salt { font-size:11.5px; color:#9aa0a6 }
          .salt.hi { color:#e0a2a2 }
          .salt.lo { color:#8fbf9a }
          /* Allergens (issue #14). The quieter style is the uncertain one: our
             word list matched, but Picnic did not mark it. The louder style is
             Picnic's own declaration. */
          .allergen { font-size:11.5px; padding:0 6px; border-radius:20px;
                      border:1px solid #5a4d20; color:#c9a227; white-space:nowrap }
          /* Product detail (issue #48) */
          .detail { display:flex; gap:18px; align-items:flex-start; flex-wrap:wrap }
          .detail-image { width:180px; height:180px; object-fit:contain;
                          background:#fff; border-radius:9px; flex:none }
          .detail-body { flex:1 1 320px; min-width:0 }
          .detail-body section { margin-bottom:18px }
          .detail-body h3 { font-size:13px; text-transform:uppercase; letter-spacing:.04em;
                            color:#9aa0a6; margin:0 0 7px }
          .detail-body p { margin:0 0 7px; font-size:13.5px; line-height:1.45 }
          .detail-body ul { margin:0; padding-left:18px; font-size:13.5px; line-height:1.5 }
          .allergen-row { border:1px solid #2a2d31; border-radius:9px;
                          padding:10px 12px; margin-bottom:10px }
          .allergen-head { display:flex; align-items:center; gap:9px; flex-wrap:wrap }
          .evidence { margin:8px 0 0; font-size:13px; line-height:1.45 }
          .verdict { display:flex; align-items:center; gap:8px; flex-wrap:wrap; margin-top:9px }
          .verdict button { font-size:12px; padding:4px 10px }
          .stale { margin:7px 0 0; font-size:12.5px; color:#e0a2a2 }
          .allergen.declared { border-color:#7a3f3f; color:#e0a2a2 }
          /* Quieter still than a suspected chip (issue #58): a "kan sporen van"
             line is a warning about the factory, not a claim about what is in
             the product, and it must not read as loudly as one. */
          .allergen.traces { border-color:#3a3d41; color:#8e939a }
          .allergen-note { margin:14px 0 0 }
          pre { background:#111316; border:1px solid #2a2d31; border-radius:9px; padding:13px;
                overflow:auto; max-height:440px; font-size:12px; color:#c9cdd2 }
          .row { display:flex; gap:18px; align-items:flex-start; flex-wrap:wrap }
          .row img { width:190px; height:190px; object-fit:contain; background:#fff;
                     border-radius:10px }
          label.chk { font-size:13px; color:#9aa0a6; display:flex; align-items:center; gap:6px;
                      cursor:pointer }
          /* Issue #63. Sits above the view, so a failed action is visible without
             scrolling whatever the failed action was going to replace. */
          .status-bar { margin:14px 0 0; padding:9px 12px; border-radius:9px;
                        border:1px solid #7a3f3f; background:#231a1a; color:#e0a2a2;
                        font-size:13.5px }
          .log { font-size:13px; margin-top:12px }
          .log div { padding:2px 0 }
          .ok { color:#7fc08a } .bad { color:#e59a9a }
          dialog { background:#1d2024; color:#e8eaed; border:1px solid #2a2d31;
                   border-radius:12px; padding:22px; width:300px }
          button:disabled { opacity:.5; cursor:default }
          button:disabled:hover { border-color:#3a3d41 }
          dialog input { width:100%; padding:8px 10px; margin-top:8px; border-radius:6px;
                         border:1px solid #3a3d41; background:#15171a; color:#e8eaed }
          [x-cloak] { display:none }
          /* Issue #23: six numeric cells read as "enter a 6-digit code" at a
             glance, which one generic text field does not. */
          .otp { display:flex; gap:8px; margin-top:8px }
          .otp input { width:38px; height:44px; padding:0; margin:0; text-align:center;
                       font-size:20px }
        </style>

        <script type="application/json" id="i18n">{{text.ClientJson()}}</script>

        <a class="brand" href="/" title="{{text.BrandHomeTitle}}">
          <img src="/icons/192.png" width="34" height="34" alt="">
          <h1>Mealie &rarr; Picnic</h1>
        </a>
        <!-- Status, then actions (issue #60). Both actions used to be words in a
             sentence -- an <a href="#"> and a button styled to look like one --
             even though one revokes the Picnic token, costing a login and
             possibly 2FA to undo, and the other ends the session. They are
             buttons now, sized like the rest of the app's controls.
             The two Picnic buttons occupy the same position and only one is ever
             shown, so the row does not shift when the status changes.
             The separator between status and actions is spacing, not a literal
             character (issue #56): a &middot; could end up last on a wrapped line
             with nothing after it. -->
        <div class="account" x-data x-cloak>
          <span class="status muted">{{text.PicnicLabel}}:
            <b class="ok" x-show="$store.picnic.authenticated">{{text.PicnicSignedIn}}</b>
            <b class="bad" x-show="!$store.picnic.authenticated">{{text.PicnicSignedOut}}</b>
          </span>
          <div class="account-actions">
            <button class="small" x-show="$store.picnic.authenticated"
                    x-on:click="$store.picnic.logout()">{{text.PicnicSignOutAction}}</button>
            <button class="small" x-show="!$store.picnic.authenticated"
                    x-on:click="$store.picnic.promptLogin()">{{text.PicnicSignInAction}}</button>
            <form method="post" action="/logout">
              <button type="submit" class="small" title="{{text.AppSignOutTitle}}">{{text.AppSignOutAction}}<span x-show="$store.me.label"> (<span x-text="$store.me.label"></span>)</span></button>
            </form>
          </div>
        </div>

        <!-- Where a failed request says so (issue #63). Empty until something goes
             wrong; aria-live so a screen reader announces it without the focus
             being stolen, and role=status rather than alert because it reports a
             failed action, not an emergency. Written from app.js, never swapped
             into by htmx. -->
        <div id="status" class="status-bar" role="status" aria-live="polite" hidden></div>

        <div id="view" hx-get="/api/list" hx-trigger="load" hx-swap="outerHTML">
          <p class="muted">{{text.Loading}}</p>
        </div>

        <dialog id="creds">
          <b>{{text.CredsTitle}}</b>
          <p class="muted" id="credsMsg"></p>
          <input id="cuser" placeholder="{{text.CredsEmailPlaceholder}}" autocomplete="username">
          <input id="cpass" type="password" autocomplete="current-password">
          <div class="bar" x-data>
            <button class="primary" x-on:click="$store.picnic.submitCreds()">{{text.CredsSubmit}}</button>
            <button x-on:click="$store.picnic.closeCreds()">{{text.DialogClose}}</button>
          </div>
        </dialog>

        <dialog id="twofa">
          <b>{{text.TwoFactorTitle}}</b>
          <p class="muted" id="twofaMsg">{{text.TwoFactorChooseChannel}}</p>
          <div class="bar" x-data>
            <button data-channel="SMS" data-label="{{text.ChannelSms}}"
                    x-on:click="$store.picnic.send2fa('SMS')">{{text.ChannelSms}}</button>
            <button data-channel="EMAIL" data-label="{{text.ChannelEmail}}"
                    x-on:click="$store.picnic.send2fa('EMAIL')">{{text.ChannelEmail}}</button>
          </div>
          <div class="otp" x-data>
            <input class="otpcell" inputmode="numeric" pattern="[0-9]*" autocomplete="one-time-code"
                   x-on:input="$store.picnic.handleOtpInput($event)" x-on:keydown="$store.picnic.handleOtpBackspace($event)">
            <input class="otpcell" inputmode="numeric" pattern="[0-9]*"
                   x-on:input="$store.picnic.handleOtpInput($event)" x-on:keydown="$store.picnic.handleOtpBackspace($event)">
            <input class="otpcell" inputmode="numeric" pattern="[0-9]*"
                   x-on:input="$store.picnic.handleOtpInput($event)" x-on:keydown="$store.picnic.handleOtpBackspace($event)">
            <input class="otpcell" inputmode="numeric" pattern="[0-9]*"
                   x-on:input="$store.picnic.handleOtpInput($event)" x-on:keydown="$store.picnic.handleOtpBackspace($event)">
            <input class="otpcell" inputmode="numeric" pattern="[0-9]*"
                   x-on:input="$store.picnic.handleOtpInput($event)" x-on:keydown="$store.picnic.handleOtpBackspace($event)">
            <input class="otpcell" inputmode="numeric" pattern="[0-9]*"
                   x-on:input="$store.picnic.handleOtpInput($event)" x-on:keydown="$store.picnic.handleOtpBackspace($event)">
          </div>
          <input type="hidden" id="otp">
          <div class="bar" x-data>
            <button class="primary" x-on:click="$store.picnic.verify2fa()">{{text.TwoFactorVerify}}</button>
            <button x-on:click="$store.picnic.closeTwofa()">{{text.DialogClose}}</button>
          </div>
        </dialog>

        <script src="/assets/app.js" defer></script>
        <script src="/assets/alpine-csp.min.js" defer></script>
        <script src="/assets/htmx.min.js" defer></script>
        </html>
        """;
}
