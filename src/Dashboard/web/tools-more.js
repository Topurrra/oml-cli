// Developer tools for the dashboard (second half). Same rules as tools.js: browser only, no server calls.
import { h, add } from './core/dom.js';
import { onLeave } from './core/lifecycle.js';
import { area, text, button, outBox, status, copyOut, id, tooLong } from './tools-ui.js';

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
    add(el, now, h('div', { class: 'grid-2' }, h('div', {}, stamp.field, button('To date', fromStamp, true)), h('div', {}, date.field, button('To timestamp', fromDate))),
      h('div', { class: 'field' }), out);
    tick();
  },
};

// ---- Diff ----
export function diffLines(a, b) {
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
      const big = tooLong(left.el.value, 2 * 1024 * 1024) || tooLong(right.el.value, 2 * 1024 * 1024);
      if (big) { out.replaceChildren(big); return; }
      const norm = s => s.replace(/\r/g, '').split('\n').map(l => trim.checked ? l.trim().replace(/\s+/g, ' ') : l);
      const a = norm(left.el.value), b = norm(right.el.value);
      // The comparison keeps a table of every line pair: 3000 × 3000 lines is the most this page does at once.
      if (a.length > 3000 || b.length > 3000) { out.replaceChildren('Each text can have at most 3000 lines here.'); return; }
      const rows = diffLines(a, b);
      const added = rows.filter(r => r[0] === '+').length, removed = rows.filter(r => r[0] === '-').length;
      out.replaceChildren(h('div', { class: 'muted' }, added + removed === 0 ? 'The texts are the same.' : `${added} line(s) added, ${removed} removed.`),
        ...rows.map(([k, line]) => h('div', { class: k === '+' ? 'add' : k === '-' ? 'del' : '' }, `${k} ${line}`)));
    };
    add(el, h('div', { class: 'grid-2' }, left.field, right.field), h('label', { class: 'check', for: trim.id }, trim, 'Ignore spaces at the start and end of lines'),
      button('Compare', compare, true), h('div', { class: 'field' }), out);
  },
};

// ---- Regex ----
// The match runs in regex-worker.js: a pattern that backtracks for ever (such as (a+)+$ on a long text) stops the worker
// after REGEX_LIMIT ms, never this page. A new keystroke ends the run before it.
const REGEX_LIMIT = 1500;
const MAX_REGEX_TEXT = 1024 * 1024;
const regex = {
  id: 'regex', name: 'Regex tester', desc: 'Try a JavaScript regular expression on a text and see every match and group.',
  render(el) {
    const pattern = text('Pattern', { placeholder: '(\\w+)@example\\.com' });
    const flags = text('Flags', { value: 'gi' });
    const input = area('Text', { rows: 8, placeholder: 'ana@example.com, rui@example.com' });
    const out = h('div', { class: 'out', 'aria-live': 'polite' }), list = h('div');
    let worker = null, timer = null, debounce = null;
    const stop = () => { if (worker) worker.terminate(); worker = null; clearTimeout(timer); };
    onLeave(stop);
    const run = () => {
      stop();
      list.replaceChildren();
      const s = input.el.value, p = pattern.el.value;
      if (!/^[dgimsuyv]*$/.test(flags.el.value)) { out.replaceChildren(h('span', { class: 'saved-bad' }, 'Flags can only be d, g, i, m, s, u, v and y.')); return; }
      if (s.length > MAX_REGEX_TEXT) { out.replaceChildren(h('span', { class: 'saved-bad' }, `The text is ${s.length} characters. Use at most ${MAX_REGEX_TEXT} here.`)); return; }
      if (!p) { out.replaceChildren(s); return; }
      worker = new Worker('regex-worker.js');
      const mine = worker;
      timer = setTimeout(() => {
        if (worker !== mine) return;
        stop();
        out.replaceChildren(h('span', { class: 'saved-bad' }, `Stopped after ${REGEX_LIMIT / 1000} s: this pattern takes too long on this text (it backtracks too much). Make the repeated parts more exact.`));
      }, REGEX_LIMIT);
      worker.onmessage = e => {
        if (worker !== mine) return;
        stop();
        const { error, matches } = e.data;
        if (error) { out.replaceChildren(h('span', { class: 'saved-bad' }, error)); return; }
        const parts = [];
        let last = 0;
        for (const m of matches) { parts.push(s.slice(last, m.index), h('mark', {}, m.text)); last = m.index + m.text.length; }
        parts.push(s.slice(last));
        out.replaceChildren(...parts);
        add(list, matches.length ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', { class: 'num' }, '#'), h('th', {}, 'Match'), h('th', {}, 'Groups'))),
          h('tbody', {}, matches.map((m, i) => h('tr', {}, h('td', { class: 'num' }, i + 1), h('td', { class: 'mono' }, m.text), h('td', { class: 'mono small' }, m.groups.map((g, j) => `$${j + 1}=${g === null ? '(none)' : g}`).join('  ')))))))
          : h('p', { class: 'muted' }, 'No match.'), matches.length >= 1000 ? h('p', { class: 'hint' }, 'Only the first 1000 matches are shown.') : null);
      };
      worker.postMessage({ pattern: p, flags: flags.el.value, text: s });
    };
    [pattern.el, flags.el, input.el].forEach(x => x.addEventListener('input', () => { clearTimeout(debounce); debounce = setTimeout(run, 200); }));
    add(el, h('div', { class: 'grid-2' }, pattern.field, flags.field), input.field, out, h('div', { class: 'field' }), list);
  },
};

// ---- Contrast ----
function rgb(value) {
  const m = /^#?([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(value.trim());
  if (!m) return null;
  const hex = m[1].length === 3 ? m[1].split('').map(c => c + c).join('') : m[1];
  return [0, 2, 4].map(i => parseInt(hex.slice(i, i + 2), 16));
}
export const luminance = c => { const [r, g, b] = c.map(v => { v /= 255; return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4; }); return 0.2126 * r + 0.7152 * g + 0.0722 * b; };

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
    add(el, h('div', { class: 'grid-2' }, fg.el, bg.el), out);
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
export function minifyCss(src) {
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
  return out.replace(/\u0000\s*/g, '').replace(/\u0001 ?/g, '').replace(/;}/g, '}').trim();
}
// Strings and comments are masked first, so their ; { } : stay as they are.
export function beautifyCss(src) {
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
      const big = tooLong(input.el.value);
      if (big) { out.textContent = ''; msg.replaceChildren(big); return; }
      const r = fn(input.el.value);
      out.textContent = r;
      const a = new Blob([input.el.value]).size, b = new Blob([r]).size;
      msg.replaceChildren(status('ok', `${a} → ${b} bytes${a ? ` (${Math.round((1 - b / a) * 100)}% ${b <= a ? 'smaller' : 'larger'})` : ''}.`));
    };
    add(el, input.field, h('div', { class: 'row' }, button('Minify', () => run(minifyCss), true), button('Format', () => run(beautifyCss)), copyOut(out, 'CSS')), msg, h('div', { class: 'field' }), out);
  },
};

export const TOOLS_B = [time, diff, regex, contrast, css];
