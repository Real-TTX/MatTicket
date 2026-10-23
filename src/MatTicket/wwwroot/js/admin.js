/* MatTicket: small behaviours of administration pages (AI profiles). Wired by data attributes, no inline scripts. */
(function () {
  'use strict';

  var doc = document;
  var $ = function (sel, scope) { return (scope || doc).querySelector(sel); };
  var toast = function (message, tone) { if (window.MatUi && window.MatUi.toast) { window.MatUi.toast(message, { tone: tone || 'danger' }); } };

  // ---------------------------------------------------------------------------------------- an AI profile

  var form = $('[data-ai-profile]');
  if (form) {
    var preset = $('[data-ai-preset]', form);
    var url = form.querySelector('[data-ai-url]') || doc.getElementById('Input_BaseUrl');
    var model = $('[data-ai-model]', form);
    var key = doc.getElementById('Input_ApiKey');
    var list = $('[data-ai-models-list]', form);
    var datalist = doc.getElementById('ai-models');
    var info = $('[data-ai-models-info]', form);

    // A template fills the address and suggests a model (only fields the person has not changed by hand).
    if (preset && url) {
      preset.addEventListener('change', function () {
        var option = preset.options[preset.selectedIndex];
        url.value = option.getAttribute('data-url') || '';
        if (model && option.getAttribute('data-model')) { model.value = option.getAttribute('data-model'); }
        if (option.value === 'custom') { url.focus(); }
      });
    }

    function provider() {
      var checked = form.querySelector('input[name="Input.Provider"]:checked');
      return checked ? checked.value : 'OpenAiCompatible';
    }

    // "Modelle laden": the provider's GET /models. A key typed here goes as a header, never in the address.
    var load = $('[data-ai-load-models]', form);
    if (load) {
      load.addEventListener('click', function () {
        var id = form.getAttribute('data-profile-id') || '';
        var query = '?provider=' + encodeURIComponent(provider()) + (id ? '&id=' + encodeURIComponent(id) : '') + (url && url.value ? '&baseUrl=' + encodeURIComponent(url.value) : '');
        load.disabled = true;
        if (info) { info.textContent = 'Frage den Anbieter …'; }
        fetch('/api/admin/ai/models' + query, { credentials: 'same-origin', headers: key && key.value ? { 'X-Ai-Key': key.value } : {} })
          .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); })
          .then(function (data) {
            if (data.error) { if (info) { info.textContent = data.error; } toast(data.error); return; }
            var models = data.models || [];
            datalist.innerHTML = '';
            list.innerHTML = '';
            models.forEach(function (m) {
              var o = doc.createElement('option');
              o.value = m.id;
              if (m.name && m.name !== m.id) { o.label = m.name; }
              datalist.appendChild(o);
            });
            models.slice(0, 60).forEach(function (m) {
              var li = doc.createElement('li');
              var b = doc.createElement('button');
              b.type = 'button';
              b.className = 'mat-chip';
              b.textContent = m.id + (m.free ? ' · kostenlos' : '');
              b.addEventListener('click', function () { model.value = m.id; model.dispatchEvent(new Event('change', { bubbles: true })); });
              li.appendChild(b);
              list.appendChild(li);
            });
            var free = models.filter(function (m) { return m.free; }).length;
            if (info) { info.textContent = models.length + ' Modelle' + (free ? ', davon ' + free + ' laut Anbieter kostenlos' : '') + '. Klick wählt eins.'; }
          })
          .catch(function () { toast('Die Modelle konnten nicht geladen werden.'); if (info) { info.textContent = 'Die Modelle konnten nicht geladen werden.'; } })
          .then(function () { load.disabled = false; });
      });
    }
  }

  // ---------------------------------------------------------------------------------------- the ChatGPT sign-in

  // While a code is waiting, ask every few seconds; the server polls OpenAI at most once per interval, however often we ask.
  var poll = $('[data-chatgpt-poll]');
  if (poll) {
    var state = $('[data-chatgpt-state]', poll);
    var tries = 0;
    var timer = setInterval(function () {
      if (doc.hidden || !navigator.onLine) { return; }
      if (++tries > 200) { clearInterval(timer); return; }
      fetch(poll.getAttribute('data-chatgpt-poll'), { credentials: 'same-origin', cache: 'no-store' })
        .then(function (r) { return r.ok ? r.json() : null; })
        .then(function (s) {
          if (!s) { return; }
          if (s.connected) { clearInterval(timer); location.reload(); return; }
          if (!s.pending) { clearInterval(timer); if (state) { state.textContent = s.loginError || 'Die Anmeldung ist beendet.'; } setTimeout(function () { location.reload(); }, 1500); }
        })
        .catch(function () { });
    }, 3000);
  }

  // ---------------------------------------------------------------------------------------- copy a value

  doc.addEventListener('click', function (e) {
    var b = e.target.closest && e.target.closest('[data-copy]');
    if (!b || !navigator.clipboard) { return; }
    navigator.clipboard.writeText(b.getAttribute('data-copy')).then(function () { toast('Kopiert.', 'success'); });
  });
})();
