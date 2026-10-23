/* MatTicket: the behaviour of the app itself. MatUi (wwwroot/ui/matui.js) does menus, dialogs, tabs, validation, pickers and the upload control; this file adds
   what is specific to MatTicket: the bars of the charts, the rich-text editor, the AI suggestion of a reply, the map that loads on demand, the installed app
   (service worker, colour of the status bar) and the behaviour without a connection. Everything is wired by data attributes; no inline styles
   anywhere (a value that is data is set here as a custom property, which the content security policy allows). */
(function () {
  'use strict';

  var doc = document;
  var root = doc.documentElement;
  var $ = function (sel, scope) { return (scope || doc).querySelector(sel); };
  var $$ = function (sel, scope) { return Array.prototype.slice.call((scope || doc).querySelectorAll(sel)); };
  var toast = function (message, tone) { if (window.MatUi && window.MatUi.toast) { window.MatUi.toast(message, { tone: tone || 'danger' }); } };


  // ================================================================================================ the installed app

  // The colour of the status bar follows the theme the person chose in the app (the meta tags follow the system by themselves).
  function syncThemeColor() {
    var theme = root.getAttribute('data-theme');
    $$('meta[data-theme-color]').forEach(function (m) {
      if (!m.hasAttribute('data-media')) { m.setAttribute('data-media', m.getAttribute('media') || ''); }
      if (theme === 'light' || theme === 'dark') {
        if (m.getAttribute('data-theme-color') === theme) { m.setAttribute('media', 'all'); } else { m.setAttribute('media', 'not all'); }
      } else {
        m.setAttribute('media', m.getAttribute('data-media'));
      }
    });
  }
  syncThemeColor();
  root.addEventListener('mat:theme', syncThemeColor);

  var sw = navigator.serviceWorker;
  if (sw && root.getAttribute('data-sw')) {
    window.addEventListener('load', function () {
      sw.register('/sw.js?v=' + encodeURIComponent(root.getAttribute('data-sw')), { scope: '/' }).catch(function () { /* the app works without it */ });
    });
  }

  // The kept pages hold the data of the person who was signed in: they go with signing out, and on the sign-in page.
  function clearKeptPages() {
    if (sw && sw.controller) { sw.controller.postMessage({ type: 'clear-pages' }); }
    if (window.caches) { caches.delete('mt-pages').catch(function () { }); }
    try { Object.keys(localStorage).filter(function (k) { return k.indexOf('mt-draft:') === 0; }).forEach(function (k) { localStorage.removeItem(k); }); } catch (e) { /* ignore */ }
  }
  if (root.hasAttribute('data-clear-kept')) { clearKeptPages(); }
  doc.addEventListener('submit', function (e) {
    if (e.target && e.target.getAttribute && /\/Account\/Logout$/i.test(e.target.getAttribute('action') || '')) { clearKeptPages(); }
  }, true);

  // ================================================================================================ without a connection

  var banner = $('[data-offline-banner]');
  var copyFrom = root.getAttribute('data-offline-copy');
  var retryTimer = null;
  var retryDelay = 3000;

  function setOffline(offline) {
    root.classList.toggle('is-offline', offline);
    if (banner) { banner.hidden = !offline && copyFrom === null; }
  }

  function ping() {
    return fetch('/health', { cache: 'no-store', credentials: 'same-origin' }).then(function (r) { return r.ok; }).catch(function () { return false; });
  }

  function back() {
    clearTimeout(retryTimer);
    retryDelay = 3000;
    setOffline(false);
    // A copy that was shown without a connection is replaced by the real page: nothing stale stays on the screen.
    if (copyFrom !== null || $('[data-offline-page]')) { location.reload(); return; }
    toast('Die Verbindung ist wieder da.', 'success');
  }

  function scheduleRetry() {
    clearTimeout(retryTimer);
    retryTimer = setTimeout(function () {
      ping().then(function (ok) {
        if (ok) { back(); } else { retryDelay = Math.min(retryDelay * 2, 60000); scheduleRetry(); }
      });
    }, retryDelay);
  }

  function reconnectNow(button) {
    var status = $('[data-offline-status]');
    if (button) { button.disabled = true; }
    if (status) { status.textContent = 'Verbinde ...'; }
    ping().then(function (ok) {
      if (button) { button.disabled = false; }
      if (ok) { back(); return; }
      if (status) { status.textContent = 'Noch keine Verbindung. Es wird automatisch weiter versucht.'; }
      toast('Noch keine Verbindung.', 'warning');
      scheduleRetry();
    });
  }

  window.addEventListener('offline', function () { setOffline(true); scheduleRetry(); });
  window.addEventListener('online', function () { ping().then(function (ok) { if (ok) { back(); } else { scheduleRetry(); } }); });
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-reconnect]');
    if (b) { e.preventDefault(); reconnectNow(b); }
  });

  if (copyFrom !== null) {
    var text = $('[data-offline-text]');
    var when = copyFrom ? new Date(copyFrom) : null;
    if (text && when && !isNaN(when)) {
      text.textContent = 'Du siehst den Stand vom ' + when.toLocaleDateString('de-DE') + ', ' + when.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) + ' Uhr. Speichern ist möglich, sobald die Verbindung zurück ist.';
    }
    setOffline(true);
    scheduleRetry();
  } else if (!navigator.onLine) {
    setOffline(true);
    scheduleRetry();
  }
  if ($('[data-offline-page]')) { scheduleRetry(); }

  // A form that is sent without a connection would end in the browser's error page and lose what was typed: keep the page, say why.
  doc.addEventListener('submit', function (e) {
    var form = e.target;
    if (!form || (form.method || '').toLowerCase() !== 'post') { return; }
    if (!navigator.onLine || root.classList.contains('is-offline')) {
      e.preventDefault();
      e.stopImmediatePropagation();
      toast('Keine Verbindung: Es wurde nichts gespeichert. Deine Eingaben bleiben auf der Seite.', 'warning');
      scheduleRetry();
    }
  }, true);

  // ================================================================================================ small helpers of forms

  // <select data-submit-on-change> sends its form when it changes.
  doc.addEventListener('change', function (event) {
    var t = event.target;
    if (t && t.hasAttribute && t.hasAttribute('data-submit-on-change') && t.form) { t.form.requestSubmit ? t.form.requestSubmit() : t.form.submit(); }
  });

  // ================================================================================================ charts

  // A bar of a chart carries its share as data-bar="0..100"; it becomes a custom property (the content security policy allows no inline style).
  $$('[data-bar]').forEach(function (el) {
    var v = parseFloat(el.getAttribute('data-bar'));
    if (!isNaN(v)) { el.style.setProperty('--mt-bar', Math.max(0, Math.min(100, v)) + '%'); }
  });

  // ================================================================================================ the rich-text editor

  // <div data-rte> holds an editable area (data-rte-area) and a hidden input (data-rte-value) that the form posts. The toolbar is drawn here; the server
  // cleans whatever arrives (an allowlist of tags), so the editor only has to be convenient.
  var RTE_TOOLS = [
    ['bold', 'bold', 'Fett'], ['italic', 'italic', 'Kursiv'], ['underline', 'underline', 'Unterstrichen'],
    ['formatBlock:h3', 'heading', 'Überschrift'], ['insertUnorderedList', 'list', 'Aufzählung'], ['insertOrderedList', 'list-ordered', 'Nummerierte Liste'],
    ['formatBlock:blockquote', 'quote', 'Zitat'], ['link', 'link', 'Link'], ['removeFormat', 'eraser', 'Formatierung entfernen']
  ];

  function icon(name) {
    return '<svg class="mat-icon" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
  }

  function attachRte(box) {
    if (box.getAttribute('data-rte-ready')) { return; }
    box.setAttribute('data-rte-ready', '1');
    var area = $('[data-rte-area]', box);
    var value = $('[data-rte-value]', box);
    if (!area) { return; }
    var bar = doc.createElement('div');
    bar.className = 'mt-rte__bar';
    bar.setAttribute('role', 'toolbar');
    bar.setAttribute('aria-label', 'Formatierung');
    RTE_TOOLS.forEach(function (t) {
      var b = doc.createElement('button');
      b.type = 'button';
      b.className = 'mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm';
      b.setAttribute('data-cmd', t[0]);
      b.setAttribute('aria-label', t[2]);
      b.setAttribute('data-tip', t[2]);
      b.innerHTML = icon(t[1]);
      bar.appendChild(b);
    });
    box.insertBefore(bar, box.firstChild);
    if (value && value.value && !area.innerHTML.trim()) { area.innerHTML = value.value; }
    var sync = function () {
      if (!value) { return; }
      var text = area.textContent.replace(/\s+/g, '');
      value.value = text.length === 0 && !area.querySelector('hr') ? '' : area.innerHTML;
      box.dispatchEvent(new CustomEvent('mt:rte-change', { bubbles: true, detail: { html: value.value } }));
    };
    bar.addEventListener('mousedown', function (e) { if (e.target.closest('[data-cmd]')) { e.preventDefault(); } });
    bar.addEventListener('click', function (e) {
      var b = e.target.closest('[data-cmd]');
      if (!b) { return; }
      area.focus();
      var cmd = b.getAttribute('data-cmd');
      if (cmd === 'link') {
        var range = window.getSelection().rangeCount ? window.getSelection().getRangeAt(0).cloneRange() : null;
        var ask = window.MatUi && window.MatUi.prompt ? window.MatUi.prompt('Adresse des Links (https://…)', { value: 'https://' }) : Promise.resolve(null);
        Promise.resolve(ask).then(function (url) {
          if (!url || !/^(https?:|mailto:|tel:)/i.test(url)) { return; }
          area.focus();
          if (range) { var sel = window.getSelection(); sel.removeAllRanges(); sel.addRange(range); }
          doc.execCommand('createLink', false, url);
          sync();
        });
        return;
      }
      var parts = cmd.split(':');
      doc.execCommand(parts[0], false, parts[1] ? '<' + parts[1] + '>' : null);
      sync();
    });
    area.addEventListener('input', sync);
    area.addEventListener('blur', sync);
    // Pasted text comes as text: formatting from Word or a web page would only be thrown away by the server anyway.
    area.addEventListener('paste', function (e) {
      if (!e.clipboardData) { return; }
      e.preventDefault();
      doc.execCommand('insertText', false, e.clipboardData.getData('text/plain'));
    });
    var form = box.closest('form');
    if (form) { form.addEventListener('submit', sync, true); }
    box.mtSet = function (html) { area.innerHTML = html || ''; sync(); };
    box.mtGet = function () { sync(); return value ? value.value : area.innerHTML; };
  }

  $$('[data-rte]').forEach(attachRte);
  window.MtRte = { attach: attachRte };

  // ================================================================================================ the AI on a ticket

  function csrf() {
    var m = $('meta[name="mt-csrf"]');
    return m ? m.getAttribute('content') : '';
  }

  function post(url) {
    return fetch(url, { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': csrf() } })
      .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); });
  }

  window.MtHttp = { csrf: csrf, post: post };

  // <button data-ai-reply="/api/tickets/5/ai/reply"> asks for a suggested reply and puts it into the editor of the composer. Nothing is sent.
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-ai-reply]');
    if (!b) { return; }
    e.preventDefault();
    var form = b.closest('form');
    var box = form && $('[data-rte]', form);
    var noteBox = form && $('[data-ai-note]', form);
    var noteText = form && $('[data-ai-note-text]', form);
    var run = form && $('[data-ai-run]', form);
    if (box) { box.classList.add('is-busy'); }
    b.disabled = true;
    var label = b.querySelector('span');
    var old = label ? label.textContent : '';
    if (label) { label.textContent = 'Die KI schreibt …'; }
    post(b.getAttribute('data-ai-reply')).then(function (data) {
      if (data.error) { toast(data.error, data.status === 'LimitReached' ? 'warning' : 'danger'); return; }
      if (box && box.mtSet) { box.mtSet(data.html); }
      var reply = form && form.querySelector('input[name="Kind"][value="reply"]');
      if (reply) { reply.checked = true; }
      if (run) { run.value = data.runId; }
      if (noteBox) {
        noteBox.hidden = false;
        noteText.textContent = 'Vorschlag der KI (Lauf #' + data.runId + '). Bitte prüfen und anpassen, bevor du ihn sendest.' + (data.note ? ' Hinweis: ' + data.note : '');
      }
    }).catch(function () {
      toast('Die KI ist gerade nicht erreichbar.', 'danger');
    }).then(function () {
      if (box) { box.classList.remove('is-busy'); }
      b.disabled = false;
      if (label) { label.textContent = old; }
    });
  });

  // <button data-ai-instruction="..."> shows exactly the text that would go to the model.
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-ai-instruction]');
    if (!b) { return; }
    e.preventDefault();
    var dialog = doc.getElementById('instruction-dialog');
    var target = dialog && $('[data-instruction-text]', dialog);
    if (!dialog || !target) { return; }
    target.textContent = 'Wird geladen …';
    if (dialog.showModal && !dialog.open) { dialog.showModal(); }
    post(b.getAttribute('data-ai-instruction')).then(function (data) { target.textContent = data.text; })
      .catch(function () { target.textContent = 'Die Anweisung konnte nicht geladen werden.'; });
  });

  // ================================================================================================ the map of an address

  // Nothing goes to Google before the person asks: the button sets the address of the frame.
  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-map-load]');
    if (!b) { return; }
    var box = b.closest('[data-map]');
    var frame = box && $('[data-map-frame]', box);
    if (!frame) { return; }
    frame.src = b.getAttribute('data-map-load');
    frame.hidden = false;
    b.closest('.mat-btn-row').hidden = true;
  });

  // ================================================================================================ colours of departments

  // A dot in the colour an administrator chose (data-swatch="#rrggbb") gets it as a custom property (no inline style).
  $$('[data-swatch]').forEach(function (el) {
    var c = el.getAttribute('data-swatch');
    if (/^#[0-9a-f]{6}$/i.test(c || '')) { el.style.setProperty('--mt-swatch', c); }
  });
})();

// Scrollbars are only visible while something scrolls: the class is set on the scrolled element and removed a moment later.
(function () {
  'use strict';
  var timers = new WeakMap();
  document.addEventListener('scroll', function (event) {
    var el = event.target === document ? document.documentElement : event.target;
    if (!el || !el.classList) { return; }
    el.classList.add('is-scrolling');
    clearTimeout(timers.get(el));
    timers.set(el, setTimeout(function () { el.classList.remove('is-scrolling'); }, 900));
  }, true);
})();

