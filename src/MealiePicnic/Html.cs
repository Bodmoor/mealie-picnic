namespace MealiePicnic;

/// <summary>
/// The whole UI, served as two strings. No build step, no npm, no framework.
/// </summary>
internal static class Html
{
    public const string LoginPage = """
        <!doctype html>
        <html lang="nl"><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Inloggen</title>
        <style>
          :root { color-scheme: dark }
          body { font:15px system-ui,sans-serif; background:#15171a; color:#e8eaed;
                 display:grid; place-items:center; height:100vh; margin:0 }
          form { background:#1d2024; border:1px solid #2a2d31; border-radius:12px;
                 padding:28px; width:290px }
          h1 { font-size:17px; margin:0 0 18px }
          input { width:100%; padding:9px 11px; border-radius:7px; border:1px solid #3a3d41;
                  background:#15171a; color:#e8eaed; box-sizing:border-box; font-size:15px }
          button { width:100%; margin-top:12px; padding:9px; border:0; border-radius:7px;
                   background:#3f7fbf; color:#fff; font-size:15px; cursor:pointer }
          button:hover { background:#4a8fd4 }
          .err { color:#e59a9a; font-size:13px; margin:12px 0 0 }
        </style>
        <form method="post" action="/login">
          <h1>Mealie &rarr; Picnic</h1>
          <input type="password" name="password" placeholder="Wachtwoord" autofocus>
          <button type="submit">Inloggen</button>
          <!--ERROR-->
        </form>
        </html>
        """;

    public const string AppPage = """
        <!doctype html>
        <html lang="nl"><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Mealie &rarr; Picnic</title>
        <style>
          :root { color-scheme: dark }
          * { box-sizing: border-box }
          body { font:15px/1.45 system-ui,sans-serif; margin:0; padding:20px 22px 60px;
                 background:#15171a; color:#e8eaed; max-width:1000px }
          h1 { font-size:18px; margin:0 0 3px }
          h2 { font-size:15px; margin:26px 0 10px; color:#9aa0a6; font-weight:600 }
          a { color:#7fb2e5 }
          .bar { display:flex; gap:9px; flex-wrap:wrap; align-items:center; margin:16px 0 6px }
          button { border:1px solid #3a3d41; background:#1d2024; color:#e8eaed;
                   border-radius:7px; padding:7px 13px; font-size:14px; cursor:pointer }
          button:hover { border-color:#5b9dd9 }
          button.primary { background:#3f7fbf; border-color:#3f7fbf; color:#fff }
          button.primary:hover { background:#4a8fd4 }
          button.danger:hover { border-color:#c86a6a; color:#e59a9a }
          .muted { color:#9aa0a6; font-size:13px }
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
          .grid { display:grid; grid-template-columns:repeat(auto-fill,minmax(150px,1fr)); gap:11px }
          .card { border:1px solid #2a2d31; border-radius:10px; padding:10px; background:#1a1d21;
                  cursor:pointer; text-align:left }
          .card:hover { border-color:#5b9dd9 }
          .card img { width:100%; height:104px; object-fit:contain; background:#fff;
                      border-radius:7px; margin-bottom:8px }
          .card .n { font-size:12.5px; line-height:1.3; height:50px; overflow:hidden }
          .card .p { font-size:12px; color:#9aa0a6; margin-top:6px }
          .card .p b { color:#e8eaed }
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
          dialog input { width:100%; padding:8px 10px; margin-top:8px; border-radius:6px;
                         border:1px solid #3a3d41; background:#15171a; color:#e8eaed }
        </style>

        <h1>Mealie &rarr; Picnic</h1>
        <div class="muted">
          <span id="pstatus">Picnic: onbekend</span> &middot; <a href="/logout">uitloggen</a>
        </div>

        <div id="view"></div>

        <dialog id="creds">
          <b>Picnic inloggen</b>
          <p class="muted">PICNIC_PASSWORD is niet ingesteld, dus vul hier je gegevens in.
             Ze worden niet opgeslagen; alleen het token wordt bewaard.</p>
          <input id="cuser" placeholder="E-mailadres" autocomplete="username">
          <input id="cpass" type="password" placeholder="Wachtwoord" autocomplete="current-password">
          <div class="bar">
            <button class="primary" onclick="submitCreds()">Inloggen</button>
            <button onclick="document.getElementById('creds').close()">Sluiten</button>
          </div>
        </dialog>

        <dialog id="twofa">
          <b>Picnic 2FA</b>
          <p class="muted" id="twofaMsg">Kies waar de code naartoe moet.</p>
          <div class="bar">
            <button onclick="send2fa('SMS')">SMS</button>
            <button onclick="send2fa('EMAIL')">E-mail</button>
          </div>
          <input id="otp" placeholder="Code" inputmode="numeric">
          <div class="bar">
            <button class="primary" onclick="verify2fa()">Verifieren</button>
            <button onclick="document.getElementById('twofa').close()">Sluiten</button>
          </div>
        </dialog>

        <script>
        let items = [], showExcluded = false;

        // Whatever we were doing when Picnic asked us to authenticate, so it can
        // be resumed once login (and any 2FA) completes.
        let pendingRetry = null;

        async function runPending() {
          const retry = pendingRetry;
          pendingRetry = null;
          if (retry) await retry();
        }

        const el = document.getElementById('view');
        const esc = s => String(s ?? '').replace(/[&<>"]/g,
          c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[c]));

        // A 401 with error 'picnic_auth' means the Picnic token is missing or
        // not 2FA-verified. Callers turn that into the login dialog.
        class PicnicAuthError extends Error {}

        async function handle(r) {
          if (r.ok) return r.json();
          const text = await r.text();
          if (r.status === 401 && text.includes('picnic_auth')) throw new PicnicAuthError(text);
          throw new Error(text);
        }
        async function jget(url) { return handle(await fetch(url)); }
        async function jpost(url, body) {
          return handle(await fetch(url, {method:'POST', headers:{'Content-Type':'application/json'},
                                          body: JSON.stringify(body ?? {})}));
        }

        // ---------------------------------------------------------------- picnic auth

        let needsCreds = false;

        async function refreshStatus() {
          try {
            const s = await jget('/api/picnic/status');
            needsCreds = s.needsCredentials;
            document.getElementById('pstatus').textContent =
              'Picnic: ' + (s.authenticated ? 'ingelogd' : 'niet ingelogd');
            return s.authenticated;
          } catch { return false; }
        }

        async function picnicLogin() {
          // No password in the environment (dev mode): collect it in the browser first.
          if (needsCreds) { document.getElementById('creds').showModal(); return; }
          await doLogin();
        }

        async function submitCreds() {
          const user = document.getElementById('cuser').value.trim();
          const pass = document.getElementById('cpass').value;
          if (!user || !pass) { alert('Vul beide velden in.'); return; }
          document.getElementById('creds').close();
          document.getElementById('cpass').value = '';
          await doLogin({user, password: pass});
        }

        async function doLogin(creds) {
          try {
            const r = await jpost('/api/picnic/login', creds);
            if (r.needs2fa) { document.getElementById('twofa').showModal(); return; }
            await refreshStatus();
            await runPending();
          } catch (e) {
            // A refused login also arrives as picnic_auth, but means wrong credentials.
            if (e.message.includes('picnic_credentials')) {
              needsCreds = true;
              document.getElementById('creds').showModal();
              return;
            }
            alert(e instanceof PicnicAuthError
              ? 'Picnic weigerde de login. Controleer de gegevens.'
              : 'Login mislukt: ' + e.message);
          }
        }

        async function send2fa(channel) {
          try {
            await jpost('/api/picnic/2fa/generate', {channel});
            document.getElementById('twofaMsg').textContent = 'Code verstuurd via ' + channel + '.';
          } catch (e) { alert('Kon geen code sturen: ' + e.message); }
        }

        async function verify2fa() {
          try {
            await jpost('/api/picnic/2fa/verify', {code: document.getElementById('otp').value.trim()});
            document.getElementById('twofa').close();
            await refreshStatus();
            await runPending();
          } catch (e) { alert('Verificatie mislukt: ' + e.message); }
        }

        // ---------------------------------------------------------------- list view

        async function loadList() {
          el.innerHTML = '<p class="muted">Laden...</p>';
          try {
            items = await jget('/api/list');
            renderList();
          } catch (e) { el.innerHTML = '<p class="bad">' + esc(e.message) + '</p>'; }
        }

        function tag(state) {
          if (state === 0) return '<span class="tag new">nieuw</span>';
          if (state === 1) return '<span class="tag linked">gekoppeld</span>';
          return '<span class="tag">niet bij Picnic</span>';
        }

        function itemRow(it, index) {
          const sub = it.state === 1 ? esc(it.picnicLabel || it.picnicUid) : esc(it.display);
          return `<div class="item" onclick="openItem(${index})">
                    <div>
                      <div class="name">${esc(it.foodName)}</div>
                      <div class="sub">${sub}${it.label ? ' &middot; ' + esc(it.label) : ''}</div>
                    </div>
                    ${tag(it.state)}
                  </div>`;
        }

        function renderList() {
          const fresh = items.filter(i => i.state === 0);
          const linked = items.filter(i => i.state === 1);
          const excluded = items.filter(i => i.state === 2);

          el.innerHTML = `
            <div class="bar">
              <button class="primary" onclick="loadList()">Haal Mealie lijst op</button>
              <button onclick="addAll()">Alles in Picnic mand</button>
              <label class="chk"><input type="checkbox" id="checkoff" checked> afvinken in Mealie</label>
              <label class="chk"><input type="checkbox" ${showExcluded ? 'checked' : ''}
                onchange="showExcluded=this.checked; renderList()"> toon uitgesloten</label>
              <button onclick="picnicLogin()">Picnic login</button>
            </div>
            <div id="basketlog" class="log"></div>

            ${fresh.length ? '<h2>Nieuw (' + fresh.length + ')</h2>' +
              fresh.map(i => itemRow(i, items.indexOf(i))).join('') : ''}

            ${linked.length ? '<h2>Gekoppeld (' + linked.length + ')</h2>' +
              linked.map(i => itemRow(i, items.indexOf(i))).join('') : ''}

            ${showExcluded && excluded.length ? '<h2>Niet bij Picnic (' + excluded.length + ')</h2>' +
              excluded.map(i => `<div class="item">
                  <div><div class="name">${esc(i.foodName)}</div>
                       <div class="sub">uitgesloten</div></div>
                  <button class="tag" onclick="include('${i.foodId}')">terugzetten</button>
                </div>`).join('') : ''}

            ${items.length === 0 ? '<p class="muted">Lijst is leeg.</p>' : ''}`;
        }

        async function include(foodId) {
          await jpost('/api/include', {foodId});
          await loadList();
        }

        // ---------------------------------------------------------------- search view

        async function openItem(index) {
          const it = items[index];
          el.innerHTML = `<div class="bar"><button onclick="renderList()">&larr; terug</button>
             <b>${esc(it.foodName)}</b></div>
             <div class="bar">
               <input id="term" value="${esc(it.foodName)}"
                      style="padding:7px 10px;border-radius:7px;border:1px solid #3a3d41;
                             background:#15171a;color:#e8eaed">
               <button onclick="search(${index})">Zoek</button>
               <button class="danger" onclick="exclude('${it.foodId}')">Niet van Picnic</button>
             </div>
             <div id="hits"><p class="muted">Zoeken...</p></div>`;
          search(index);
        }

        async function search(index) {
          const it = items[index];
          const term = document.getElementById('term')?.value || it.foodName;
          const hits = document.getElementById('hits');
          hits.innerHTML = '<p class="muted">Zoeken...</p>';
          try {
            const products = await jget('/api/search?term=' + encodeURIComponent(term));
            if (!products.length) { hits.innerHTML = '<p class="muted">Geen resultaten.</p>'; return; }
            hits.innerHTML = '<div class="grid">' + products.map(p => `
              <button class="card" onclick="openProduct(${index},'${p.id}')">
                ${p.imageId ? `<img src="/api/image/${p.imageId}" loading="lazy" alt="">`
                            : '<div style="height:104px"></div>'}
                <div class="n">${esc(p.name)}</div>
                <div class="p"><b>${esc(p.priceText)}</b> ${esc(p.unitQuantity)}</div>
              </button>`).join('') + '</div>';
          } catch (e) {
            if (e instanceof PicnicAuthError) {
              hits.innerHTML = '<p class="muted">Niet ingelogd bij Picnic. Inloggen...</p>';
              pendingRetry = () => search(index);
              await picnicLogin();
              return;
            }
            hits.innerHTML = '<p class="bad">' + esc(e.message) + '</p>';
          }
        }

        // ---------------------------------------------------------------- detail view

        async function openProduct(index, productId) {
          const it = items[index];
          el.innerHTML = '<p class="muted">Details laden...</p>';
          let detail = null;
          try { detail = await jget('/api/product/' + encodeURIComponent(productId)); }
          catch (e) { /* detail page is a nice-to-have; linking works regardless */ }

          el.innerHTML = `
            <div class="bar"><button onclick="openItem(${index})">&larr; terug</button>
              <b>${esc(productId)}</b></div>
            <div class="row">
              <div>
                <p class="muted">Koppelen aan Mealie food <b>${esc(it.foodName)}</b></p>
                <div class="bar">
                  <button class="primary" onclick="link(${index},'${productId}')">
                    Kies dit product</button>
                  <button class="danger" onclick="exclude('${it.foodId}')">Niet van Picnic</button>
                </div>
              </div>
            </div>
            <h2>Volledige API-respons</h2>
            <pre>${esc(JSON.stringify(detail?.raw ?? {}, null, 2))}</pre>`;
        }

        async function link(index, productId) {
          const it = items[index];
          try {
            await jpost('/api/link', {foodId: it.foodId, picnicUid: productId, label: ''});
            await loadList();
          } catch (e) { alert('Opslaan mislukt: ' + e.message); }
        }

        async function exclude(foodId) {
          try {
            await jpost('/api/exclude', {foodId});
            await loadList();
          } catch (e) { alert('Opslaan mislukt: ' + e.message); }
        }

        // ---------------------------------------------------------------- basket

        async function addAll() {
          const checkOff = document.getElementById('checkoff')?.checked ?? false;
          const log = document.getElementById('basketlog');
          log.innerHTML = '<div class="muted">Toevoegen...</div>';
          try {
            const r = await jpost('/api/basket', {checkOff});
            log.innerHTML = r.results.map(x => x.ok
                ? `<div class="ok">&check; ${esc(x.name)} x${x.amount}</div>`
                : `<div class="bad">&times; ${esc(x.name)}: ${esc(x.error)}</div>`).join('')
              + (r.unmapped ? `<div class="muted">${r.unmapped} nog niet gekoppeld</div>` : '');
            if (checkOff) await loadList();
          } catch (e) { log.innerHTML = '<div class="bad">' + esc(e.message) + '</div>'; }
        }

        refreshStatus().then(loadList);
        </script>
        </html>
        """;
}
