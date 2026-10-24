// Developer tools for the dashboard (first half). They run only in the browser; the spec check fetches
// `oml spec schema` once. Each tool is { id, name, desc, render(el) } and builds its own small form.
import { h, add } from './core/dom.js';
import { api, need, ApiError } from './core/api.js';
import { cli, errorBox } from './core/ui.js';
import { area, text, button, outBox, status, copyOut } from './tools-ui.js';

// ---- JSON ----
// Where JSON.parse failed. Newer browsers no longer say "at position N", so a small scanner finds the spot itself.
export function badJsonAt(src) {
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
      catch (e) { add(msg, h('div', { class: 'message error', role: 'alert' }, h('strong', {}, 'Not valid JSON. '), h('pre', { text: jsonError(input.el.value, e) }))); return; }
      if (mode === 'check') { add(msg, status('ok', 'Valid JSON.')); return; }
      out.textContent = mode === 'min' ? JSON.stringify(value) : JSON.stringify(value, null, indent.value === 'tab' ? '\t' : +indent.value);
      add(msg, status('ok', `Valid JSON. ${input.el.value.length} → ${out.textContent.length} characters.`));
    };
    add(el, input.field, h('div', { class: 'row' }, button('Format', () => run('format'), true), button('Minify', () => run('min')), button('Check', () => run('check')), indent, copyOut(out, 'JSON')), msg, h('div', { class: 'field' }), out);
  },
};

// ---- oml spec ----
let schemaPromise = null;
function schema() {
  if (!schemaPromise) {
    let cached = null;
    try { cached = JSON.parse(sessionStorage.getItem('oml-spec-schema') || 'null'); } catch { cached = null; }
    schemaPromise = cached ? Promise.resolve(cached) : api('spec/schema').then(r => {
      need(r);
      try { sessionStorage.setItem('oml-spec-schema', JSON.stringify(r.output)); } catch { /* not cached */ }
      return r.output;
    });
    schemaPromise.catch(() => { schemaPromise = null; });
  }
  return schemaPromise;
}

// JSONC: drop comments outside strings. Trailing commas stay, so JSON.parse refuses them as `oml apply` does.
export function stripComments(src) {
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
export function validate(value, s, path, errors, rootSchema) {
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
      try { s = await schema(); } catch (e) { msg.replaceChildren(errorBox(e)); return; }
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
        cli('oml spec check C:\\Work\\change.json'));
    };
    const showKeys = async () => {
      let s;
      try { s = await schema(); } catch (e) { keys.replaceChildren(errorBox(e)); return; }
      keys.replaceChildren(h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', {}, 'Top-level key'), h('th', {}, 'What it adds'))),
        h('tbody', {}, Object.entries(s.properties || {}).filter(([k]) => !k.startsWith('$')).map(([k, v]) => h('tr', {}, h('td', {}, h('code', { text: k })), h('td', { class: 'small' }, v.description || '')))))),
        cli('oml spec entities'));
    };
    add(el, input.field, h('div', { class: 'row' }, button('Check spec', check, true), button('Show the spec keys', showKeys)), msg, keys);
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
      } catch { add(msg, status('error', 'This is not Base64 of UTF-8 text.')); }
    };
    add(el, input.field, h('label', { class: 'check', for: urlSafe.id }, urlSafe, 'URL-safe (- and _ , no padding)'),
      h('div', { class: 'row' }, button('Encode', encode, true), button('Decode', decode), copyOut(out, 'Result')), msg, h('div', { class: 'field' }), out);
  },
};

// ---- URL ----
const url = {
  id: 'url', name: 'URL encode', desc: 'Encode text for use in a URL, or decode %20-style text.',
  render(el) {
    const input = area('Text', { rows: 5, placeholder: 'Orders & Invoices/2026' });
    const out = outBox(), msg = h('div');
    const go = fn => { msg.replaceChildren(); try { out.textContent = fn(input.el.value); } catch { out.textContent = ''; add(msg, status('error', 'This text has a broken % sequence.')); } };
    add(el, input.field, h('div', { class: 'row' },
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
      if (parts.length < 2) { add(out, status('error', 'A JWT has three parts separated by dots.')); return; }
      let header, payload;
      try { header = part(parts[0]); payload = part(parts[1]); } catch { add(out, status('error', 'The header or payload is not Base64URL JSON.')); return; }
      const times = ['exp', 'iat', 'nbf'].filter(k => typeof payload[k] === 'number').map(k => {
        const d = new Date(payload[k] * 1000);
        const note = k === 'exp' ? (d < new Date() ? ' (expired)' : ' (still valid)') : '';
        return h('li', {}, h('code', { text: k }), ` ${d.toLocaleString()} · ${d.toISOString()}${note}`);
      });
      add(out, h('h3', {}, 'Header'), h('pre', { class: 'out', text: JSON.stringify(header, null, 2) }),
        h('h3', {}, 'Payload'), h('pre', { class: 'out', text: JSON.stringify(payload, null, 2) }),
        times.length ? h('ul', { class: 'small' }, times) : null,
        h('p', { class: 'hint' }, 'The signature is not checked. Do not trust these claims without checking it on the server.'));
    };
    add(el, input.field, button('Decode', decode, true), h('div', { class: 'field' }), out);
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
    add(el, h('div', { class: 'grid-2' }, count.field, h('div', {},
      h('label', { class: 'check', for: upper.id }, upper, 'Upper case'), h('label', { class: 'check', for: braces.id }, braces, 'With { }'), h('label', { class: 'check', for: plain.id }, plain, 'Without dashes'))),
      h('div', { class: 'row' }, button('Generate', make, true), copyOut(out, 'GUIDs')), h('div', { class: 'field' }), out);
    make();
  },
};

export const TOOLS_A = [json, spec, b64, url, jwt, guid];
