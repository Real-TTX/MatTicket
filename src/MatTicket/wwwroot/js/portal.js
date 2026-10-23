/* MatTicket: the public form. The only thing the page does by itself is to ask for the answer of an AI step once it is shown; everything
   else is a plain form that works without the script. */
(function () {
  'use strict';

  var doc = document;
  var box = doc.querySelector('[data-assist]');
  if (!box || !box.classList.contains('is-busy')) { return; }

  var form = box.closest('form');
  var state = form.querySelector('[data-form-state]');
  var body = box.querySelector('[data-assist-body]');
  var done = form.querySelector('[data-assist-done]');
  var token = form.querySelector('input[name="__RequestVerificationToken"]');

  var data = new FormData();
  data.append('State', state.value);
  if (token) { data.append('__RequestVerificationToken', token.value); }

  fetch(box.getAttribute('data-assist'), { method: 'POST', credentials: 'same-origin', body: data })
    .then(function (r) { if (!r.ok) { throw new Error('HTTP ' + r.status); } return r.json(); })
    .then(function (result) {
      box.classList.remove('is-busy');
      if (result.state) { state.value = result.state; }
      if (result.html) {
        // The server has cleaned the answer (an allowlist of tags, no links, no scripts) before it was sent.
        body.innerHTML = result.html;
        if (done) { done.disabled = false; }
      } else {
        body.textContent = (result.error || 'Eine automatische Antwort ist gerade nicht möglich.') + ' Unser Team hilft Ihnen gern weiter.';
        box.classList.remove('mat-alert--info');
        box.classList.add('mat-alert--warning');
      }
    })
    .catch(function () {
      box.classList.remove('is-busy');
      body.textContent = 'Eine automatische Antwort ist gerade nicht möglich. Unser Team hilft Ihnen gern weiter.';
    });
})();
