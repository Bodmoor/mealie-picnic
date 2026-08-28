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
    public const string AppPage = """
        <!doctype html>
        <html lang="nl"><meta charset="utf-8">
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
          select { border:1px solid #3a3d41; background:#1d2024; color:#e8eaed;
                   border-radius:7px; padding:7px 9px; font-size:14px }
          button { border:1px solid #3a3d41; background:#1d2024; color:#e8eaed;
                   border-radius:7px; padding:7px 13px; font-size:14px; cursor:pointer }
          button:hover { border-color:#5b9dd9 }
          button.primary { background:#3f7fbf; border-color:#3f7fbf; color:#fff }
          button.primary:hover { background:#4a8fd4 }
          button.danger:hover { border-color:#c86a6a; color:#e59a9a }
          .muted { color:#9aa0a6; font-size:13px }
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
                  cursor:pointer; text-align:left }
          .card:hover { border-color:#5b9dd9 }
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
             when the lazily fetched leaf and salt figure arrive. */
          .card .f { min-height:19px; margin-top:5px; display:flex; align-items:center; gap:6px }
          .card .f img { width:auto; height:15px; margin:0; background:none; border-radius:2px }
          .salt { font-size:11.5px; color:#9aa0a6 }
          .salt.hi { color:#e0a2a2 }
          .salt.lo { color:#8fbf9a }
          pre { background:#111316; border:1px solid #2a2d31; border-radius:9px; padding:13px;
                overflow:auto; max-height:440px; font-size:12px; color:#c9cdd2 }
          .row { display:flex; gap:18px; align-items:flex-start; flex-wrap:wrap }
          .row img { width:190px; height:190px; object-fit:contain; background:#fff;
                     border-radius:10px }
          label.chk { font-size:13px; color:#9aa0a6; display:flex; align-items:center; gap:6px;
                      cursor:pointer }
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
        </style>

        <a class="brand" href="/" title="Terug naar de lijst">
          <img src="/icons/192.png" width="34" height="34" alt="">
          <h1>Mealie &rarr; Picnic</h1>
        </a>
        <div class="muted" x-data x-init="$store.picnic.refresh()" x-cloak>
          <span x-show="$store.picnic.authenticated">Picnic: <b class="ok">ingelogd</b>
            <a href="#" x-on:click.prevent="$store.picnic.logout()">uitloggen</a></span>
          <span x-show="!$store.picnic.authenticated">Picnic: <b class="bad">niet ingelogd</b>
            <a href="#" x-on:click.prevent="$store.picnic.promptLogin()">inloggen</a></span>
          &middot;
          <form method="post" action="/logout" style="display:inline">
            <button type="submit" class="linklike">afmelden bij deze app</button>
          </form>
        </div>

        <div id="view" hx-get="/api/list" hx-trigger="load" hx-swap="outerHTML">
          <p class="muted">Laden...</p>
        </div>

        <dialog id="creds">
          <b>Picnic inloggen</b>
          <p class="muted" id="credsMsg"></p>
          <input id="cuser" placeholder="E-mailadres" autocomplete="username">
          <input id="cpass" type="password" autocomplete="current-password">
          <div class="bar" x-data>
            <button class="primary" x-on:click="$store.picnic.submitCreds()">Inloggen</button>
            <button x-on:click="$store.picnic.closeCreds()">Sluiten</button>
          </div>
        </dialog>

        <dialog id="twofa">
          <b>Picnic 2FA</b>
          <p class="muted" id="twofaMsg">Kies waar de code naartoe moet.</p>
          <div class="bar" x-data>
            <button data-channel="SMS" data-label="SMS"
                    x-on:click="$store.picnic.send2fa('SMS')">SMS</button>
            <button data-channel="EMAIL" data-label="E-mail"
                    x-on:click="$store.picnic.send2fa('EMAIL')">E-mail</button>
          </div>
          <input id="otp" placeholder="Code" inputmode="numeric">
          <div class="bar" x-data>
            <button class="primary" x-on:click="$store.picnic.verify2fa()">Verifieren</button>
            <button x-on:click="$store.picnic.closeTwofa()">Sluiten</button>
          </div>
        </dialog>

        <script src="/assets/app.js" defer></script>
        <script src="/assets/alpine-csp.min.js" defer></script>
        <script src="/assets/htmx.min.js" defer></script>
        </html>
        """;
}
