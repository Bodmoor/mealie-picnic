namespace MealiePicnic;

/// <summary>
/// The whole UI, served as two strings. No build step, no npm, no framework.
/// </summary>
public static class Html
{
    public const string LoginPage = """
        <!doctype html>
        <html lang="nl"><meta charset="utf-8">
        <meta name="viewport" content="width=device-width,initial-scale=1">
        <link rel="icon" href="/favicon.ico" type="image/png">
        <link rel="icon" href="/icons/192.png" sizes="192x192" type="image/png">
        <link rel="apple-touch-icon" href="/icons/192.png">
        <link rel="manifest" href="/manifest.webmanifest">
        <meta name="theme-color" content="#1d2024">
        <meta name="mobile-web-app-capable" content="yes">
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
          h1 { font-size:18px; margin:0 0 3px }
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
        </style>

        <h1>Mealie &rarr; Picnic</h1>
        <div class="muted">
          <span id="pstatus">Picnic: onbekend</span>
          &middot;
          <form method="post" action="/logout" style="display:inline">
            <button type="submit" class="linklike">afmelden bij deze app</button>
          </form>
        </div>

        <div id="view"></div>

        <dialog id="creds">
          <b>Picnic inloggen</b>
          <p class="muted" id="credsMsg"></p>
          <input id="cuser" placeholder="E-mailadres" autocomplete="username">
          <input id="cpass" type="password" autocomplete="current-password">
          <div class="bar">
            <button class="primary" onclick="submitCreds()">Inloggen</button>
            <button onclick="document.getElementById('creds').close()">Sluiten</button>
          </div>
        </dialog>

        <dialog id="twofa">
          <b>Picnic 2FA</b>
          <p class="muted" id="twofaMsg">Kies waar de code naartoe moet.</p>
          <div class="bar">
            <button data-channel="SMS" data-label="SMS"
                    onclick="send2fa('SMS')">SMS</button>
            <button data-channel="EMAIL" data-label="E-mail"
                    onclick="send2fa('EMAIL')">E-mail</button>
          </div>
          <input id="otp" placeholder="Code" inputmode="numeric">
          <div class="bar">
            <button class="primary" onclick="verify2fa()">Verifieren</button>
            <button onclick="resetCooldown();document.getElementById('twofa').close()">Sluiten</button>
          </div>
        </dialog>

        <script>
        let items = [], showExcluded = false;
        let lists = [], currentList = localStorage.getItem('listId') || '';
        // Last search results, so link() can record the product name it saw.
        let lastProducts = [];

        // Whatever we were doing when Picnic asked us to authenticate, so it can
        // be resumed once login (and any 2FA) completes.
        let pendingRetry = null;

        async function runPending() {
          const retry = pendingRetry;
          pendingRetry = null;
          if (retry) await retry();
        }

        const el = document.getElementById('view');
        // RULE: every ${...} interpolation in this file is either a number we
        // produced ourselves (${index}, ${done.length}) or wrapped in esc().
        // Ids from Mealie/Picnic look safe today, but that is their invariant,
        // not ours. The single quote matters most: values are placed inside
        // single-quoted JS strings in onclick attributes.
        const esc = s => String(s ?? '').replace(/[&<>"'`\/]/g,
          c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',
                 "'":'&#39;','`':'&#96;','/':'&#47;'}[c]));

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

        // Mirrors /api/picnic/status. configuredPassword is only a flag: the
        // password itself stays on the server.
        let picnic = { authenticated: false, hasConfiguredUser: false,
                       hasConfiguredPassword: false, configuredUser: '' };

        async function refreshStatus() {
          try {
            picnic = await jget('/api/picnic/status');
          } catch {
            picnic = { authenticated: false, hasConfiguredUser: false,
                       hasConfiguredPassword: false, configuredUser: '' };
          }
          renderStatus();
          return picnic.authenticated;
        }

        function renderStatus() {
          const host = document.getElementById('pstatus');
          host.innerHTML = picnic.authenticated
            ? `Picnic: <b class="ok">ingelogd</b>
               <a href="#" onclick="picnicLogout();return false">uitloggen</a>`
            : `Picnic: <b class="bad">niet ingelogd</b>
               <a href="#" onclick="picnicLogin();return false">inloggen</a>`;
        }

        async function picnicLogout() {
          try {
            await jpost('/api/picnic/logout');
          } catch (e) { /* the token is dropped server-side regardless */ }
          await refreshStatus();
        }

        async function picnicLogin() {
          if (picnic.authenticated) return;

          // Everything present in configuration? Then no dialog is needed at all.
          if (picnic.hasConfiguredUser && picnic.hasConfiguredPassword) {
            await doLogin();
            return;
          }
          openCreds();
        }

        function openCreds() {
          // Prefill from configuration so the common case is one click.
          document.getElementById('cuser').value = picnic.configuredUser || '';

          const pass = document.getElementById('cpass');
          pass.value = '';
          pass.placeholder = picnic.hasConfiguredPassword
            ? 'Laat leeg om het ingestelde wachtwoord te gebruiken'
            : 'Wachtwoord';

          const missing = [
            picnic.hasConfiguredUser ? null : 'PICNIC_USER',
            picnic.hasConfiguredPassword ? null : 'PICNIC_PASSWORD',
          ].filter(Boolean);

          document.getElementById('credsMsg').textContent = missing.length
            ? `Niet ingesteld in de configuratie: ${missing.join(' en ')}. `
              + 'Vul aan; er wordt niets opgeslagen, alleen het token wordt bewaard.'
            : 'Gegevens komen uit de configuratie. Pas ze hier eventueel aan.';

          document.getElementById('creds').showModal();
        }

        async function submitCreds() {
          const user = document.getElementById('cuser').value.trim();
          const pass = document.getElementById('cpass').value;

          if (!user) { alert('Vul een e-mailadres in.'); return; }
          if (!pass && !picnic.hasConfiguredPassword) { alert('Vul een wachtwoord in.'); return; }

          document.getElementById('creds').close();
          document.getElementById('cpass').value = '';
          // A blank password means: use the one from configuration.
          await doLogin({user, password: pass || null});
        }

        async function doLogin(creds) {
          try {
            const r = await jpost('/api/picnic/login', creds);
            if (r.needs2fa) {
              resetCooldown();
              document.getElementById('twofaMsg').textContent = 'Kies waar de code naartoe moet.';
              document.getElementById('twofa').showModal();
              return;
            }
            await refreshStatus();
            await runPending();
          } catch (e) {
            // A refused login also arrives as picnic_auth, but means wrong credentials.
            if (e.message.includes('picnic_credentials')) {
              openCreds();
              return;
            }
            alert(e instanceof PicnicAuthError
              ? 'Picnic weigerde de login. Controleer de gegevens.'
              : 'Login mislukt: ' + e.message);
          }
        }

        // Resend cooldown: both channel buttons are locked while a code is in flight
        // and for a while after, so an impatient double-click cannot burn codes.
        const RESEND_SECONDS = 30;
        let cooldownTimer = null;

        function channelButtons() {
          return [...document.querySelectorAll('#twofa button[data-channel]')];
        }

        function startCooldown(seconds) {
          clearInterval(cooldownTimer);
          let left = seconds;

          const tick = () => {
            for (const button of channelButtons()) {
              button.disabled = left > 0;
              button.textContent = left > 0
                ? `${button.dataset.label} (${left}s)`
                : button.dataset.label;
            }
            if (left-- <= 0) clearInterval(cooldownTimer);
          };

          tick();
          cooldownTimer = setInterval(tick, 1000);
        }

        function resetCooldown() {
          clearInterval(cooldownTimer);
          for (const button of channelButtons()) {
            button.disabled = false;
            button.textContent = button.dataset.label;
          }
        }

        async function send2fa(channel) {
          const buttons = channelButtons();
          buttons.forEach(b => b.disabled = true);
          document.getElementById('twofaMsg').textContent = 'Code aanvragen...';

          try {
            await jpost('/api/picnic/2fa/generate', {channel});
            document.getElementById('twofaMsg').textContent =
              `Code verstuurd via ${channel === 'EMAIL' ? 'e-mail' : channel}.`;
            document.getElementById('otp').focus();
            startCooldown(RESEND_SECONDS);
          } catch (e) {
            // Failed to send: let them retry straight away.
            document.getElementById('twofaMsg').textContent = 'Kon geen code sturen: ' + e.message;
            resetCooldown();
          }
        }

        async function verify2fa() {
          try {
            await jpost('/api/picnic/2fa/verify', {code: document.getElementById('otp').value.trim()});
            resetCooldown();
            document.getElementById('otp').value = '';
            document.getElementById('twofa').close();
            await refreshStatus();
            await runPending();
          } catch (e) { alert('Verificatie mislukt: ' + e.message); }
        }

        // ---------------------------------------------------------------- list view

        async function loadLists() {
          try {
            const r = await jget('/api/lists');
            lists = r.lists;
            // Fall back to the configured default when nothing is remembered, or
            // when the remembered list has since been deleted in Mealie.
            if (!currentList || !lists.some(l => l.id === currentList)) {
              const fallback = lists.find(l =>
                l.name.trim().toLowerCase() === r.defaultName.trim().toLowerCase());
              currentList = (fallback ?? lists[0])?.id ?? '';
            }
          } catch { lists = []; }
        }

        function pickList(id) {
          currentList = id;
          localStorage.setItem('listId', id);
          loadList();
        }

        async function loadList() {
          el.innerHTML = '<p class="muted">Laden...</p>';
          try {
            if (!lists.length) await loadLists();
            const q = currentList ? '?listId=' + encodeURIComponent(currentList) : '';
            items = await jget('/api/list' + q);
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
          // Checked items are done: kept out of the buckets and out of the basket,
          // but still visible in a collapsed section so nothing disappears silently.
          const open = items.filter(i => !i.checked);
          const fresh = open.filter(i => i.state === 0);
          const linked = open.filter(i => i.state === 1);
          const excluded = open.filter(i => i.state === 2);
          const done = items.filter(i => i.checked);

          el.innerHTML = `
            <div class="bar">
              <select onchange="pickList(this.value)" title="Mealie lijst">
                ${lists.map(l => `<option value="${esc(l.id)}"
                  ${l.id === currentList ? 'selected' : ''}>${esc(l.name)}</option>`).join('')}
              </select>
              <button class="primary" onclick="loadList()">Haal Mealie lijst op</button>
              <button onclick="addAll()">Alles in Picnic mand</button>
              <label class="chk"><input type="checkbox" id="checkoff" checked> afvinken in Mealie</label>
              <label class="chk"><input type="checkbox" ${showExcluded ? 'checked' : ''}
                onchange="showExcluded=this.checked; renderList()"> toon uitgesloten</label>
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
                  <button class="tag" onclick="include('${esc(i.foodId)}')">terugzetten</button>
                </div>`).join('') : ''}

            ${done.length ? `
              <details class="done-block">
                <summary>Afgevinkt / toegevoegd aan mand (${done.length})</summary>
                ${done.map(i => `<div class="item done-item">
                    <div>
                      <div class="name">${esc(i.foodName)}</div>
                      <div class="sub">${esc(i.picnicLabel || i.picnicUid || i.display)}</div>
                    </div>
                    <span class="tag">afgevinkt</span>
                  </div>`).join('')}
              </details>` : ''}

            ${items.length === 0 ? '<p class="muted">Lijst is leeg.</p>' : ''}
            ${items.length > 0 && open.length === 0
              ? '<p class="muted">Alles op deze lijst is afgevinkt.</p>' : ''}`;
        }

        async function include(foodId) {
          await jpost('/api/include', {foodId});
          await loadList();
        }

        // ---------------------------------------------------------------- search view

        async function openItem(index) {
          const it = items[index];
          el.innerHTML = `<div class="bar"><button onclick="renderList()">&larr; terug</button>
             <b>${esc(it.foodName)}</b>
             ${it.picnicUid
               ? `<span class="muted">gekoppeld aan
                    ${esc(it.picnicLabel || it.picnicUid)}</span>`
               : '<span class="muted">nog niet gekoppeld</span>'}</div>
             <div class="bar">
               <input id="term" value="${esc(it.foodName)}"
                      style="padding:7px 10px;border-radius:7px;border:1px solid #3a3d41;
                             background:#15171a;color:#e8eaed">
               <button onclick="search(${index})">Zoek</button>
               <button class="danger" onclick="exclude('${esc(it.foodId)}')">Niet bij Picnic</button>
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
            lastProducts = products;
            if (!products.length) { hits.innerHTML = '<p class="muted">Geen resultaten.</p>'; return; }
            hits.innerHTML = '<div class="grid">' + products.map(p => {
              // The product this food currently points at, if any.
              const isLinked = p.id === it.picnicUid;
              return `
              <button class="card${isLinked ? ' linked' : ''}"
                      onclick="link(${index},'${esc(p.id)}')">
                ${p.imageId ? `<img src="/api/image/${esc(encodeURIComponent(p.imageId))}" loading="lazy" alt="">`
                            : '<div style="height:104px"></div>'}
                ${isLinked ? '<div class="badge">&check; gekoppeld</div>' : ''}
                <div class="n">${esc(p.name)}</div>
                <div class="p"><b>${esc(p.priceText)}</b> ${esc(p.unitQuantity)}</div>
              </button>`; }).join('') + '</div>';

            // A linked product can fall outside the current result set, e.g. after
            // renaming the food or when Picnic reshuffles its ranking.
            if (it.picnicUid && !products.some(p => p.id === it.picnicUid)) {
              hits.insertAdjacentHTML('afterbegin',
                `<p class="muted">Huidige koppeling <b>${esc(it.picnicUid)}</b>`
                + ' staat niet in deze resultaten.</p>');
            }
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

        // One click links and returns to the list. There is no confirmation step,
        // so relinking is the undo: pick another product for the same food.
        async function link(index, productId) {
          const it = items[index];
          // Keep the product name alongside the id: a bare uid is undiagnosable later.
          const label = lastProducts.find(p => p.id === productId)?.name ?? '';

          const hits = document.getElementById('hits');
          if (hits) hits.innerHTML = `<p class="muted">Koppelen aan ${esc(label || productId)}...</p>`;

          try {
            await jpost('/api/link', {foodId: it.foodId, picnicUid: productId, label});
            await loadList();
          } catch (e) {
            alert('Opslaan mislukt: ' + e.message);
            await search(index);   // put the results back so another pick is possible
          }
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
            const r = await jpost('/api/basket', {checkOff, listId: currentList});
            log.innerHTML = (r.results.length
                ? r.results.map(x => x.ok
                    ? `<div class="ok">&check; ${esc(x.name)} x${x.amount}</div>`
                    : `<div class="bad">&times; ${esc(x.name)}: ${esc(x.error)}</div>`).join('')
                : '<div class="muted">Niets toegevoegd: geen gekoppelde items op deze lijst.</div>')
              + (r.aborted ? '<div class="bad">Gestopt na herhaalde fouten; rest niet geprobeerd.</div>' : '')
              + (r.unmapped ? `<div class="muted">${r.unmapped} nog niet gekoppeld</div>` : '');
            if (checkOff) await loadList();
          } catch (e) { log.innerHTML = '<div class="bad">' + esc(e.message) + '</div>'; }
        }

        // Registering the service worker is what makes Android offer "install".
        // Requires a secure context: HTTPS, or localhost. Over plain HTTP on a LAN
        // address this silently does nothing -- expected, not an error.
        if ('serviceWorker' in navigator) {
          navigator.serviceWorker.register('/sw.js').catch(() => { /* not a secure context */ });
        }

        refreshStatus().then(loadLists).then(loadList);
        </script>
        </html>
        """;
}
