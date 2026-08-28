// Picnic auth/2FA and lazy product-facts loading. Everything else (the shopping
// list, search, link, exclude, basket) is server-rendered and driven by htmx --
// this file only covers what genuinely has no server round trip of its own.
//
// Registered via the 'alpine:init' event (not called eagerly) so this script can
// load in any order relative to the deferred Alpine script tag; both are
// deferred, so they still run in document order, but this is the documented-safe
// pattern regardless of that.
document.addEventListener('alpine:init', () => {
  Alpine.store('picnic', {
    authenticated: false, hasConfiguredUser: false, hasConfiguredPassword: false, configuredUser: '',

    // Whatever htmx request failed with a picnic_auth 401, so it can be replayed
    // once login (and any 2FA) completes. htmx's own request/response cycle is
    // opaque to us -- this is the equivalent of the old pendingRetry closure, but
    // driven by htmx:responseError instead of by hand at each call site, so it
    // now covers every htmx action (search, link, exclude, basket), not just search.
    pendingRequest: null,

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

    // Called both when the user clicks "inloggen" and when a request was refused
    // for lack of Picnic auth. Everything present in configuration means no
    // dialog is needed at all.
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
        ? 'Laat leeg om het ingestelde wachtwoord te gebruiken'
        : 'Wachtwoord';

      const missing = [
        this.hasConfiguredUser ? null : 'PICNIC_USER',
        this.hasConfiguredPassword ? null : 'PICNIC_PASSWORD',
      ].filter(Boolean);
      document.getElementById('credsMsg').textContent = missing.length
        ? `Niet ingesteld in de configuratie: ${missing.join(' en ')}. `
          + 'Vul aan; er wordt niets opgeslagen, alleen het token wordt bewaard.'
        : 'Gegevens komen uit de configuratie. Pas ze hier eventueel aan.';

      document.getElementById('creds').showModal();
    },

    async submitCreds() {
      const user = document.getElementById('cuser').value.trim();
      const pass = document.getElementById('cpass').value;
      if (!user) { alert('Vul een e-mailadres in.'); return; }
      if (!pass && !this.hasConfiguredPassword) { alert('Vul een wachtwoord in.'); return; }

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
          alert('Login mislukt: ' + text);
          return;
        }
        const body = JSON.parse(text);
        if (body.needs2fa) {
          this.resetCooldown();
          document.getElementById('twofaMsg').textContent = 'Kies waar de code naartoe moet.';
          document.getElementById('twofa').showModal();
          return;
        }
        await this.refresh();
        await this.runPending();
      } catch (e) {
        alert('Login mislukt: ' + e.message);
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
      document.getElementById('twofaMsg').textContent = 'Code aanvragen...';
      try {
        const r = await fetch('/api/picnic/2fa/generate', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ channel }),
        });
        if (!r.ok) throw new Error(await r.text());
        document.getElementById('twofaMsg').textContent =
          `Code verstuurd via ${channel === 'EMAIL' ? 'e-mail' : channel}.`;
        this.otpCells()[0]?.focus();
        this.startCooldown(30);
      } catch (e) {
        document.getElementById('twofaMsg').textContent = 'Kon geen code sturen: ' + e.message;
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
      } catch (e) { alert('Verificatie mislukt: ' + e.message); }
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
  Alpine.data('factsLoader', () => ({
    html: '',
    init() {
      const productId = this.$el.dataset.productId;
      const observer = new IntersectionObserver(async (entries) => {
        if (!entries[0].isIntersecting) return;
        observer.disconnect();
        try {
          const r = await fetch('/api/details/' + encodeURIComponent(productId));
          if (!r.ok) return; // a nicety on top of the pick; failing must not surface an error
          this.html = factsHtml(await r.json());
        } catch { /* same: silent */ }
      }, { rootMargin: '250px' });
      observer.observe(this.$el);
    },
  }));
});

function factsHtml(d) {
  const out = [];
  if (d.organic) {
    out.push('<img src="/icons/eu-organic.svg" alt="Biologisch"'
      + ' title="Het product wordt op de Picnic-pagina als biologisch aangeduid">');
  }
  if (typeof d.saltGramsPer100 === 'number') {
    const g = d.saltGramsPer100;
    // EU front-of-pack thresholds: 1.5 g salt per 100 g is "high", 0.3 g is "low".
    const cls = g > 1.5 ? 'salt hi' : (g <= 0.3 ? 'salt lo' : 'salt');
    const text = (Math.round(g * 100) / 100).toString().replace('.', ',');
    out.push(`<span class="${cls}" title="Zout per 100 g">${text} g zout</span>`);
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
