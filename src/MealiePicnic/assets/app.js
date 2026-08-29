// Picnic auth/2FA and lazy product-facts loading. Everything else (the shopping
// list, search, link, exclude, basket) is server-rendered and driven by htmx --
// this file only covers what genuinely has no server round trip of its own.
//
// Registered via the 'alpine:init' event (not called eagerly) so this script can
// load in any order relative to the deferred Alpine script tag; both are
// deferred, so they still run in document order, but this is the documented-safe
// pattern regardless of that.

// Issue #13: the strings this file writes, in the configured language. They
// arrive in a <script type="application/json"> data block the shell renders
// (Html.cs) rather than through IStringLocalizer, which cannot reach client-side
// code, and rather than a fetch, which would cost a round trip before the login
// dialog could say anything. The block is data, not script: its type is not a
// JavaScript MIME type, so the strict script-src does not apply to it.
//
// This script is deferred and the block sits above it, so it is always parsed by
// now. A missing or corrupt block is a bug in the shell, not a runtime state; it
// degrades to blank labels rather than throwing, because a TypeError here would
// take the whole Picnic login flow with it.
const strings = (() => {
  try {
    return JSON.parse(document.getElementById('i18n').textContent);
  } catch {
    return {};
  }
})();

const t = (key) => strings[key] ?? '';

document.addEventListener('alpine:init', () => {
  // Issue #22: labels the "afmelden bij deze app" button with the signed-in
  // account, so it reads "afmelden (paul@example.com)" instead of just
  // "afmelden" -- easy to lose track of who's signed in on a shared household
  // browser. null (the local/password fallback identity has no email) means
  // the button stays unlabelled.
  //
  // Refreshed from the store's own init() rather than from an x-init attribute
  // on the page (issue #43). Alpine calls init() on a store when it registers
  // one, so there is no directive expression to evaluate -- which matters,
  // because the @alpinejs/csp build parses exactly one expression per directive
  // and the old shared x-init held two calls separated by a semicolon. It threw
  // on parse, so neither store was ever refreshed and the button stayed
  // unlabelled.
  Alpine.store('me', {
    label: null,
    init() {
      this.refresh();
    },
    async refresh() {
      try {
        const r = await fetch('/api/me');
        this.label = (await r.json()).label;
      } catch {
        this.label = null;
      }
    },
  });

  Alpine.store('picnic', {
    authenticated: false, hasConfiguredUser: false, hasConfiguredPassword: false, configuredUser: '',

    // Whatever htmx request failed with a picnic_auth 401, so it can be replayed
    // once login (and any 2FA) completes. htmx's own request/response cycle is
    // opaque to us -- this is the equivalent of the old pendingRetry closure, but
    // driven by htmx:responseError instead of by hand at each call site, so it
    // now covers every htmx action (search, link, exclude, basket), not just search.
    pendingRequest: null,

    // Same as the me store: Alpine calls this on registration, so the status is
    // fetched without a directive expression standing between the two (#43).
    init() {
      this.refresh();
    },

    async refresh() {
      try {
        const r = await fetch('/api/picnic/status');
        const status = await r.json();
        Object.assign(this, status);
      } catch {
        this.authenticated = false;
      }
    },

    async logout() {
      try { await fetch('/api/picnic/logout', { method: 'POST' }); } catch { /* token dropped server-side regardless */ }
      await this.refresh();
    },

    // Called both when the user clicks the Picnic sign-in link and when a request
    // was refused for lack of Picnic auth. Everything present in configuration
    // means no dialog is needed at all.
    async promptLogin() {
      if (this.authenticated) { await this.runPending(); return; }
      if (this.hasConfiguredUser && this.hasConfiguredPassword) {
        await this.doLogin();
        return;
      }
      this.openCreds();
    },

    openCreds() {
      document.getElementById('cuser').value = this.configuredUser || '';
      const pass = document.getElementById('cpass');
      pass.value = '';
      pass.placeholder = this.hasConfiguredPassword
        ? t('passwordKeepConfigured')
        : t('passwordPlaceholder');

      const missing = [
        this.hasConfiguredUser ? null : 'PICNIC_USER',
        this.hasConfiguredPassword ? null : 'PICNIC_PASSWORD',
      ].filter(Boolean);
      document.getElementById('credsMsg').textContent = missing.length
        ? t('missingConfigBefore') + missing.join(t('and')) + t('missingConfigAfter')
        : t('credsFromConfiguration');

      document.getElementById('creds').showModal();
    },

    async submitCreds() {
      const user = document.getElementById('cuser').value.trim();
      const pass = document.getElementById('cpass').value;
      if (!user) { alert(t('enterEmail')); return; }
      if (!pass && !this.hasConfiguredPassword) { alert(t('enterPassword')); return; }

      document.getElementById('creds').close();
      document.getElementById('cpass').value = '';
      await this.doLogin({ user, password: pass || null });
    },

    async doLogin(creds) {
      try {
        const r = await fetch('/api/picnic/login', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(creds ?? {}),
        });
        const text = await r.text();
        if (!r.ok) {
          if (text.includes('picnic_credentials')) { this.openCreds(); return; }
          alert(t('loginFailed') + text);
          return;
        }
        const body = JSON.parse(text);
        if (body.needs2fa) {
          this.resetCooldown();
          document.getElementById('twofaMsg').textContent = t('chooseChannel');
          document.getElementById('twofa').showModal();
          return;
        }
        await this.refresh();
        await this.runPending();
      } catch (e) {
        alert(t('loginFailed') + e.message);
      }
    },

    channelButtons() {
      return [...document.querySelectorAll('#twofa button[data-channel]')];
    },

    // Issue #23: six single-digit cells read as "enter a 6-digit code" at a
    // glance, unlike one generic text field. They only drive the DOM (styling
    // and per-cell focus); #otp -- read by verify2fa() -- is the actual value,
    // kept in sync here so the rest of the 2FA flow does not need to change.
    otpCells() {
      return [...document.querySelectorAll('.otpcell')];
    },
    syncOtp() {
      document.getElementById('otp').value = this.otpCells().map(c => c.value).join('');
    },
    // Handles both an ordinary keystroke (one digit, current cell) and a
    // multi-digit paste or SMS autofill landing in one cell -- there is no
    // maxlength on the cells for exactly that reason, since it would otherwise
    // truncate a paste to one character before this ever sees it.
    handleOtpInput(event) {
      const digits = event.target.value.replace(/\D/g, '');
      const cells = this.otpCells();
      const start = cells.indexOf(event.target);
      [...digits].forEach((d, i) => { if (cells[start + i]) cells[start + i].value = d; });
      const next = cells[start + digits.length];
      if (next) next.focus(); else event.target.blur();
      this.syncOtp();
    },
    handleOtpBackspace(event) {
      if (event.key !== 'Backspace' || event.target.value) return;
      const cells = this.otpCells();
      const previous = cells[cells.indexOf(event.target) - 1];
      if (previous) { previous.focus(); previous.value = ''; this.syncOtp(); }
    },
    clearOtp() {
      this.otpCells().forEach(c => c.value = '');
      document.getElementById('otp').value = '';
    },

    // Both channel buttons lock while a code is in flight and for a while after,
    // so an impatient double-click cannot burn codes.
    cooldownTimer: null,
    startCooldown(seconds) {
      clearInterval(this.cooldownTimer);
      let left = seconds;
      const tick = () => {
        for (const button of this.channelButtons()) {
          button.disabled = left > 0;
          button.textContent = left > 0 ? `${button.dataset.label} (${left}s)` : button.dataset.label;
        }
        if (left-- <= 0) clearInterval(this.cooldownTimer);
      };
      tick();
      this.cooldownTimer = setInterval(tick, 1000);
    },
    resetCooldown() {
      clearInterval(this.cooldownTimer);
      for (const button of this.channelButtons()) {
        button.disabled = false;
        button.textContent = button.dataset.label;
      }
    },

    async send2fa(channel) {
      this.channelButtons().forEach(b => b.disabled = true);
      document.getElementById('twofaMsg').textContent = t('requestingCode');
      try {
        const r = await fetch('/api/picnic/2fa/generate', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ channel }),
        });
        if (!r.ok) throw new Error(await r.text());
        document.getElementById('twofaMsg').textContent = t('codeSentVia')
          .replace('{channel}', channel === 'EMAIL' ? t('emailChannelName') : channel);
        this.otpCells()[0]?.focus();
        this.startCooldown(30);
      } catch (e) {
        document.getElementById('twofaMsg').textContent = t('couldNotSendCode') + e.message;
        this.resetCooldown();
      }
    },

    async verify2fa() {
      try {
        const code = document.getElementById('otp').value.trim();
        const r = await fetch('/api/picnic/2fa/verify', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ code }),
        });
        if (!r.ok) throw new Error(await r.text());
        this.resetCooldown();
        this.clearOtp();
        document.getElementById('twofa').close();
        await this.refresh();
        await this.runPending();
      } catch (e) { alert(t('verificationFailed') + e.message); }
    },

    closeTwofa() {
      this.resetCooldown();
      this.clearOtp();
      document.getElementById('twofa').close();
    },
    closeCreds() {
      document.getElementById('creds').close();
    },

    async runPending() {
      const req = this.pendingRequest;
      this.pendingRequest = null;
      if (req) htmx.ajax(req.verb, req.path, { source: req.elt, target: req.target ?? req.elt });
    },
  });

  // Issue #6: organic mark and salt per 100 g, fetched per product card only once
  // the card is near the viewport -- a search grid can hold ninety cards, and
  // eagerly fetching facts for all of them would be ninety calls for a grid the
  // user scrolls past.
  //
  // Registered with no constructor argument: the CSP build cannot evaluate a
  // call expression like x-data="factsLoader('s123')" (issue #26), so the
  // product id instead rides along as a data-product-id attribute and is read
  // from $el once the component initialises.
  // The result is written straight to the element rather than bound through
  // x-html: the CSP build rejects that directive outright ("Using the x-html
  // directive is prohibited in the CSP build"), which is what kept the facts
  // invisible even once the fetch itself was working (issue #33). Assigning
  // innerHTML from this file is fine -- the CSP restriction is on Alpine
  // evaluating expressions, not on ordinary JS in a same-origin script -- and
  // it is what the pre-refactor code did too. The markup is built here from our
  // own endpoint's booleans and numbers, never from product text.
  Alpine.data('factsLoader', () => ({
    init() {
      const el = this.$el;
      const productId = el.dataset.productId;
      const observer = new IntersectionObserver(async (entries) => {
        if (!entries[0].isIntersecting) return;
        observer.disconnect();
        try {
          const r = await fetch('/api/details/' + encodeURIComponent(productId));
          if (!r.ok) return; // a nicety on top of the pick; failing must not surface an error
          el.innerHTML = factsHtml(await r.json());
        } catch { /* same: silent */ }
      }, { rootMargin: '250px' });
      observer.observe(el);
    },
  }));
});

// The strings here come from the #i18n block, but the escaping rule is unchanged:
// everything interpolated below is our own endpoint's booleans and numbers, or a
// translation we wrote, never product text from Picnic.
//
// That still holds for the allergen chips (issue #14) now that their labels are
// translated: the label is looked up by the group key rather than taken from the
// response, so nothing from the wire reaches the markup. A group the server knows
// and this build has no label for is skipped rather than rendered raw.
//
// A tick means Picnic marked the allergen itself, in the emphasis EU labelling
// requires. A question mark means our own word list matched ordinary ingredient
// text. They are different claims and the card must not blur them.
function factsHtml(d) {
  const out = [];
  if (d.organic) {
    out.push(`<img src="/icons/eu-organic.svg" alt="${t('organicAlt')}" title="${t('organicTitle')}">`);
  }
  const labels = strings.allergenLabels ?? {};
  for (const allergen of d.allergens ?? []) {
    const label = labels[allergen.group];
    if (!label) continue;
    const cls = allergen.declared ? 'allergen declared' : 'allergen';
    const title = allergen.declared ? t('allergenDeclared') : t('allergenSuspected');
    out.push(`<span class="${cls}" title="${title}">${label} ${allergen.declared ? '✓' : '?'}</span>`);
  }
  if (typeof d.saltGramsPer100 === 'number') {
    const g = d.saltGramsPer100;
    // EU front-of-pack thresholds: 1.5 g salt per 100 g is "high", 0.3 g is "low".
    const cls = g > 1.5 ? 'salt hi' : (g <= 0.3 ? 'salt lo' : 'salt');
    // The separator is the one the server formats prices with, so a card no
    // longer shows "€2.69" beside "1,2 g zout" (issue #13).
    const text = (Math.round(g * 100) / 100).toString()
      .replace('.', strings.decimalSeparator ?? '.');
    out.push(`<span class="${cls}" title="${t('saltTitle')}">${text} ${t('saltUnit')}</span>`);
  }
  return out.join('');
}

// A failed htmx request for lack of Picnic auth becomes the login dialog instead
// of a dead swap. Global, so it covers every htmx-driven action on the page, not
// just search (which is all the old pendingRetry flow covered).
document.body.addEventListener('htmx:responseError', (evt) => {
  const xhr = evt.detail.xhr;
  if (xhr.status !== 401 || !xhr.responseText.includes('picnic_auth')) return;

  evt.detail.isError = false; // this is handled, not a real error -- don't also log it to the console
  const store = Alpine.store('picnic');
  store.pendingRequest = {
    verb: evt.detail.requestConfig.verb,
    path: evt.detail.requestConfig.path,
    elt: evt.detail.requestConfig.elt,
    target: evt.detail.requestConfig.target,
  };
  store.promptLogin();
});

// Registering the service worker is what makes Android offer "install". Requires
// a secure context: HTTPS, or localhost. Over plain HTTP on a LAN address this
// silently does nothing -- expected, not an error.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register('/sw.js').catch(() => { /* not a secure context */ });
}
