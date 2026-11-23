import { h } from './core/dom.js';
import { copy } from './core/ui.js';
import { iconButton } from './core/table.js';

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
export const copyOut = (out, what) => iconButton('copy', 'Copy the ' + (what || 'result').toLowerCase(), () => copy(out.textContent, what || 'Result'));

// The tools work on text in this page; a very large paste would freeze it. 5 MB is far more than a spec, a token or
// a style sheet. tooLong returns the message to show, or null.
export const MAX_TEXT = 5 * 1024 * 1024;
export const tooLong = (value, max = MAX_TEXT) =>
  value.length > max ? status('error', `This text is ${(value.length / 1024 / 1024).toFixed(1)} MB. This tool takes at most ${(max / 1024 / 1024).toFixed(max < 1024 * 1024 ? 2 : 0)} MB.`) : null;
