// Developer tools for the dashboard. They run only in the browser; the spec check fetches `oml spec schema` once.
// Each tool is { id, name, desc, render(el) } and builds its own small form with window.omlUi.h.
'use strict';
(() => {
  const ui = () => window.omlUi;
  const h = (...a) => ui().h(...a);
  let uid = 0;
  const id = name => `t-${name}-${++uid}`;

  function area(label, opts) {
    const i = id('area');
    const el = h('textarea', { id: i, class: 'code', rows: (opts && opts.rows) || 8, spellcheck: 'false', placeholder: (opts && opts.placeholder) || '' });
    if (opts && opts.value) el.value = opts.value;
    return { el, field: h('div', { class: 'field' }, h('label', { for: i }, label), el) };
  }
  function text(label, opts) {
    const i = id('text');
    const el = h('input', { type: (opts && opts.type) || 'text', id: i, class: 'code', spellcheck: 'false', placeholder: (opts && opts.placeholder) || '', value: (opts && opts.value) || '' });
    return { el, field: h('div', { class: 'field' }, h('label', { for: i }, label), el) };
  }
  const button = (label, onclick, primary) => h('button', { class: 'button' + (primary ? ' primary' : ''), type: 'button', onclick }, label);
  const outBox = () => h('pre', { class: 'out', 'aria-live': 'polite' });
  const status = (kind, message) => h('div', { class: 'message ' + (kind === 'error' ? 'error' : ''), role: kind === 'error' ? 'alert' : 'status' },
    kind === 'error' ? h('strong', {}, 'Problem: ') : kind === 'ok' ? h('span', { class: 'badge ok' }, 'OK') : null, ' ', message);
  const copyOut = (out, what) => ui().copyButton(() => out.textContent, 'Copy result', what);

  // ---- JSON ----
  // Where JSON.parse failed. Newer browsers no longer say "at position N", so a small scanner finds the spot itself.
  function badJsonAt(src) {
    let i = 0;
    const ws = () => { while (/\s/.test(src[i] || '')) i++; };
    const fail = what => { throw { at: i, what }; };
    const value = () => {
      ws();
      const c = src[i];
      if (c === '{') {
        i++; ws();
        if (src[i] === '}') { i++; return; }
        for (;;) {
          ws(); if (src[i] !== '"') fail('Expected a "quoted" property name'); string(); ws();
          if (src[i] !== ':') fail('Expected ":" after the property name'); i++; value(); ws();
          if (src[i] === ',') { i++; ws(); if (src[i] === '}') fail('A comma before } is not allowed in JSON'); continue; }
          if (src[i] === '}') { i++; return; }
          fail('Expected "," or "}"');
        }
      }
      if (c === '[') {
        i++; ws();
        if (src[i] === ']') { i++; return; }
        for (;;) {
          value(); ws();
          if (src[i] === ',') { i++; ws(); if (src[i] === ']') fail('A comma before ] is not allowed in JSON'); continue; }
          if (src[i] === ']') { i++; return; }
          fail('Expected "," or "]"');
        }
      }
      if (c === '"') return string();
      const m = /^(-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?|true|false|null)/.exec(src.slice(i));
      if (m) { i += m[0].length; return; }
      fail(c === undefined ? 'The text ends too early' : c === "'" ? 'Use "double quotes", not single quotes' : `Unexpected character ${c}`);
    };
    const string = () => {
      i++;
      while (i < src.length && src[i] !== '"') { if (src[i] === '\n') fail('A string cannot contain a line break'); i += src[i] === '\\' ? 2 : 1; }
      if (src[i] !== '"') fail('A string is not closed');
      i++;
    };
    try { value(); ws(); if (i < src.length) fail('Unexpected text after the end'); return null; }
    catch (e) { return e && typeof e.at === 'number' ? e : null; }
  }
  function jsonError(src, e) {
    const msg = String(e.message || e);
    let line, col;
    const lc = /line (\d+) column (\d+)/i.exec(msg);
    const pos = /position (\d+)/i.exec(msg);
    const found = badJsonAt(src);
    const at = found ? found.at : pos ? +pos[1] : null;
    if (at !== null) { const before = src.slice(0, at).split('\n'); line = before.length; col = before[before.length - 1].length + 1; }
    else if (lc) { line = +lc[1]; col = +lc[2]; }
    if (!line) return msg;
    if (found) return `Line ${line}, column ${col}: ${found.what}.\n\n${src.split('\n')[line - 1] || ''}\n${' '.repeat(Math.max(0, col - 1))}^`;
    const text = src.split('\n')[line - 1] || '';
    return `Line ${line}, column ${col}: ${msg.replace(/\s*\(line \d+ column \d+\)/i, '').replace(/ in JSON at position \d+/i, '')}\n\n${text}\n${' '.repeat(Math.max(0, col - 1))}^`;
  }
  const json = {
    id: 'json', name: 'JSON', desc: 'Format, minify and check JSON. Errors show the line and column.',
    render(el) {
      const input = area('JSON', { rows: 12, placeholder: '{ "name": "Order", "total": 42.5 }' });
      const indent = h('select', { 'aria-label': 'Indent' }, [['2', '2 spaces'], ['4', '4 spaces'], ['tab', 'Tab']].map(([v, t]) => h('option', { value: v }, t)));
      const out = outBox(), msg = h('div');
      const run = mode => {
        msg.replaceChildren(); out.textContent = '';
        let value;
        try { value = JSON.parse(input.el.value); }
        catch (e) { ui().add(msg, h('div', { class: 'message error', role: 'alert' }, h('strong', {}, 'Not valid JSON. '), h('pre', { text: jsonError(input.el.value, e) }))); return; }
        if (mode === 'check') { ui().add(msg, status('ok', 'Valid JSON.')); return; }
        out.textContent = mode === 'min' ? JSON.stringify(value) : JSON.stringify(value, null, indent.value === 'tab' ? '\t' : +indent.value);
        ui().add(msg, status('ok', `Valid JSON. ${input.el.value.length} → ${out.textContent.length} characters.`));
      };
      ui().add(el, input.field, h('div', { class: 'row' }, button('Format', () => run('format'), true), button('Minify', () => run('min')), button('Check', () => run('check')), indent, copyOut(out, 'JSON')), msg, h('div', { class: 'field' }), out);
    },
  };

  // ---- oml spec ----
  let schemaPromise = null;
  function schema() {
    if (!schemaPromise) {
      let cached = null;
      try { cached = JSON.parse(sessionStorage.getItem('oml-spec-schema') || 'null'); } catch { cached = null; }
      schemaPromise = cached ? Promise.resolve(cached) : ui().api('spec/schema').then(r => {
        ui().need(r);
        try { sessionStorage.setItem('oml-spec-schema', JSON.stringify(r.output)); } catch { /* not cached */ }
        return r.output;
      });
      schemaPromise.catch(() => { schemaPromise = null; });
    }
    return schemaPromise;
  }
  // JSONC: drop comments outside strings. Trailing commas stay, so JSON.parse refuses them as `oml apply` does.
  function stripComments(src) {
    let out = '', i = 0;
    while (i < src.length) {
      const c = src[i];
      if (c === '"') { let j = i + 1; while (j < src.length && src[j] !== '"') j += src[j] === '\\' ? 2 : 1; out += src.slice(i, j + 1); i = j + 1; }
      else if (c === '/' && src[i + 1] === '/') { while (i < src.length && src[i] !== '\n') { out += ' '; i++; } }
      else if (c === '/' && src[i + 1] === '*') { const end = src.indexOf('*/', i + 2); const stop = end < 0 ? src.length : end + 2; out += src.slice(i, stop).replace(/[^\n]/g, ' '); i = stop; }
      else { out += c; i++; }
    }
    return out;
  }
  function distance(a, b) {
    const d = Array.from({ length: a.length + 1 }, (_, i) => [i, ...Array(b.length).fill(0)]);
    for (let j = 1; j <= b.length; j++) d[0][j] = j;
    for (let i = 1; i <= a.length; i++) for (let j = 1; j <= b.length; j++)
      d[i][j] = Math.min(d[i - 1][j] + 1, d[i][j - 1] + 1, d[i - 1][j - 1] + (a[i - 1] === b[j - 1] ? 0 : 1));
    return d[a.length][b.length];
  }
  const typeOf = v => v === null ? 'null' : Array.isArray(v) ? 'array' : Number.isInteger(v) ? 'integer' : typeof v;
  // The JSON Schema subset that `oml spec schema` uses: $ref, oneOf, const, enum, type, properties, required, additionalProperties, items.
  function validate(value, s, path, errors, rootSchema) {
    if (s.$ref) s = s.$ref.replace(/^#\//, '').split('/').reduce((n, k) => n && n[k], rootSchema) || {};
    if (s.oneOf) {
      if (!s.oneOf.some(option => { const e = []; validate(value, option, path, e, rootSchema); return e.length === 0; }))
        errors.push([path, 'does not have an allowed shape here']);
      return;
    }
    if (s.const !== undefined && value !== s.const) { errors.push([path, `must be ${JSON.stringify(s.const)}`]); return; }
    if (s.enum && !s.enum.includes(value)) { errors.push([path, `must be one of ${s.enum.map(x => JSON.stringify(x)).join(', ')}`]); return; }
    if (s.type) {
      const types = [].concat(s.type), actual = typeOf(value);
      if (!types.includes(actual) && !(actual === 'integer' && types.includes('number'))) { errors.push([path, `must be ${types.join(' or ')}, not ${actual}`]); return; }
    }
    if (typeOf(value) === 'object') {
      const props = s.properties || {};
      for (const key of s.required || []) if (!(key in value)) errors.push([path, `needs "${key}"`]);
      for (const [key, v] of Object.entries(value)) {
        const p = path ? `${path}.${key}` : key;
        if (props[key]) validate(v, props[key], p, errors, rootSchema);
        else if (key.startsWith('_') || key.startsWith('$')) continue; // free for notes, as in `oml apply`
        else if (s.additionalProperties === false) {
          const near = Object.keys(props).map(k => [k, distance(k.toLowerCase(), key.toLowerCase())]).sort((a, b) => a[1] - b[1])[0];
          errors.push([p, `unknown key "${key}"` + (near && near[1] <= 2 ? `; did you mean "${near[0]}"?` : '')]);
        } else if (typeof s.additionalProperties === 'object') validate(v, s.additionalProperties, p, errors, rootSchema);
      }
    }
    if (typeOf(value) === 'array' && s.items) value.forEach((v, i) => validate(v, s.items, `${path}[${i}]`, errors, rootSchema));
  }
  const spec = {
    id: 'spec', name: 'oml spec check', desc: 'Check the shape of an oml spec against the schema of this oml version. Comments are allowed.',
    render(el) {
      const input = area('Spec (JSON with comments)', { rows: 14, placeholder: '{ "entities": [ { "name": "ReviewNote", "attributes": [ { "name": "Text", "length": 250 } ] } ] }' });
      const msg = h('div'), keys = h('div');
      const check = async () => {
        msg.replaceChildren();
        let s;
        try { s = await schema(); } catch (e) { msg.replaceChildren(ui().errorBox(e)); return; }
        let value;
        const src = stripComments(input.el.value);
        try { value = JSON.parse(src); }
        catch (e) { msg.replaceChildren(h('div', { class: 'message error', role: 'alert' }, h('strong', {}, 'Not valid JSON. '), h('pre', { text: jsonError(src, e) }))); return; }
        const errors = [];
        validate(value, s, '', errors, s);
        msg.replaceChildren(errors.length === 0
          ? status('ok', 'The shape is right. oml spec check also checks flow targets, node kinds and edit operations.')
          : h('div', { class: 'message error', role: 'alert' }, h('strong', {}, `${errors.length} problem${errors.length > 1 ? 's' : ''}:`),
            h('ul', {}, errors.slice(0, 50).map(([p, m]) => h('li', {}, h('code', { text: p || '(top level)' }), ' ', m)))),
          ui().cli('oml spec check C:\\Work\\change.json'));
      };
      const showKeys = async () => {
        let s;
        try { s = await schema(); } catch (e) { keys.replaceChildren(ui().errorBox(e)); return; }
        keys.replaceChildren(h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', {}, 'Top-level key'), h('th', {}, 'What it adds'))),
          h('tbody', {}, Object.entries(s.properties || {}).filter(([k]) => !k.startsWith('$')).map(([k, v]) => h('tr', {}, h('td', {}, h('code', { text: k })), h('td', { class: 'small' }, v.description || '')))))),
          ui().cli('oml spec entities'));
      };
      ui().add(el, input.field, h('div', { class: 'row' }, button('Check spec', check, true), button('Show the spec keys', showKeys)), msg, keys);
    },
  };

  // ---- Base64 ----
  const b64 = {
    id: 'base64', name: 'Base64', desc: 'Encode text to Base64 or decode it back. UTF-8 text.',
    render(el) {
      const input = area('Text or Base64', { rows: 6 });
      const urlSafe = h('input', { type: 'checkbox', id: id('b64url') });
      const out = outBox(), msg = h('div');
      const encode = () => {
        msg.replaceChildren();
        const bin = Array.from(new TextEncoder().encode(input.el.value), b => String.fromCharCode(b)).join('');
        let r = btoa(bin);
        if (urlSafe.checked) r = r.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
        out.textContent = r;
      };
      const decode = () => {
        msg.replaceChildren(); out.textContent = '';
        try {
          let s = input.el.value.trim().replace(/\s+/g, '').replace(/-/g, '+').replace(/_/g, '/');
          while (s.length % 4) s += '=';
          const bin = atob(s);
          out.textContent = new TextDecoder('utf-8', { fatal: true }).decode(Uint8Array.from(bin, c => c.charCodeAt(0)));
        } catch { ui().add(msg, status('error', 'This is not Base64 of UTF-8 text.')); }
      };
      ui().add(el, input.field, h('label', { class: 'check', for: urlSafe.id }, urlSafe, 'URL-safe (- and _ , no padding)'),
        h('div', { class: 'row' }, button('Encode', encode, true), button('Decode', decode), copyOut(out, 'Result')), msg, h('div', { class: 'field' }), out);
    },
  };

  // ---- URL ----
  const url = {
    id: 'url', name: 'URL encode', desc: 'Encode text for use in a URL, or decode %20-style text.',
    render(el) {
      const input = area('Text', { rows: 5, placeholder: 'Orders & Invoices/2026' });
      const out = outBox(), msg = h('div');
      const go = fn => { msg.replaceChildren(); try { out.textContent = fn(input.el.value); } catch { out.textContent = ''; ui().add(msg, status('error', 'This text has a broken % sequence.')); } };
      ui().add(el, input.field, h('div', { class: 'row' },
        button('Encode', () => go(encodeURIComponent), true), button('Decode', () => go(s => decodeURIComponent(s.replace(/\+/g, ' ')))), copyOut(out, 'Result')), msg, h('div', { class: 'field' }), out);
    },
  };

  // ---- JWT ----
  const jwt = {
    id: 'jwt', name: 'JWT decoder', desc: 'Read the header and claims of a JSON Web Token. It does not check the signature.',
    render(el) {
      const input = area('Token', { rows: 5, placeholder: 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.c2lnbmF0dXJl' });
      const out = h('div');
      const part = s => {
        let b = s.replace(/-/g, '+').replace(/_/g, '/');
        while (b.length % 4) b += '=';
        return JSON.parse(new TextDecoder().decode(Uint8Array.from(atob(b), c => c.charCodeAt(0))));
      };
      const decode = () => {
        out.replaceChildren();
        const parts = input.el.value.trim().split('.');
        if (parts.length < 2) { ui().add(out, status('error', 'A JWT has three parts separated by dots.')); return; }
        let header, payload;
        try { header = part(parts[0]); payload = part(parts[1]); } catch { ui().add(out, status('error', 'The header or payload is not Base64URL JSON.')); return; }
        const times = ['exp', 'iat', 'nbf'].filter(k => typeof payload[k] === 'number').map(k => {
          const d = new Date(payload[k] * 1000);
          const note = k === 'exp' ? (d < new Date() ? ' (expired)' : ' (still valid)') : '';
          return h('li', {}, h('code', { text: k }), ` ${d.toLocaleString()} · ${d.toISOString()}${note}`);
        });
        ui().add(out, h('h3', {}, 'Header'), h('pre', { class: 'out', text: JSON.stringify(header, null, 2) }),
          h('h3', {}, 'Payload'), h('pre', { class: 'out', text: JSON.stringify(payload, null, 2) }),
          times.length ? h('ul', { class: 'small' }, times) : null,
          h('p', { class: 'hint' }, 'The signature is not checked. Do not trust these claims without checking it on the server.'));
      };
      ui().add(el, h('div', { class: 'warning-banner', role: 'note' }, h('strong', {}, 'Never paste production tokens. '), 'Use test tokens only. This page does not send the token anywhere, but your clipboard and screen can be seen by others.'),
        input.field, button('Decode', decode, true), h('div', { class: 'field' }), out);
    },
  };

  // ---- GUID ----
  const guid = {
    id: 'guid', name: 'GUID generator', desc: 'Make random GUIDs (UUID version 4).',
    render(el) {
      const count = text('How many', { type: 'number', value: '5' });
      count.el.min = '1'; count.el.max = '100';
      const upper = h('input', { type: 'checkbox', id: id('upper') });
      const braces = h('input', { type: 'checkbox', id: id('braces') });
      const plain = h('input', { type: 'checkbox', id: id('plain') });
      const out = outBox();
      const make = () => {
        const n = Math.min(100, Math.max(1, parseInt(count.el.value, 10) || 1));
        out.textContent = Array.from({ length: n }, () => {
          let g = crypto.randomUUID();
          if (plain.checked) g = g.replace(/-/g, '');
          if (upper.checked) g = g.toUpperCase();
          return braces.checked ? `{${g}}` : g;
        }).join('\n');
      };
      ui().add(el, h('div', { class: 'grid-2' }, count.field, h('div', {},
        h('label', { class: 'check', for: upper.id }, upper, 'Upper case'), h('label', { class: 'check', for: braces.id }, braces, 'With { }'), h('label', { class: 'check', for: plain.id }, plain, 'Without dashes'))),
        h('div', { class: 'row' }, button('Generate', make, true), copyOut(out, 'GUIDs')), h('div', { class: 'field' }), out);
      make();
    },
  };

  // ---- Unix time ----
  const time = {
    id: 'time', name: 'Unix timestamp', desc: 'Change a Unix timestamp to a date, or a date to a timestamp.',
    render(el) {
      const now = h('p', { class: 'mono small' });
      const tick = () => { if (!now.isConnected) { clearInterval(timer); return; } const ms = Date.now(); now.textContent = `Now: ${Math.floor(ms / 1000)} s · ${ms} ms · ${new Date(ms).toISOString()}`; };
      const timer = setInterval(tick, 1000);
      const stamp = text('Timestamp (seconds or milliseconds)', { placeholder: '1767225600' });
      const date = text('Date and time (your time zone)', { type: 'datetime-local' });
      const out = outBox();
      const fromStamp = () => {
        const n = Number(stamp.el.value.trim());
        if (!stamp.el.value.trim() || !Number.isFinite(n)) { out.textContent = 'Type a number.'; return; }
        const ms = Math.abs(n) > 1e11 ? n : n * 1000;
        const d = new Date(ms);
        if (isNaN(d)) { out.textContent = 'This number is out of range.'; return; }
        out.textContent = `Read as ${Math.abs(n) > 1e11 ? 'milliseconds' : 'seconds'}\nUTC:   ${d.toISOString()}\nLocal: ${d.toLocaleString()}`;
      };
      const fromDate = () => {
        const d = new Date(date.el.value);
        if (!date.el.value || isNaN(d)) { out.textContent = 'Choose a date and time.'; return; }
        out.textContent = `Seconds:      ${Math.floor(d.getTime() / 1000)}\nMilliseconds: ${d.getTime()}\nUTC:          ${d.toISOString()}`;
      };
      ui().add(el, now, h('div', { class: 'grid-2' }, h('div', {}, stamp.field, button('To date', fromStamp, true)), h('div', {}, date.field, button('To timestamp', fromDate))),
        h('div', { class: 'field' }), out);
      tick();
    },
  };

  // ---- Diff ----
  function diffLines(a, b) {
    const n = a.length, m = b.length;
    const lcs = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
    for (let i = n - 1; i >= 0; i--) for (let j = m - 1; j >= 0; j--) lcs[i][j] = a[i] === b[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    const out = [];
    let i = 0, j = 0;
    while (i < n && j < m) {
      if (a[i] === b[j]) { out.push([' ', a[i]]); i++; j++; }
      else if (lcs[i + 1][j] >= lcs[i][j + 1]) out.push(['-', a[i++]]);
      else out.push(['+', b[j++]]);
    }
    while (i < n) out.push(['-', a[i++]]);
    while (j < m) out.push(['+', b[j++]]);
    return out;
  }
  const diff = {
    id: 'diff', name: 'Text diff', desc: 'Compare two texts line by line.',
    render(el) {
      const left = area('Before', { rows: 10 }), right = area('After', { rows: 10 });
      const trim = h('input', { type: 'checkbox', id: id('trim') });
      const out = h('div', { class: 'out diff', 'aria-live': 'polite' });
      const compare = () => {
        const norm = s => s.replace(/\r/g, '').split('\n').map(l => trim.checked ? l.trim().replace(/\s+/g, ' ') : l);
        const a = norm(left.el.value), b = norm(right.el.value);
        if (a.length > 3000 || b.length > 3000) { out.replaceChildren('Each text can have at most 3000 lines here.'); return; }
        const rows = diffLines(a, b);
        const added = rows.filter(r => r[0] === '+').length, removed = rows.filter(r => r[0] === '-').length;
        out.replaceChildren(h('div', { class: 'muted' }, added + removed === 0 ? 'The texts are the same.' : `${added} line(s) added, ${removed} removed.`),
          ...rows.map(([k, line]) => h('div', { class: k === '+' ? 'add' : k === '-' ? 'del' : '' }, `${k} ${line}`)));
      };
      ui().add(el, h('div', { class: 'grid-2' }, left.field, right.field), h('label', { class: 'check', for: trim.id }, trim, 'Ignore spaces at the start and end of lines'),
        button('Compare', compare, true), h('div', { class: 'field' }), out);
    },
  };

  // ---- Regex ----
  const regex = {
    id: 'regex', name: 'Regex tester', desc: 'Try a JavaScript regular expression on a text and see every match and group.',
    render(el) {
      const pattern = text('Pattern', { placeholder: '(\\w+)@example\\.com' });
      const flags = text('Flags', { value: 'gi' });
      const input = area('Text', { rows: 8, placeholder: 'ana@example.com, rui@example.com' });
      const out = h('div', { class: 'out', 'aria-live': 'polite' }), list = h('div');
      const run = () => {
        list.replaceChildren();
        let re;
        try { re = new RegExp(pattern.el.value, flags.el.value.includes('g') ? flags.el.value : flags.el.value + 'g'); }
        catch (e) { out.replaceChildren(h('span', { class: 'saved-bad' }, e.message)); return; }
        if (!pattern.el.value) { out.replaceChildren(input.el.value); return; }
        const s = input.el.value, parts = [], rows = [];
        let last = 0, count = 0;
        for (const m of s.matchAll(re)) {
          if (++count > 1000) break;
          parts.push(s.slice(last, m.index), h('mark', {}, m[0]));
          last = m.index + m[0].length;
          rows.push(h('tr', {}, h('td', { class: 'num' }, count), h('td', { class: 'mono' }, m[0]), h('td', { class: 'mono small' }, m.slice(1).map((g, i) => `$${i + 1}=${g === undefined ? '(none)' : g}`).join('  '))));
        }
        parts.push(s.slice(last));
        out.replaceChildren(...parts);
        ui().add(list, rows.length ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', { class: 'num' }, '#'), h('th', {}, 'Match'), h('th', {}, 'Groups'))), h('tbody', {}, rows)))
          : h('p', { class: 'muted' }, 'No match.'));
      };
      [pattern.el, flags.el, input.el].forEach(x => x.addEventListener('input', run));
      ui().add(el, h('div', { class: 'grid-2' }, pattern.field, flags.field), input.field, out, h('div', { class: 'field' }), list);
    },
  };

  // ---- Contrast ----
  function rgb(value) {
    const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(value.trim());
    if (!m) return null;
    const hex = m[1].length === 3 ? m[1].split('').map(c => c + c).join('') : m[1];
    return [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16));
  }
  const luminance = c => { const [r, g, b] = c.map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };
  const contrast = {
    id: 'contrast', name: 'Color contrast', desc: 'Check text and background colours against WCAG AA and AAA.',
    render(el) {
      const pair = (label, value) => {
        const t = text(label, { value });
        const pick = h('input', { type: 'color', value, 'aria-label': label + ' picker' });
        pick.addEventListener('input', () => { t.el.value = pick.value; check(); });
        t.el.addEventListener('input', () => { const c = rgb(t.el.value); if (c) pick.value = '#' + c.map(v => v.toString(16).padStart(2, '0')).join(''); check(); });
        return { t, el: h('div', { class: 'row' }, h('div', { class: 'grow' }, t.field), pick) };
      };
      const fg = pair('Text colour', '#171717'), bg = pair('Background colour', '#ffffff');
      const out = h('div', { 'aria-live': 'polite' });
      const check = () => {
        const a = rgb(fg.t.el.value), b = rgb(bg.t.el.value);
        if (!a || !b) { out.replaceChildren(status('error', 'Use hex colours such as #1a1a1a or #fff.')); return; }
        const [l1, l2] = [luminance(a), luminance(b)].sort((x, y) => y - x);
        const ratio = (l1 + 0.05) / (l2 + 0.05);
        const badge = (ok, label) => h('span', { class: 'badge ' + (ok ? 'ok' : 'fail') }, (ok ? 'Pass ' : 'Fail ') + label);
        const sample = h('div', { class: 'swatch' }, h('div', { class: 'big' }, 'Large text 24 px'), h('div', {}, 'Normal text: the quick brown fox jumps over the lazy dog.'));
        sample.style.color = '#' + a.map(v => v.toString(16).padStart(2, '0')).join('');
        sample.style.background = '#' + b.map(v => v.toString(16).padStart(2, '0')).join('');
        out.replaceChildren(h('p', { class: 'ratio' }, ratio.toFixed(2) + ' : 1'),
          h('div', { class: 'row' }, badge(ratio >= 4.5, 'AA normal (4.5)'), badge(ratio >= 3, 'AA large (3)'), badge(ratio >= 7, 'AAA normal (7)'), badge(ratio >= 4.5, 'AAA large (4.5)')),
          h('div', { class: 'field' }), sample);
      };
      ui().add(el, h('div', { class: 'grid-2' }, fg.el, bg.el), out);
      check();
    },
  };

  // ---- CSS ----
  // Splits CSS into strings, comments and code, so ; { } inside strings and url(...) are left alone.
  function cssTokens(src) {
    const tokens = [];
    let i = 0, code = '';
    const flush = () => { if (code) tokens.push(['code', code]); code = ''; };
    while (i < src.length) {
      const c = src[i];
      if (c === '"' || c === "'") {
        flush();
        let j = i + 1;
        while (j < src.length && src[j] !== c && src[j] !== '\n') j += src[j] === '\\' ? 2 : 1;
        tokens.push(['str', src.slice(i, j + 1)]); i = j + 1;
      } else if (c === '/' && src[i + 1] === '*') {
        flush();
        const end = src.indexOf('*/', i + 2), stop = end < 0 ? src.length : end + 2;
        tokens.push(['com', src.slice(i, stop)]); i = stop;
      } else { code += c; i++; }
    }
    flush();
    return tokens;
  }
  function minifyCss(src) {
    let out = '', depth = 0;
    for (const [kind, v] of cssTokens(src)) {
      if (kind === 'com') { if (v.startsWith('/*!')) out += v; continue; }
      if (kind === 'str') { out += v; continue; }
      let s = '';
      for (const ch of v.replace(/\s+/g, ' ')) {
        if (ch === '(') depth++;
        if (ch === ')') depth = Math.max(0, depth - 1);
        if (depth === 0 && '{};,'.includes(ch)) s = s.replace(/ $/, '') + ch + '\u0000';
        else if (depth === 0 && ch === ':') s += ch + '\u0001';
        else s += ch;
      }
      out += s;
    }
    // \0: no space is needed after { } ; and ,. \1: none after ':' (but keep the one before: "a :hover" is not "a:hover").
    return out.replace(/\u0000\s*/g, '').replace(/\u0001 ?/g, '').replace(/;}/g, '}').trim();
  }
  // Strings and comments are masked first, so their ; { } : stay as they are.
  function beautifyCss(src) {
    const saved = [];
    const masked = cssTokens(minifyCss(src)).map(([k, v]) => k === 'code' ? v : (saved.push(v), `\u0002${saved.length - 1}\u0002`)).join('');
    let out = '', indent = 0, depth = 0, seg = '';
    const pad = () => '  '.repeat(indent);
    for (const ch of masked) {
      if (ch === '(') depth++;
      else if (ch === ')') depth = Math.max(0, depth - 1);
      if (depth > 0 || !'{};'.includes(ch)) { seg += ch; continue; }
      const text = seg.trim(); seg = '';
      if (ch === '{') { out += pad() + text.replace(/,(?![^(]*\))/g, ', ') + ' {\n'; indent++; continue; }
      if (text) out += pad() + text.replace(/:/, ': ') + ';\n';
      if (ch === '}') { indent = Math.max(0, indent - 1); out += pad() + '}\n' + (indent === 0 ? '\n' : ''); }
    }
    if (seg.trim()) out += seg.trim() + '\n';
    return out.replace(/\u0002(\d+)\u0002/g, (_, n) => saved[n]).trim() + '\n';
  }
  const css = {
    id: 'css', name: 'CSS minify', desc: 'Make CSS smaller, or format it so it is easy to read. Comments that start with /*! stay.',
    render(el) {
      const input = area('CSS', { rows: 12, placeholder: '.order-card { padding: 16px; color: var(--color-neutral-9); }' });
      const out = outBox(), msg = h('div');
      const run = fn => {
        const r = fn(input.el.value);
        out.textContent = r;
        const a = new Blob([input.el.value]).size, b = new Blob([r]).size;
        msg.replaceChildren(status('ok', `${a} → ${b} bytes${a ? ` (${Math.round((1 - b / a) * 100)}% ${b <= a ? 'smaller' : 'larger'})` : ''}.`));
      };
      ui().add(el, input.field, h('div', { class: 'row' }, button('Minify', () => run(minifyCss), true), button('Format', () => run(beautifyCss)), copyOut(out, 'CSS')), msg, h('div', { class: 'field' }), out);
    },
  };

  window.omlTools = [json, spec, b64, url, jwt, guid, time, diff, regex, contrast, css];
  // Small self-checks for the logic that is easy to get wrong; a failure shows up as a script error in the page check.
  window.omlToolTests = () => {
    const eq = (a, b, what) => { if (a !== b) throw new Error(`tools self-check ${what}: ${JSON.stringify(a)} !== ${JSON.stringify(b)}`); };
    eq(minifyCss('a :hover { color: red ; background: url("x;y.png") }\n/* c */ b{margin:0 auto;}'), 'a :hover{color:red;background:url("x;y.png")}b{margin:0 auto}', 'minify');
    eq(beautifyCss('a,b{color:red;background:url("x;y.png")}'), 'a, b {\n  color: red;\n  background: url("x;y.png");\n}\n', 'beautify');
    eq(stripComments('{"a":"//x", /* c */ "b":1} // end').replace(/\s+/g, ''), '{"a":"//x","b":1}', 'jsonc');
    eq(diffLines(['a', 'b', 'c'], ['a', 'c', 'd']).map(r => r[0]).join(''), ' - +', 'diff');
    const errors = []; validate({ entities: [{ nme: 'X' }] }, { type: 'object', properties: { entities: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'], additionalProperties: false } } }, additionalProperties: false }, '', errors, {});
    eq(errors.length, 2, 'schema');
    eq(badJsonAt('{\n  "a": 1,\n  "b": [1, 2,]\n}').at, 25, 'json position');
    eq(badJsonAt('{"a": [1, {"b": null}]}'), null, 'valid json');
    eq(Math.round((luminance([255, 255, 255]) + 0.05) / (luminance([0, 0, 0]) + 0.05)), 21, 'contrast');
    return true;
  };
})();
