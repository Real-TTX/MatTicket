/* MatTicket: the designer of a form.
   The page holds the definition as JSON (#designer-boot); this script edits it in memory and sends it to the server with "Speichern", where it is
   cleaned (safe HTML, known types) and checked. Two views of the same definition:
   - Inhalt: the steps as visitors will see them (WYSIWYG: texts are edited in place), a palette of blocks and the catalogue of questions on the
     left, the properties of what is selected on the right; blocks are moved by dragging (or with the buttons of a selected block).
   - Ablauf: the steps as nodes that can be dragged, the routes between them as arrows with their conditions; a node is selected to edit its routes.
   No inline styles: positions are custom properties set from here (the content security policy allows that). */
(function () {
  'use strict';

  var doc = document;
  var root = doc.querySelector('[data-designer]');
  var bootEl = doc.getElementById('designer-boot');
  if (!root || !bootEl) { return; }

  var boot = JSON.parse(bootEl.textContent);
  var def = boot.definition || {};
  def.steps = def.steps || [];
  var types = {};
  boot.types.forEach(function (t) { types[t.type] = t; });
  var kinds = {};
  boot.stepKinds.forEach(function (k) { kinds[k.kind] = k; });

  var state = { step: def.start || (def.steps[0] && def.steps[0].id) || null, block: null, view: 'content', flowStep: null, dirty: false, drag: null };

  var $ = function (sel, scope) { return (scope || doc).querySelector(sel); };
  var status = $('[data-designer-status]');
  var checkBox = $('[data-designer-check]');

  // ================================================================================================ small helpers

  function el(tag, attrs, children) {
    var e = doc.createElement(tag);
    Object.keys(attrs || {}).forEach(function (k) {
      var v = attrs[k];
      if (v === null || v === undefined || v === false) { return; }
      if (k === 'text') { e.textContent = v; } else if (k === 'html') { e.innerHTML = v; } else if (k.indexOf('on') === 0) { e.addEventListener(k.slice(2), v); } else { e.setAttribute(k, v === true ? '' : v); }
    });
    (children || []).forEach(function (c) { if (c) { e.appendChild(typeof c === 'string' ? doc.createTextNode(c) : c); } });
    return e;
  }

  function icon(name, cls) {
    var span = doc.createElement('span');
    span.innerHTML = '<svg class="mat-icon' + (cls ? ' ' + cls : '') + '" aria-hidden="true" focusable="false"><use href="#mat-i-' + name + '"></use></svg>';
    return span.firstChild;
  }

  function button(label, iconName, onclick, cls, tip) {
    return el('button', { type: 'button', 'class': 'mat-btn ' + (cls || 'mat-btn--ghost mat-btn--sm'), onclick: onclick, 'aria-label': tip || null, 'data-tip': tip || null },
      [iconName ? icon(iconName) : null, label ? el('span', { text: label }) : null]);
  }

  function toast(message, tone) { if (window.MatUi && window.MatUi.toast) { window.MatUi.toast(message, { tone: tone || 'info' }); } }

  function confirmDialog(text, ok) {
    if (window.MatUi && window.MatUi.confirm) { return Promise.resolve(window.MatUi.confirm(text, { ok: ok || 'OK' })); }
    return Promise.resolve(true);
  }

  function slugKey(text) {
    var s = (text || '').toLowerCase().replace(/ä/g, 'ae').replace(/ö/g, 'oe').replace(/ü/g, 'ue').replace(/ß/g, 'ss')
      .normalize('NFD').replace(/[̀-ͯ]/g, '').replace(/[^a-z0-9]+/g, '_').replace(/^_+|_+$/g, '');
    if (!/^[a-z]/.test(s)) { s = 'f_' + s; }
    return s.slice(0, 40).replace(/_+$/, '') || 'feld';
  }

  function stepById(id) { return def.steps.find(function (s) { return s.id === id; }) || null; }

  function currentStep() { return stepById(state.step) || def.steps[0] || null; }

  function blockById(step, id) { return step ? step.blocks.find(function (b) { return b.id === id; }) || null : null; }

  function questions() {
    var list = [];
    def.steps.forEach(function (s) { s.blocks.forEach(function (b) { if (types[b.type] && types[b.type].question) { list.push(b); } }); });
    return list;
  }

  function uniqueKey(base, except) {
    var taken = questions().filter(function (b) { return b !== except; }).map(function (b) { return b.key; });
    var key = slugKey(base);
    var n = 2;
    while (taken.indexOf(key) >= 0) { key = slugKey(base).slice(0, 36) + '_' + n++; }
    return key;
  }

  function uniqueStepId(base) {
    var id = slugKey(base).replace(/_/g, '-');
    var n = 2;
    var candidate = id;
    while (stepById(candidate)) { candidate = id + '-' + n++; }
    return candidate;
  }

  function uniqueBlockId(step) {
    var n = step.blocks.length + 1;
    var id = step.id + '-b' + n;
    while (blockById(step, id)) { id = step.id + '-b' + (++n); }
    return id;
  }

  function markDirty() {
    state.dirty = true;
    if (status) { status.textContent = 'Nicht gespeicherte Änderungen.'; }
  }

  function optionsOf(block) {
    if (block.type === 'yesno') { return [{ value: 'ja', label: 'Ja' }, { value: 'nein', label: 'Nein' }]; }
    if (block.type === 'category') { return boot.categories.map(function (c) { return { value: String(c.id), label: c.name }; }); }
    return block.options || [];
  }

  // ================================================================================================ creating things

  function newBlock(type, from) {
    var step = currentStep();
    var t = types[type];
    var b = { id: uniqueBlockId(step), type: type };
    if (from) {
      Object.keys(from).forEach(function (k) { b[k] = JSON.parse(JSON.stringify(from[k])); });
      b.id = uniqueBlockId(step);
      if (t.question) { b.key = uniqueKey(from.key || from.label || 'feld'); }
      return b;
    }

    if (type === 'richtext') { b.html = '<p>Text eingeben …</p>'; return b; }
    if (type === 'divider') { return b; }
    var label = { consent: 'Ich bin einverstanden, dass meine Angaben zur Bearbeitung gespeichert werden.', category: 'Worum geht es?', yesno: 'Ja oder nein?', email: 'E-Mail-Adresse', phone: 'Telefon', date: 'Datum' }[type] || 'Neue Frage';
    b.label = label;
    b.key = uniqueKey(type === 'email' ? 'email' : type === 'phone' ? 'telefon' : label);
    b.required = type === 'consent';
    b.options = t.options ? [{ value: 'option_1', label: 'Option 1' }, { value: 'option_2', label: 'Option 2' }] : [];
    if (type === 'email') { b.mapTo = questions().some(function (q) { return q.mapTo === 'requester.email'; }) ? null : 'requester.email'; }
    return b;
  }

  function insertBlock(block, index) {
    var step = currentStep();
    if (!step) { return; }
    if (index === undefined || index === null) {
      var selected = step.blocks.findIndex(function (b) { return b.id === state.block; });
      index = selected >= 0 ? selected + 1 : step.blocks.length;
    }
    step.blocks.splice(index, 0, block);
    state.block = block.id;
    markDirty();
    renderAll();
  }

  function addStep(kind) {
    var k = kinds[kind];
    var maxX = def.steps.reduce(function (m, s) { return Math.max(m, s.x || 0); }, 0);
    var step = {
      id: uniqueStepId(kind === 'page' ? 'seite' : kind === 'ai' ? 'ki' : kind === 'ticket' ? 'ticket' : 'ende'),
      kind: kind,
      title: { page: 'Neue Seite', ai: 'Vorschlag unseres Assistenten', ticket: 'Ihre Kontaktdaten', end: 'Vielen Dank!' }[kind] || k.label,
      blocks: [],
      next: [],
      x: maxX + 300,
      y: 60 + (def.steps.length % 3) * 150,
    };
    if (kind === 'ticket') {
      step.buttonText = 'Anfrage senden';
      step.doneHtml = '<p>Vielen Dank! Ihre Anfrage ist bei uns angekommen.</p>';
      step.blocks.push({ id: step.id + '-b1', type: 'text', key: uniqueKey('name'), label: 'Name', required: true, mapTo: 'requester.name', width: 'half', options: [] });
      step.blocks.push({ id: step.id + '-b2', type: 'email', key: uniqueKey('email'), label: 'E-Mail-Adresse', required: true, mapTo: 'requester.email', width: 'half', options: [] });
    }
    if (kind === 'end') { step.doneHtml = '<p>Schön, dass wir helfen konnten.</p>'; }
    if (kind === 'ai') { step.helpedText = 'Das hat geholfen'; step.notHelpedText = 'Weiter zum Ticket'; }

    // The selected step goes on to the new one when it had nowhere to go.
    var from = currentStep();
    if (from && from.kind !== 'ticket' && from.kind !== 'end' && from.next.length === 0) { from.next.push({ to: step.id }); }
    def.steps.push(step);
    if (!def.start) { def.start = step.id; }
    state.step = step.id;
    state.flowStep = step.id;
    state.block = null;
    markDirty();
    renderAll();
  }

  // ================================================================================================ the list of steps

  function renderSteps() {
    var list = $('[data-steps]', root);
    list.innerHTML = '';
    def.steps.forEach(function (s, i) {
      var k = kinds[s.kind] || kinds.page;
      var li = el('li', { 'class': 'mt-steplist__item' + (s.id === state.step ? ' is-active' : '') }, [
        el('button', { type: 'button', 'class': 'mt-steplist__main', 'aria-current': s.id === state.step ? 'true' : null, onclick: function () { state.step = s.id; state.block = null; renderAll(); } }, [
          icon(k.icon),
          el('span', { 'class': 'mt-steplist__text' }, [el('span', { 'class': 'mt-steplist__title', text: s.title || s.id }), el('small', { 'class': 'mat-muted', text: k.label + (s.id === def.start ? ' · Start' : '') })]),
        ]),
        el('span', { 'class': 'mt-steplist__tools' }, [
          i > 0 ? button(null, 'arrow-up', function () { def.steps.splice(i, 1); def.steps.splice(i - 1, 0, s); markDirty(); renderSteps(); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Nach oben') : null,
        ]),
      ]);
      list.appendChild(li);
    });

    var add = $('[data-add-step]', root);
    add.innerHTML = '';
    boot.stepKinds.forEach(function (k) {
      add.appendChild(button(k.label, k.icon, function () { addStep(k.kind); }, 'mat-btn--secondary mat-btn--sm'));
    });
  }

  // ================================================================================================ the palette and the catalogue

  function renderPalette() {
    var box = $('[data-palette]', root);
    box.innerHTML = '';
    boot.types.forEach(function (t) {
      var b = el('button', { type: 'button', 'class': 'mt-palette__item', draggable: 'true', 'data-new-type': t.type, onclick: function () { insertBlock(newBlock(t.type)); } }, [icon(t.icon), el('span', { text: t.label })]);
      b.addEventListener('dragstart', function (e) { state.drag = { type: t.type }; e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', t.type); });
      box.appendChild(b);
    });
  }

  function renderCatalog() {
    var box = $('[data-catalog]', root);
    var filter = ($('[data-catalog-search]', root).value || '').toLowerCase();
    box.innerHTML = '';
    var shown = boot.questions.filter(function (q) { return !filter || (q.label || '').toLowerCase().indexOf(filter) >= 0 || (q.key || '').indexOf(filter) >= 0; });
    if (shown.length === 0) { box.appendChild(el('p', { 'class': 'mat-hint', text: boot.questions.length === 0 ? 'Der Katalog ist leer.' : 'Nichts gefunden.' })); return; }
    shown.forEach(function (q) {
      var t = types[q.type] || types.text;
      var b = el('button', { type: 'button', 'class': 'mt-palette__item', draggable: 'true', title: q.label, onclick: function () { insertBlock(newBlock(q.type, q)); } }, [icon(t.icon), el('span', { text: q.label })]);
      b.addEventListener('dragstart', function (e) { state.drag = { question: q }; e.dataTransfer.effectAllowed = 'copy'; e.dataTransfer.setData('text/plain', q.key); });
      box.appendChild(b);
    });
  }

  $('[data-catalog-search]', root).addEventListener('input', renderCatalog);

  // ================================================================================================ the canvas (what visitors see)

  function rte(html, onchange, small) {
    var value = el('input', { type: 'hidden', 'data-rte-value': true });
    value.value = html || '';
    var area = el('div', { 'class': 'mt-rte__area mt-richtext' + (small ? ' mt-rte__area--sm' : ''), contenteditable: 'true', role: 'textbox', 'aria-multiline': 'true', 'aria-label': 'Text', 'data-rte-area': true, 'data-placeholder': 'Text eingeben …' });
    var box = el('div', { 'class': 'mt-rte', 'data-rte': true }, [value, area]);
    if (window.MtRte) { window.MtRte.attach(box); }
    box.addEventListener('mt:rte-change', function (e) { onchange(e.detail.html); });
    return box;
  }

  function fieldPreview(b) {
    var t = types[b.type];
    var full = b.width !== 'half';
    var label = function (forId) {
      return el(forId ? 'label' : 'span', { 'class': 'mat-label', 'for': forId || null }, [b.label || '', b.required ? el('span', { 'class': 'mat-req', 'aria-hidden': 'true', text: ' *' }) : null]);
    };
    var hint = b.help ? el('span', { 'class': 'mat-hint', text: b.help }) : null;
    var id = 'pv-' + b.id;
    var wrap;
    switch (b.type) {
      case 'radio':
      case 'yesno':
      case 'checkboxes':
        wrap = el('fieldset', { 'class': 'mat-field mat-fieldset mat-field--full' }, [el('legend', { 'class': 'mat-label', text: (b.label || '') + (b.required ? ' *' : '') })]);
        var opts = el('div', { 'class': 'mat-options' });
        optionsOf(b).forEach(function (o) {
          opts.appendChild(el('label', { 'class': 'mat-option' }, [el('input', { type: b.type === 'checkboxes' ? 'checkbox' : 'radio', tabindex: '-1', name: id }), el('span', { text: o.label })]));
        });
        wrap.appendChild(opts);
        if (hint) { wrap.appendChild(hint); }
        return wrap;
      case 'consent':
        return el('div', { 'class': 'mat-field mat-field--full' }, [el('label', { 'class': 'mat-check' }, [el('input', { type: 'checkbox', tabindex: '-1' }), el('span', { 'class': 'mat-check__text', text: (b.label || '') + (b.required ? ' *' : '') })]), hint]);
      case 'select':
      case 'category':
        var sel = el('select', { 'class': 'mat-select', id: id, tabindex: '-1' }, [el('option', { text: 'Bitte wählen' })]);
        optionsOf(b).slice(0, 30).forEach(function (o) { sel.appendChild(el('option', { text: o.label })); });
        return el('div', { 'class': 'mat-field' + (full ? ' mat-field--full' : '') }, [label(id), sel, hint]);
      case 'textarea':
        return el('div', { 'class': 'mat-field mat-field--full' }, [label(id), el('textarea', { 'class': 'mat-textarea', id: id, rows: '4', tabindex: '-1', placeholder: b.placeholder || null }), hint]);
      default:
        var inputType = { email: 'email', phone: 'tel', number: 'number', date: 'date' }[b.type] || 'text';
        return el('div', { 'class': 'mat-field' + (full ? ' mat-field--full' : '') }, [label(id), el('input', { 'class': 'mat-input', id: id, type: inputType, tabindex: '-1', placeholder: b.placeholder || null }), hint]);
    }
    return wrap;
  }

  function blockTools(step, b, index) {
    return el('div', { 'class': 'mt-dblock__tools' }, [
      index > 0 ? button(null, 'arrow-up', function (e) { e.stopPropagation(); step.blocks.splice(index, 1); step.blocks.splice(index - 1, 0, b); markDirty(); renderCanvas(); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Nach oben') : null,
      index < step.blocks.length - 1 ? button(null, 'arrow-down', function (e) { e.stopPropagation(); step.blocks.splice(index, 1); step.blocks.splice(index + 1, 0, b); markDirty(); renderCanvas(); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Nach unten') : null,
      button(null, 'copy', function (e) { e.stopPropagation(); insertBlock(newBlock(b.type, b), index + 1); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Verdoppeln'),
      button(null, 'trash', function (e) {
        e.stopPropagation();
        step.blocks.splice(index, 1);
        state.block = null;
        markDirty();
        renderAll();
      }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Entfernen'),
    ]);
  }

  function renderCanvas() {
    var canvas = $('[data-canvas]', root);
    canvas.innerHTML = '';
    var step = currentStep();
    if (!step) {
      canvas.appendChild(el('div', { 'class': 'mat-empty' }, [el('span', { 'class': 'mat-empty__icon' }, [icon('layers')]), el('div', { 'class': 'mat-empty__title', text: 'Noch kein Schritt' }), el('p', { text: 'Lege links einen Schritt an.' })]));
      return;
    }

    if (step.id === def.start) {
      canvas.appendChild(el('h1', { 'class': 'mt-pf__formtitle', text: def.title || boot.name }));
      var intro = el('div', { 'class': 'mt-dblock mt-dblock--intro', 'data-block': '__intro' }, [el('span', { 'class': 'mt-dblock__label', text: 'Einleitung (nur am Anfang)' }), rte(def.intro, function (html) { def.intro = html; markDirty(); }, true)]);
      canvas.appendChild(intro);
    }

    canvas.appendChild(el('h2', { 'class': 'mt-pf__title', text: step.title || '' }));
    var grid = el('div', { 'class': 'mat-form__grid mt-dgrid', 'data-drop': true });
    step.blocks.forEach(function (b, index) {
      var t = types[b.type] || types.text;
      var selected = b.id === state.block;
      var wrap = el('div', { 'class': 'mt-dblock' + (selected ? ' is-selected' : '') + (b.width === 'half' && t.question && ['radio', 'checkboxes', 'yesno', 'textarea', 'consent'].indexOf(b.type) < 0 ? '' : ' mat-field--full'), 'data-block': b.id, draggable: 'true', tabindex: '0', role: 'button', 'aria-pressed': selected ? 'true' : 'false', 'aria-label': t.label + ': ' + (b.label || '') });
      wrap.appendChild(el('span', { 'class': 'mt-dblock__label' }, [icon(t.icon), el('span', { text: t.label + (b.key ? ' · ' + b.key : '') + (b.mapTo ? ' → ' + (boot.mapTargets.find(function (m) { return m.value === b.mapTo; }) || {}).label : '') })]));
      if (b.type === 'richtext') {
        wrap.appendChild(rte(b.html, function (html) { b.html = html; markDirty(); }));
      } else if (b.type === 'divider') {
        wrap.appendChild(el('hr', { 'class': 'mt-pf__divider' }));
      } else {
        wrap.appendChild(fieldPreview(b));
      }
      if (selected) { wrap.appendChild(blockTools(step, b, index)); }
      wrap.addEventListener('click', function (e) {
        if (e.target.closest('[data-rte]')) { if (state.block !== b.id) { state.block = b.id; renderProps(); markSelected(); } return; }
        state.block = b.id;
        renderCanvas();
        renderProps();
      });
      wrap.addEventListener('keydown', function (e) { if ((e.key === 'Enter' || e.key === ' ') && e.target === wrap) { e.preventDefault(); state.block = b.id; renderCanvas(); renderProps(); } });
      wrap.addEventListener('dragstart', function (e) {
        if (e.target.closest && e.target.closest('[data-rte-area]')) { return; }
        state.drag = { move: b.id };
        e.dataTransfer.effectAllowed = 'move';
        e.dataTransfer.setData('text/plain', b.id);
        wrap.classList.add('is-dragging');
      });
      wrap.addEventListener('dragend', function () { wrap.classList.remove('is-dragging'); clearDropMarks(); });
      grid.appendChild(wrap);
    });
    if (step.blocks.length === 0) {
      grid.appendChild(el('div', { 'class': 'mt-dempty mat-field--full', text: step.kind === 'ai' ? 'Ein KI-Schritt braucht keine Bausteine: hier steht später die Antwort des Assistenten. Ziehe Bausteine hierher, wenn du vorher etwas sagen willst.' : 'Ziehe Bausteine hierher oder klicke links auf einen.' }));
    }
    canvas.appendChild(grid);
    canvas.appendChild(stepFooter(step));
    wireDrop(grid, step);
  }

  function markSelected() {
    root.querySelectorAll('.mt-dblock').forEach(function (d) { d.classList.toggle('is-selected', d.getAttribute('data-block') === state.block); });
  }

  function stepFooter(step) {
    var box = el('div', { 'class': 'mt-pf__foot' });
    if (step.kind === 'ai') {
      box.appendChild(el('div', { 'class': 'mat-alert mat-alert--info mt-pf__ai' }, [icon('sparkles'), el('div', { 'class': 'mat-alert__body', text: 'Hier erscheint die Antwort des Assistenten auf die bisherigen Angaben.' + (step.instruction ? ' Zusatzanweisung: ' + step.instruction : '') })]));
      box.appendChild(el('div', { 'class': 'mat-btn-row' }, [
        el('span', { 'class': 'mat-btn mat-btn--primary' }, [icon('check'), el('span', { text: step.helpedText || 'Das hat geholfen' })]),
        el('span', { 'class': 'mat-btn mat-btn--secondary' }, [icon('ticket'), el('span', { text: step.notHelpedText || 'Weiter zum Ticket' })]),
      ]));
      return box;
    }
    if (step.kind === 'end') {
      box.appendChild(el('span', { 'class': 'mt-dblock__label', text: 'Abschlusstext' }));
      box.appendChild(rte(step.doneHtml, function (html) { step.doneHtml = html; markDirty(); }, true));
      return box;
    }
    var row = el('div', { 'class': 'mat-btn-row' }, [
      el('span', { 'class': 'mat-btn mat-btn--primary' }, [icon(step.kind === 'ticket' ? 'send' : 'arrow-right'), el('span', { text: step.buttonText || (step.kind === 'ticket' ? 'Absenden' : 'Weiter') })]),
      step.id !== def.start ? el('span', { 'class': 'mat-btn mat-btn--secondary' }, [icon('back'), el('span', { text: 'Zurück' })]) : null,
    ]);
    box.appendChild(row);
    if (step.kind === 'ticket') {
      box.appendChild(el('span', { 'class': 'mt-dblock__label', text: 'Text nach dem Absenden (dazu kommen Ticketnummer und Link)' }));
      box.appendChild(rte(step.doneHtml, function (html) { step.doneHtml = html; markDirty(); }, true));
    }
    return box;
  }

  function clearDropMarks() { root.querySelectorAll('.is-drop-before, .is-drop-after').forEach(function (d) { d.classList.remove('is-drop-before', 'is-drop-after'); }); }

  function dropIndex(grid, y) {
    var blocks = Array.prototype.slice.call(grid.querySelectorAll(':scope > .mt-dblock'));
    for (var i = 0; i < blocks.length; i++) {
      var r = blocks[i].getBoundingClientRect();
      if (y < r.top + r.height / 2) { return { index: i, el: blocks[i], before: true }; }
    }
    return { index: blocks.length, el: blocks[blocks.length - 1] || null, before: false };
  }

  function wireDrop(grid, step) {
    grid.addEventListener('dragover', function (e) {
      if (!state.drag) { return; }
      e.preventDefault();
      clearDropMarks();
      var at = dropIndex(grid, e.clientY);
      if (at.el) { at.el.classList.add(at.before ? 'is-drop-before' : 'is-drop-after'); }
    });
    grid.addEventListener('dragleave', function (e) { if (!grid.contains(e.relatedTarget)) { clearDropMarks(); } });
    grid.addEventListener('drop', function (e) {
      if (!state.drag) { return; }
      e.preventDefault();
      var at = dropIndex(grid, e.clientY);
      var drag = state.drag;
      state.drag = null;
      clearDropMarks();
      if (drag.move) {
        var from = step.blocks.findIndex(function (b) { return b.id === drag.move; });
        if (from < 0) { return; }
        var moved = step.blocks.splice(from, 1)[0];
        step.blocks.splice(at.index > from ? at.index - 1 : at.index, 0, moved);
        state.block = moved.id;
        markDirty();
        renderCanvas();
        return;
      }
      insertBlock(drag.question ? newBlock(drag.question.type, drag.question) : newBlock(drag.type), at.index);
    });
  }

  doc.addEventListener('dragend', function () { state.drag = null; clearDropMarks(); });

  // ================================================================================================ properties

  var uid = 0;
  function field(label, control, hint, full) {
    var id = control.id || ('p' + (++uid));
    control.id = id;
    return el('div', { 'class': 'mat-field' + (full === false ? '' : ' mat-field--full') }, [el('label', { 'class': 'mat-label', 'for': id, text: label }), control, hint ? el('span', { 'class': 'mat-hint', text: hint }) : null]);
  }

  function textInput(value, oninput, attrs) {
    var input = el('input', Object.assign({ 'class': 'mat-input', type: 'text' }, attrs || {}));
    input.value = value || '';
    input.addEventListener('input', function () { oninput(input.value); markDirty(); });
    return input;
  }

  function textArea(value, oninput, rows) {
    var area = el('textarea', { 'class': 'mat-textarea', rows: String(rows || 3) });
    area.value = value || '';
    area.addEventListener('input', function () { oninput(area.value); markDirty(); });
    return area;
  }

  function select(options, value, onchange, empty) {
    var s = el('select', { 'class': 'mat-select' });
    if (empty !== undefined) { s.appendChild(el('option', { value: '', text: empty })); }
    options.forEach(function (o) { var opt = el('option', { value: String(o.value), text: o.label }); if (String(o.value) === String(value === null || value === undefined ? '' : value)) { opt.selected = true; } s.appendChild(opt); });
    s.addEventListener('change', function () { onchange(s.value); markDirty(); });
    return s;
  }

  function toggle(label, value, onchange) {
    var input = el('input', { type: 'checkbox', value: 'true' });
    input.checked = !!value;
    input.addEventListener('change', function () { onchange(input.checked); markDirty(); });
    return el('div', { 'class': 'mat-field mat-field--full' }, [el('label', { 'class': 'mat-switch' }, [input, el('span', { 'class': 'mat-switch__track' }), el('span', { text: label })])]);
  }

  function renderProps() {
    var box = $('[data-props]', root);
    var title = $('[data-props-title]', root);
    box.innerHTML = '';
    var step = currentStep();
    if (!step) { title.textContent = 'Eigenschaften'; return; }
    var b = blockById(step, state.block);
    if (b) {
      title.textContent = 'Baustein: ' + (types[b.type] || {}).label;
      blockProps(box, step, b);
    } else {
      title.textContent = 'Schritt';
      stepProps(box, step, false);
    }
  }

  function blockProps(box, step, b) {
    var t = types[b.type];
    if (b.type === 'richtext') {
      box.appendChild(el('p', { 'class': 'mat-hint', text: 'Den Text bearbeitest du direkt in der Mitte. Fett, Listen und Links über die Leiste.' }));
    } else if (b.type === 'divider') {
      box.appendChild(el('p', { 'class': 'mat-hint', text: 'Eine Trennlinie zwischen zwei Teilen des Schritts.' }));
    } else {
      var grid = el('div', { 'class': 'mat-form__grid' });
      var keyEdited = !!b.keyEdited;
      var keyInput = textInput(b.key, function (v) { b.key = slugKey(v); keyEdited = true; b.keyEdited = true; renderCanvasSoon(); }, { 'class': 'mat-input mat-mono' });
      grid.appendChild(field('Beschriftung', textArea(b.label, function (v) {
        b.label = v;
        if (!keyEdited && b.questionId === undefined) { b.key = uniqueKey(v, b); keyInput.value = b.key; }
        renderCanvasSoon();
      }, 2)));
      grid.appendChild(field('Schlüssel', keyInput, 'Unter diesem Namen steht die Antwort im Ticket und in Bedingungen. Platzhalter im Betreff: {' + (b.key || 'schluessel') + '}'));
      grid.appendChild(field('Art', select(boot.types.filter(function (x) { return x.question; }).map(function (x) { return { value: x.type, label: x.label }; }), b.type, function (v) {
        b.type = v;
        if (types[v].options && (!b.options || b.options.length === 0)) { b.options = [{ value: 'option_1', label: 'Option 1' }, { value: 'option_2', label: 'Option 2' }]; }
        renderCanvas(); renderProps();
      })));
      if (['text', 'textarea', 'email', 'phone', 'number'].indexOf(b.type) >= 0) {
        grid.appendChild(field('Platzhalter', textInput(b.placeholder, function (v) { b.placeholder = v; renderCanvasSoon(); })));
      }
      grid.appendChild(field('Hilfetext', textInput(b.help, function (v) { b.help = v; renderCanvasSoon(); })));
      grid.appendChild(toggle('Pflichtfeld', b.required, function (v) { b.required = v; renderCanvas(); }));
      if (t.options) { grid.appendChild(optionsEditor(b)); }
      if (b.type === 'category') { grid.appendChild(el('p', { 'class': 'mat-hint mat-field--full', text: 'Die Auswahl sind die aktiven Kategorien. Die gewählte Kategorie bestimmt Abteilung und Team des Tickets.' })); }
      grid.appendChild(field('Geht auch in', select(boot.mapTargets, b.mapTo || '', function (v) { b.mapTo = v || null; renderCanvas(); }, 'Nur in die Angaben'), 'Füllt z. B. den Namen oder die E-Mail des Anfragenden, den Betreff oder die Beschreibung.'));
      if (['text', 'email', 'phone', 'number', 'date', 'select', 'category'].indexOf(b.type) >= 0) {
        grid.appendChild(field('Breite', select([{ value: 'full', label: 'Ganze Breite' }, { value: 'half', label: 'Halbe Breite' }], b.width === 'half' ? 'half' : 'full', function (v) { b.width = v === 'half' ? 'half' : null; renderCanvas(); })));
      }
      if (b.questionId) { grid.appendChild(el('p', { 'class': 'mat-hint mat-field--full', text: 'Aus dem Fragenkatalog übernommen; Änderungen hier gelten nur für dieses Formular.' })); }
      box.appendChild(grid);
    }
    box.appendChild(el('div', { 'class': 'mat-btn-row' }, [
      button('Zum Schritt', 'back', function () { state.block = null; renderCanvas(); renderProps(); }, 'mat-btn--secondary mat-btn--sm'),
      el('span', { 'class': 'mat-btn-row__spacer' }),
      button('Entfernen', 'trash', function () { var i = step.blocks.indexOf(b); step.blocks.splice(i, 1); state.block = null; markDirty(); renderAll(); }, 'mat-btn--danger mat-btn--sm'),
    ]));
  }

  var canvasTimer = null;
  function renderCanvasSoon() { clearTimeout(canvasTimer); canvasTimer = setTimeout(function () { renderCanvas(); if (state.view === 'flow') { renderFlow(); } }, 250); }

  function optionsEditor(b) {
    var wrap = el('div', { 'class': 'mat-field mat-field--full' }, [el('span', { 'class': 'mat-label', text: 'Auswahlmöglichkeiten' })]);
    var list = el('ul', { 'class': 'mt-optlist' });
    function draw() {
      list.innerHTML = '';
      b.options.forEach(function (o, i) {
        var label = textInput(o.label, function (v) {
          var auto = !o.valueEdited;
          o.label = v;
          if (auto) { o.value = slugKey(v); value.value = o.value; }
          renderCanvasSoon();
        }, { 'aria-label': 'Beschriftung der Option ' + (i + 1) });
        var value = textInput(o.value, function (v) { o.value = slugKey(v); o.valueEdited = true; }, { 'class': 'mat-input mat-input--sm mat-mono', 'aria-label': 'Wert der Option ' + (i + 1) });
        list.appendChild(el('li', { 'class': 'mt-optlist__row' }, [label, value, button(null, 'x', function () { b.options.splice(i, 1); markDirty(); draw(); renderCanvas(); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Option entfernen')]));
      });
    }
    draw();
    wrap.appendChild(list);
    wrap.appendChild(button('Option hinzufügen', 'plus', function () { var n = b.options.length + 1; b.options.push({ value: 'option_' + n, label: 'Option ' + n }); markDirty(); draw(); renderCanvas(); }, 'mat-btn--secondary mat-btn--sm'));
    wrap.appendChild(el('span', { 'class': 'mat-hint', text: 'Links die Beschriftung, rechts der Wert für Bedingungen.' }));
    return wrap;
  }

  function stepProps(box, step, inFlow) {
    var grid = el('div', { 'class': 'mat-form__grid' });
    grid.appendChild(field('Titel', textInput(step.title, function (v) { step.title = v; renderSteps(); renderCanvasSoon(); })));
    grid.appendChild(field('Art', select(boot.stepKinds.map(function (k) { return { value: k.kind, label: k.label }; }), step.kind, function (v) {
      step.kind = v;
      if ((v === 'ticket' || v === 'end') && !step.doneHtml) { step.doneHtml = '<p>Vielen Dank!</p>'; }
      renderAll();
    })));
    if (step.kind === 'page' || step.kind === 'ticket') {
      grid.appendChild(field('Text des Knopfs', textInput(step.buttonText, function (v) { step.buttonText = v; renderCanvasSoon(); }, { placeholder: step.kind === 'ticket' ? 'Absenden' : 'Weiter' })));
    }
    if (step.kind === 'ai') {
      grid.appendChild(field('Anweisung für diesen Schritt', textArea(step.instruction, function (v) { step.instruction = v; renderCanvasSoon(); }, 4), 'Zusätzlich zur Grundanweisung, z. B. wobei der Assistent helfen soll und wobei nicht. Der Assistent sieht nur, was in diesem Formular eingegeben wurde.'));
      grid.appendChild(field('Knopf „geholfen“', textInput(step.helpedText, function (v) { step.helpedText = v; renderCanvasSoon(); }, { placeholder: 'Das hat geholfen' }), null, false));
      grid.appendChild(field('Knopf „nicht geholfen“', textInput(step.notHelpedText, function (v) { step.notHelpedText = v; renderCanvasSoon(); }, { placeholder: 'Weiter zum Ticket' }), null, false));
      grid.appendChild(el('p', { 'class': 'mat-hint mat-field--full', text: 'Für die Wege: die Bedingung „Ergebnis der KI ist gleich solved“ (geholfen) oder „open“ (nicht geholfen).' }));
    }
    if (step.kind === 'ticket') {
      grid.appendChild(field('Betreff des Tickets', textInput(step.subjectTemplate, function (v) { step.subjectTemplate = v; }, { placeholder: '{bereich}: {kurz}' }), 'Mit Platzhaltern der Antworten. Leer: die Frage, die in den Betreff geht, sonst der Name des Formulars.'));
      grid.appendChild(field('Priorität', select(boot.priorities, step.priority || '', function (v) { step.priority = v || null; }, 'Normal (oder aus einer Frage)'), null, false));
      grid.appendChild(field('Kategorie', select(boot.categories.map(function (c) { return { value: c.id, label: c.name }; }), step.categoryId || '', function (v) { step.categoryId = v ? Number(v) : null; }, 'Aus dem Formular'), null, false));
      grid.appendChild(field('Abteilung', select(boot.departments.map(function (d) { return { value: d.id, label: d.name }; }), step.departmentId || '', function (v) { step.departmentId = v ? Number(v) : null; }, 'Aus Kategorie oder Formular'), null, false));
      grid.appendChild(field('Team', select(boot.teams.map(function (t) { return { value: t.id, label: t.name }; }), step.teamId || '', function (v) { step.teamId = v ? Number(v) : null; }, 'Aus Kategorie oder Formular'), null, false));
    }
    box.appendChild(grid);

    if (step.kind === 'page' || step.kind === 'ai') {
      box.appendChild(routesEditor(step));
    } else {
      box.appendChild(el('p', { 'class': 'mat-hint', text: step.kind === 'ticket' ? 'Ein Ticket-Schritt ist das Ende des Ablaufs: er legt das Ticket an.' : 'Ein Endschritt beendet den Ablauf ohne Ticket.' }));
    }

    var tools = el('div', { 'class': 'mat-btn-row' }, [
      step.id !== def.start ? button('Als Start', 'flag', function () { def.start = step.id; markDirty(); renderAll(); }, 'mat-btn--secondary mat-btn--sm') : el('span', { 'class': 'mat-pill mat-pill--ok', text: 'Start' }),
      inFlow ? button('Inhalt bearbeiten', 'layers', function () { state.step = step.id; state.block = null; setView('content'); }, 'mat-btn--secondary mat-btn--sm') : null,
      el('span', { 'class': 'mat-btn-row__spacer' }),
      button('Schritt löschen', 'trash', function () {
        confirmDialog('Den Schritt „' + (step.title || step.id) + '“ mit seinen Bausteinen löschen? Wege dorthin werden entfernt.', 'Löschen').then(function (ok) {
          if (!ok) { return; }
          def.steps = def.steps.filter(function (s) { return s !== step; });
          def.steps.forEach(function (s) { s.next = s.next.filter(function (r) { return r.to !== step.id; }); });
          if (def.start === step.id) { def.start = def.steps[0] ? def.steps[0].id : ''; }
          state.step = def.start;
          state.flowStep = null;
          state.block = null;
          markDirty();
          renderAll();
        });
      }, 'mat-btn--danger mat-btn--sm'),
    ]);
    box.appendChild(tools);
  }

  // ---- the routes of a step: condition and target

  function fieldChoices() {
    var list = questions().map(function (q) { return { value: q.key, label: (q.label || q.key) + ' (' + q.key + ')' }; });
    list.push({ value: '$ai', label: 'Ergebnis der KI ($ai)' });
    return list;
  }

  function routeText(r) {
    if (!r.when || !r.when.field) { return 'sonst'; }
    var op = boot.operators.find(function (o) { return o.value === r.when.op; });
    var q = questions().find(function (b) { return b.key === r.when.field; });
    var value = r.when.value || '';
    if (q) { var opt = optionsOf(q).find(function (o) { return o.value === value; }); if (opt) { value = opt.label; } }
    if (r.when.field === '$ai') { value = value === 'solved' ? 'geholfen' : value === 'open' ? 'nicht geholfen' : value; }
    var name = r.when.field === '$ai' ? 'KI' : q ? (q.label || q.key) : r.when.field;
    return name + ' ' + (op ? op.label : r.when.op) + (r.when.op === 'empty' || r.when.op === 'notempty' ? '' : ' „' + value + '“');
  }

  function routesEditor(step) {
    var wrap = el('div', { 'class': 'mt-routes' }, [el('h3', { 'class': 'mt-subhead', text: 'Wege zum nächsten Schritt' }), el('p', { 'class': 'mat-hint', text: 'Der erste Weg, dessen Bedingung passt, wird genommen. Ein Weg ohne Bedingung ist der Standard – er gehört ans Ende.' })]);
    var list = el('ol', { 'class': 'mt-routes__list' });
    var targets = def.steps.filter(function (s) { return s !== step; }).map(function (s) { return { value: s.id, label: s.title || s.id }; });
    function draw() {
      list.innerHTML = '';
      step.next.forEach(function (r, i) {
        var row = el('li', { 'class': 'mt-routes__row' });
        var conditional = !!(r.when && r.when.field);
        var cond = el('div', { 'class': 'mt-routes__cond' });
        if (conditional) {
          cond.appendChild(select(fieldChoices(), r.when.field, function (v) { r.when.field = v; r.when.value = ''; draw(); renderFlowSoon(); }));
          cond.appendChild(select(boot.operators, r.when.op || 'eq', function (v) { r.when.op = v; draw(); renderFlowSoon(); }));
          if (r.when.op !== 'empty' && r.when.op !== 'notempty') {
            var q = questions().find(function (b) { return b.key === r.when.field; });
            var opts = r.when.field === '$ai' ? [{ value: 'solved', label: 'geholfen (solved)' }, { value: 'open', label: 'nicht geholfen (open)' }] : q ? optionsOf(q) : [];
            cond.appendChild(opts.length > 0 && r.when.op !== 'contains' && r.when.op !== 'in'
              ? select(opts, r.when.value || '', function (v) { r.when.value = v; renderFlowSoon(); }, 'Wert wählen')
              : textInput(r.when.value, function (v) { r.when.value = v; renderFlowSoon(); }, { placeholder: 'Wert', 'aria-label': 'Wert' }));
          }
        } else {
          cond.appendChild(el('span', { 'class': 'mat-muted', text: 'Sonst (immer)' }));
        }
        row.appendChild(el('span', { 'class': 'mt-routes__if', text: conditional ? 'Wenn' : '' }));
        row.appendChild(cond);
        row.appendChild(el('span', { 'class': 'mt-routes__then', text: '→' }));
        row.appendChild(select(targets, r.to, function (v) { r.to = v; renderFlowSoon(); renderCanvasSoon(); }, 'Schritt wählen'));
        row.appendChild(el('span', { 'class': 'mt-routes__tools' }, [
          i > 0 ? button(null, 'arrow-up', function () { step.next.splice(i, 1); step.next.splice(i - 1, 0, r); markDirty(); draw(); renderFlowSoon(); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Nach oben') : null,
          button(null, 'x', function () { step.next.splice(i, 1); markDirty(); draw(); renderFlowSoon(); }, 'mat-btn--ghost mat-btn--icon mat-btn--sm', 'Weg entfernen'),
        ]));
        list.appendChild(row);
      });
    }
    draw();
    wrap.appendChild(list);
    var firstKey = (questions()[0] || {}).key || '$ai';
    wrap.appendChild(el('div', { 'class': 'mat-btn-row mat-btn-row--plain' }, [
      button('Weg mit Bedingung', 'git-branch', function () {
        var r = { when: { field: step.kind === 'ai' ? '$ai' : firstKey, op: 'eq', value: step.kind === 'ai' ? 'solved' : '' }, to: (targets[0] || {}).value || '' };
        var defaultIndex = step.next.findIndex(function (x) { return !x.when || !x.when.field; });
        if (defaultIndex >= 0) { step.next.splice(defaultIndex, 0, r); } else { step.next.push(r); }
        markDirty(); draw(); renderFlowSoon();
      }, 'mat-btn--secondary mat-btn--sm'),
      step.next.some(function (x) { return !x.when || !x.when.field; }) ? null : button('Standardweg', 'arrow-right', function () { step.next.push({ to: (targets[0] || {}).value || '' }); markDirty(); renderAllSoon(); }, 'mat-btn--secondary mat-btn--sm'),
    ]));
    return wrap;
  }

  var allTimer = null;
  function renderAllSoon() { clearTimeout(allTimer); allTimer = setTimeout(renderAll, 50); }

  // ================================================================================================ the flow editor

  var NODE_W = 220;
  var NODE_H = 96;

  function renderFlow() {
    var nodes = $('[data-flow-nodes]', root);
    var svg = $('[data-flow-edges]', root);
    var flow = $('[data-flow]', root);
    nodes.innerHTML = '';
    var maxX = 0;
    var maxY = 0;
    def.steps.forEach(function (s) {
      var k = kinds[s.kind] || kinds.page;
      var node = el('div', { 'class': 'mt-node mt-node--' + s.kind + (s.id === state.flowStep ? ' is-selected' : '') + (s.id === def.start ? ' is-start' : ''), tabindex: '0', role: 'button', 'aria-label': k.label + ': ' + (s.title || s.id), 'data-node': s.id }, [
        el('div', { 'class': 'mt-node__head' }, [icon(k.icon), el('span', { 'class': 'mt-node__kind', text: k.label }), s.id === def.start ? el('span', { 'class': 'mt-node__start', text: 'Start' }) : null]),
        el('div', { 'class': 'mt-node__title', text: s.title || s.id }),
        el('div', { 'class': 'mt-node__meta', text: s.kind === 'ai' ? 'Antwort der KI' : s.blocks.filter(function (b) { return types[b.type] && types[b.type].question; }).length + ' Fragen' }),
      ]);
      node.style.setProperty('--x', (s.x || 0) + 'px');
      node.style.setProperty('--y', (s.y || 0) + 'px');
      wireNode(node, s);
      nodes.appendChild(node);
      maxX = Math.max(maxX, (s.x || 0) + NODE_W);
      maxY = Math.max(maxY, (s.y || 0) + NODE_H);
    });
    state.flowW = Math.max(maxX + 200, 800);
    state.flowH = Math.max(maxY + 160, 420);
    flow.style.setProperty('--w', state.flowW + 'px');
    flow.style.setProperty('--h', state.flowH + 'px');
    drawEdges(svg);
    flowProps();
  }

  var flowTimer = null;
  function renderFlowSoon() { if (state.view !== 'flow') { return; } clearTimeout(flowTimer); flowTimer = setTimeout(function () { drawEdges($('[data-flow-edges]', root)); }, 120); }

  function drawEdges(svg) {
    var ns = 'http://www.w3.org/2000/svg';
    // The drawing has exactly the size of the canvas of the nodes, so one unit is one pixel.
    var width = state.flowW || 1200;
    var height = state.flowH || 600;
    svg.setAttribute('viewBox', '0 0 ' + width + ' ' + height);
    svg.setAttribute('width', width);
    svg.setAttribute('height', height);
    svg.setAttribute('preserveAspectRatio', 'xMinYMin meet');
    svg.innerHTML = '<defs><marker id="mt-arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="8" markerHeight="8" orient="auto-start-reverse"><path d="M0 0L10 5L0 10z" class="mt-edge__head"></path></marker></defs>';
    def.steps.forEach(function (s) {
      s.next.forEach(function (r, i) {
        var target = stepById(r.to);
        if (!target) { return; }
        var x1 = (s.x || 0) + NODE_W;
        var y1 = (s.y || 0) + 30 + Math.min(i, 3) * 16;
        var x2 = target.x || 0;
        var y2 = (target.y || 0) + NODE_H / 2;
        var back = x2 <= x1;
        var dx = Math.max(60, Math.abs(x2 - x1) / 2);
        var d = back
          ? 'M' + x1 + ' ' + y1 + ' C' + (x1 + 80) + ' ' + (y1 - 120) + ' ' + (x2 - 80) + ' ' + (y2 - 120) + ' ' + x2 + ' ' + y2
          : 'M' + x1 + ' ' + y1 + ' C' + (x1 + dx) + ' ' + y1 + ' ' + (x2 - dx) + ' ' + y2 + ' ' + x2 + ' ' + y2;
        var conditional = r.when && r.when.field;
        var path = doc.createElementNS(ns, 'path');
        path.setAttribute('d', d);
        path.setAttribute('class', 'mt-edge' + (conditional ? ' mt-edge--cond' : '') + (s.id === state.flowStep ? ' is-active' : ''));
        path.setAttribute('marker-end', 'url(#mt-arrow)');
        svg.appendChild(path);
        var label = doc.createElementNS(ns, 'text');
        var mx = (x1 + x2) / 2;
        var my = back ? Math.min(y1, y2) - 80 : (y1 + y2) / 2 - 6;
        label.setAttribute('x', mx);
        label.setAttribute('y', my);
        label.setAttribute('text-anchor', 'middle');
        label.setAttribute('class', 'mt-edge__label');
        var text = routeText(r);
        label.textContent = text.length > 34 ? text.slice(0, 33) + '…' : text;
        svg.appendChild(label);
      });
    });
  }

  function wireNode(node, s) {
    var start = null;
    node.addEventListener('pointerdown', function (e) {
      if (e.button !== 0) { return; }
      start = { x: e.clientX, y: e.clientY, sx: s.x || 0, sy: s.y || 0, moved: false };
      node.setPointerCapture(e.pointerId);
    });
    node.addEventListener('pointermove', function (e) {
      if (!start) { return; }
      var dx = e.clientX - start.x;
      var dy = e.clientY - start.y;
      if (!start.moved && Math.abs(dx) + Math.abs(dy) < 4) { return; }
      start.moved = true;
      node.classList.add('is-dragging');
      s.x = Math.max(0, Math.round((start.sx + dx) / 10) * 10);
      s.y = Math.max(0, Math.round((start.sy + dy) / 10) * 10);
      node.style.setProperty('--x', s.x + 'px');
      node.style.setProperty('--y', s.y + 'px');
      drawEdges($('[data-flow-edges]', root));
    });
    node.addEventListener('pointerup', function () {
      if (!start) { return; }
      var moved = start.moved;
      start = null;
      node.classList.remove('is-dragging');
      if (moved) { markDirty(); renderFlow(); return; }
      state.flowStep = s.id;
      state.step = s.id;
      state.block = null;
      renderFlow();
    });
    node.addEventListener('keydown', function (e) {
      var step = e.shiftKey ? 50 : 10;
      var moves = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] };
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); state.flowStep = s.id; state.step = s.id; renderFlow(); var again = root.querySelector('[data-node="' + s.id + '"]'); if (again) { again.focus(); } return; }
      if (moves[e.key] && e.altKey) {
        e.preventDefault();
        s.x = Math.max(0, (s.x || 0) + moves[e.key][0]);
        s.y = Math.max(0, (s.y || 0) + moves[e.key][1]);
        markDirty();
        renderFlow();
        var moved = root.querySelector('[data-node="' + s.id + '"]');
        if (moved) { moved.focus(); }
      }
    });
  }

  function flowProps() {
    var box = $('[data-flow-props]', root);
    var title = $('[data-flow-props-title]', root);
    box.innerHTML = '';
    var step = stepById(state.flowStep);
    if (!step) {
      title.textContent = 'Ablauf';
      box.appendChild(el('p', { 'class': 'mat-hint', text: 'Klicke einen Schritt an, um seine Wege zu bearbeiten. Schritte verschiebst du mit der Maus (oder Alt + Pfeiltasten).' }));
      box.appendChild(el('div', { 'class': 'mat-btn-row mat-btn-row--plain' }, boot.stepKinds.map(function (k) { return button(k.label, k.icon, function () { addStep(k.kind); }, 'mat-btn--secondary mat-btn--sm'); })));
      box.appendChild(el('div', { 'class': 'mat-btn-row mat-btn-row--plain' }, [button('Automatisch anordnen', 'grid', arrange, 'mat-btn--ghost mat-btn--sm')]));
      return;
    }
    title.textContent = 'Schritt: ' + (step.title || step.id);
    stepProps(box, step, true);
    box.appendChild(el('div', { 'class': 'mat-btn-row mat-btn-row--plain' }, [button('Auswahl aufheben', 'x', function () { state.flowStep = null; renderFlow(); }, 'mat-btn--ghost mat-btn--sm'), button('Automatisch anordnen', 'grid', arrange, 'mat-btn--ghost mat-btn--sm')]));
  }

  /** Layers from the start: each step one column right of the step that leads to it first; unreachable steps in a last column. */
  function arrange() {
    var level = {};
    var queue = [];
    if (stepById(def.start)) { level[def.start] = 0; queue.push(def.start); }
    while (queue.length) {
      var id = queue.shift();
      (stepById(id).next || []).forEach(function (r) { if (stepById(r.to) && level[r.to] === undefined) { level[r.to] = level[id] + 1; queue.push(r.to); } });
    }
    var maxLevel = Object.keys(level).reduce(function (m, k) { return Math.max(m, level[k]); }, 0);
    var rows = {};
    def.steps.forEach(function (s) {
      var l = level[s.id] === undefined ? maxLevel + 1 : level[s.id];
      rows[l] = (rows[l] || 0) + 1;
      s.x = 40 + l * 300;
      s.y = 40 + (rows[l] - 1) * 150;
    });
    markDirty();
    renderFlow();
  }

  // ================================================================================================ views, saving, the check

  function setView(view) {
    state.view = view;
    root.querySelectorAll('[data-view]').forEach(function (b) { b.setAttribute('aria-pressed', b.getAttribute('data-view') === view ? 'true' : 'false'); });
    $('[data-designer-content]', root).hidden = view !== 'content';
    $('[data-designer-flow]', root).hidden = view !== 'flow';
    if (view === 'flow') { if (!state.flowStep) { state.flowStep = state.step; } renderFlow(); } else { renderAll(); }
  }

  root.querySelectorAll('[data-view]').forEach(function (b) { b.addEventListener('click', function () { setView(b.getAttribute('data-view')); }); });

  function renderCheck(check) {
    if (!checkBox || !check) { return; }
    checkBox.innerHTML = '';
    var errors = check.errors || [];
    var warnings = check.warnings || [];
    var pill = el('button', { type: 'button', 'class': 'mat-pill ' + (errors.length ? 'mat-pill--error' : warnings.length ? 'mat-pill--warn' : 'mat-pill--ok') }, [
      icon(errors.length ? 'x-circle' : warnings.length ? 'alert' : 'check'),
      doc.createTextNode(errors.length ? errors.length + ' Fehler' : warnings.length ? warnings.length + ' Hinweise' : 'Bereit zum Veröffentlichen'),
    ]);
    pill.addEventListener('click', function () {
      var text = errors.map(function (e) { return '✕ ' + e; }).concat(warnings.map(function (w) { return '! ' + w; })).join('\n') || 'Alles in Ordnung.';
      if (window.MatUi && window.MatUi.alert) { window.MatUi.alert(text, { title: 'Prüfung des Entwurfs' }); }
    });
    checkBox.appendChild(pill);
  }

  function save() {
    var body = new FormData();
    var clean = JSON.parse(JSON.stringify(def));
    clean.steps.forEach(function (s) { s.blocks.forEach(function (b) { delete b.keyEdited; (b.options || []).forEach(function (o) { delete o.valueEdited; }); }); });
    body.append('definition', JSON.stringify(clean));
    if (status) { status.textContent = 'Speichere …'; }
    return fetch(root.getAttribute('data-save-url'), { method: 'POST', credentials: 'same-origin', headers: { 'RequestVerificationToken': window.MtHttp ? window.MtHttp.csrf() : '' }, body: body })
      .then(function (r) { return r.json().then(function (data) { return { ok: r.ok, data: data }; }); })
      .then(function (res) {
        if (!res.ok) { throw new Error(res.data && res.data.error ? res.data.error : 'Speichern fehlgeschlagen.'); }
        def = res.data.definition;
        def.steps = def.steps || [];
        state.dirty = false;
        if (!stepById(state.step)) { state.step = def.start; }
        if (state.flowStep && !stepById(state.flowStep)) { state.flowStep = null; }
        if (status) { status.textContent = 'Gespeichert um ' + new Date().toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' }) + '.'; }
        renderCheck(res.data.check);
        if (state.view === 'flow') { renderFlow(); } else { renderAll(); }
        toast('Der Entwurf ist gespeichert.', 'success');
      })
      .catch(function (err) {
        if (status) { status.textContent = 'Nicht gespeichert.'; }
        toast(err.message || 'Speichern fehlgeschlagen.', 'danger');
      });
  }

  $('[data-designer-save]', root).addEventListener('click', save);
  doc.addEventListener('keydown', function (e) {
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') { e.preventDefault(); save(); }
  });

  // The preview shows the saved draft: save first when something is open.
  var preview = $('[data-designer-preview]', root);
  if (preview) {
    preview.addEventListener('click', function (e) {
      if (!state.dirty) { return; }
      e.preventDefault();
      var win = window.open('about:blank', '_blank');
      save().then(function () { if (win) { win.location = preview.href; } });
    });
  }

  window.addEventListener('beforeunload', function (e) {
    if (state.dirty) { e.preventDefault(); e.returnValue = ''; }
  });

  function renderAll() {
    renderSteps();
    renderCanvas();
    renderProps();
    if (state.view === 'flow') { renderFlow(); }
  }

  renderPalette();
  renderCatalog();
  renderCheck(boot.check);
  renderAll();
})();
