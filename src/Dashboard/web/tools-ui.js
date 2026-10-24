import { h } from './core/dom.js';
import { copyButton } from './core/ui.js';

let uid = 0;
export const id = name => `t-${name}-${++uid}`;

export function area(label, opts) {
  const i = id('area');
  const el = h('textarea', { id: i, class: 'code', rows: (opts && opts.rows) || 8, spellcheck: 'false', placeholder: (opts && opts.placeholder) || '' });
  if (opts && opts.value) el.value = opts.value;
  return { el, field: h('div', { class: 'field' }, h('label', { for: i }, label), el) };
}

export function text(label, opts) {
  const i = id('text');
  const el = h('input', { type: (opts && opts.type) || 'text', id: i, class: 'code', spellcheck: 'false', placeholder: (opts && opts.placeholder) || '', value: (opts && opts.value) || '' });
  return { el, field: h('div', { class: 'field' }, h('label', { for: i }, label), el) };
}

export const button = (label, onclick, primary) => h('button', { class: 'button' + (primary ? ' primary' : ''), type: 'button', onclick }, label);
export const outBox = () => h('pre', { class: 'out', 'aria-live': 'polite' });
export const status = (kind, message) => h('div', { class: 'message ' + (kind === 'error' ? 'error' : ''), role: kind === 'error' ? 'alert' : 'status' },
  kind === 'error' ? h('strong', {}, 'Problem: ') : kind === 'ok' ? h('span', { class: 'badge ok' }, 'OK') : null, ' ', message);
export const copyOut = (out, what) => copyButton(() => out.textContent, 'Copy result', what);
