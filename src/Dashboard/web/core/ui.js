import { h, add, icon } from './dom.js';
import { store } from './store.js';
import { api, need, ApiError, runs, problem, KEY } from './api.js';
import { onLeave } from './lifecycle.js';
import { workset } from './workset.js';
import { viewer, viewSrc } from './viewer.js';

export const CONNECT_NOTE = 'Service Studio will ask you to allow the connection once.';

let toastTimer;
export function toast(text) {
  const el = document.getElementById('toast');
  el.textContent = text;
  el.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.classList.remove('show'), 2200);
}

export async function copy(text, what) {
  try { await navigator.clipboard.writeText(text); toast((what || 'Text') + ' copied'); }
  catch {
    const area = h('textarea', { class: 'sr-only', 'aria-hidden': 'true' });
    area.value = text; document.body.append(area); area.select();
    try { document.execCommand('copy'); toast((what || 'Text') + ' copied'); } catch { toast('Copy did not work. Select the text and press Ctrl+C.'); }
    area.remove();
  }
}

// aria: the accessible name when the same label repeats on a page ("Copy name" in every row).
export function copyButton(getText, label, what, aria) {
  return h('button', { class: 'button small', type: 'button', 'aria-label': aria, onclick: () => copy(typeof getText === 'function' ? getText() : getText, what) }, icon('copy'), label || 'Copy');
}

const seconds = ms => ms >= 1000 ? (ms / 1000).toFixed(1) + ' s' : ms + ' ms';

// The style attribute is blocked by the page's CSP; a CSSOM width is not.
function bar(percent) { const el = h('span'); el.style.width = Math.max(0.5, Math.min(100, percent)).toFixed(1) + '%'; return el; }

// --timings: one bar per phase, as a share of the total.
function phases(timings) {
  const total = timings.total || Object.values(timings).reduce((a, b) => a + b, 0) || 1;
  const list = Object.entries(timings).filter(([k]) => k !== 'total');
  return h('details', { class: 'phases', open: true }, h('summary', {}, `Timings: ${seconds(total)} in ${list.length} phases`),
    h('ul', { 'aria-label': 'Time per phase' }, list.map(([name, ms]) => h('li', {},
      h('span', { class: 'phase-name' }, name), h('span', { class: 'phase-bar', 'aria-hidden': 'true' }, bar(ms * 100 / total)),
      h('span', { class: 'num tiny' }, seconds(ms))))));
}

// The CLI command for an action, with a copy button: people learn the CLI from here. Right after a run of that
// command (see api()), it also says how long the run took and, with "Show timings", the phases.
export function cli(command) {
  const run = runs.get(command);
  runs.delete(command);
  const box = h('div', { class: 'cli' }, h('span', { class: 'cli-label', 'aria-hidden': 'true' }, 'CLI'),
    h('code', { text: command }), run ? h('span', { class: 'tiny muted done' }, (run.ok ? 'Done in ' : 'Ended after ') + seconds(run.ms)) : null,
    copyButton(command, 'Copy', 'Command', 'Copy the command ' + command));
  return run && run.timings ? h('div', {}, box, phases(run.timings)) : box;
}

export function download(name, data, type, base64) {
  let blob;
  if (base64) {
    const bin = atob(data); const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    blob = new Blob([bytes], { type: type || 'application/octet-stream' });
  } else blob = new Blob([data], { type: type || 'text/plain' });
  const url = URL.createObjectURL(blob);
  const a = h('a', { href: url, download: name });
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 30000);
}

// Large results (zips, .oap files, reports) come from /download/<id>: a random id that works for 10 minutes.
// A link cannot send the X-Oml-Key header, so the key goes in the query, as in the page address.
export const downloadUrl = id => '/download/' + encodeURIComponent(id) + '?key=' + encodeURIComponent(KEY);
export function downloadLink(id, label, name) {
  return h('a', { class: 'button small', href: downloadUrl(id), download: name || '', 'aria-label': name ? label + ': ' + name : label }, icon('download'), label);
}
export function blobUrl(base64, type) {
  const bin = atob(base64); const bytes = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
  return URL.createObjectURL(new Blob([bytes], { type }));
}

export function bytes(n) {
  if (n === undefined || n === null) return '';
  if (n < 1024) return n + ' B';
  if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
  return (n / 1024 / 1024).toFixed(2) + ' MB';
}
export const fileName = p => String(p || '').split(/[\\/]/).pop();

export function errorBox(err) {
  const data = err && err.data;
  const details = data && (data.output !== undefined || data.stderr)
    ? h('details', {}, h('summary', {}, 'Show the CLI output'),
        h('pre', { text: [data.output === undefined || data.output === null ? '' : (typeof data.output === 'string' ? data.output : JSON.stringify(data.output, null, 2)), data.stderr || ''].filter(Boolean).join('\n\n') }))
    : null;
  return h('div', { class: 'message error', role: 'alert' },
    h('strong', {}, 'That did not work. '), String(err && err.message || err), details,
    data && data.cli ? cli(data.cli) : null);
}

// A spinner with seconds. After 2 s it also says which command the dashboard runs now: commands run one at a time,
// so a click can wait for an earlier one.
function working(label) {
  const secondsEl = h('span', { class: 'tiny' });
  const queue = h('span', { class: 'tiny queue' });
  const el = h('div', { class: 'working', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), h('span', {}, label), secondsEl, queue);
  const start = Date.now();
  let asking = false;
  const timer = setInterval(() => {
    if (!el.isConnected) { clearInterval(timer); return; }
    const s = Math.round((Date.now() - start) / 1000);
    secondsEl.textContent = s >= 2 ? `${s} s` : '';
    if (s >= 2 && s % 2 === 0 && !asking) {
      asking = true;
      api('queue').then(q => {
        queue.textContent = q.running ? `Now running: ${q.running} (${q.seconds} s)` + (q.waiting ? `. ${q.waiting} more waiting.` : '') : '';
      }).catch(() => { /* the spinner still counts */ }).finally(() => { asking = false; });
    }
  }, 1000);
  return el;
}

// Runs one action: the button waits, the output area shows progress, then the result or a friendly error.
export async function task(button, out, label, fn) {
  if (button) button.disabled = true;
  out.replaceChildren(working(label));
  try {
    const result = await fn();
    out.replaceChildren();
    return result;
  } catch (e) {
    out.replaceChildren(errorBox(e));
    return null;
  } finally {
    if (button) button.disabled = false;
  }
}

// A row of toggle buttons (one pressed): theme, width, tabs.
export function segmented(label, options, current, set) {
  const group = h('div', { class: 'segmented', role: 'group', 'aria-label': label });
  for (const [value, text] of options) {
    const b = h('button', { type: 'button', 'aria-pressed': String(value === current) }, text);
    b.addEventListener('click', () => { group.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); set(value); });
    add(group, b);
  }
  return group;
}

export function dropZone(accept, label, onFiles) {
  const input = h('input', { type: 'file', multiple: true, accept, class: 'sr-only', tabindex: '-1', 'aria-hidden': 'true' });
  const choose = h('button', { class: 'button', type: 'button', onclick: () => input.click() }, icon('upload'), 'Choose files');
  const zone = h('div', { class: 'drop' }, icon('upload'), h('p', {}, h('strong', {}, label)), h('p', { class: 'muted small' }, 'Drag and drop files here, or'), choose, input);
  input.addEventListener('change', () => { if (input.files.length) onFiles([...input.files]); input.value = ''; });
  zone.addEventListener('dragover', e => { e.preventDefault(); zone.classList.add('over'); });
  zone.addEventListener('dragleave', () => zone.classList.remove('over'));
  zone.addEventListener('drop', e => { e.preventDefault(); zone.classList.remove('over'); if (e.dataTransfer.files.length) onFiles([...e.dataTransfer.files]); });
  return zone;
}

const MAX_UPLOAD = 140 * 1024 * 1024; // base64 adds a third; the server takes 200 MB per request
export function readFiles(files) {
  const total = files.reduce((s, f) => s + f.size, 0);
  if (total > MAX_UPLOAD) return Promise.reject(new ApiError(`These files are ${bytes(total)}. Send at most ${bytes(MAX_UPLOAD)} at once.`));
  return Promise.all(files.map(f => new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve({ name: f.name, data: String(reader.result).split(',')[1] || '' });
    reader.onerror = () => reject(new ApiError('Could not read ' + f.name + '.'));
    reader.readAsDataURL(f);
  })));
}

// A command result with a non-zero exit code becomes an error with the CLI's own text attached.
// A pull copy with errors (exit 1) is kept, but the person must compare it with Service Studio first.
export function pullWarning(r) {
  const o = r && r.output;
  if (!o || !o.attention) return null;
  const ids = (o.errors || []).map(e => `${e.id} (${e.count})`).join(', ');
  return h('div', { class: 'message warn', role: 'alert' }, h('strong', {}, 'Check this copy. '), o.attention, ids ? h('div', { class: 'small' }, 'Errors: ' + ids) : null);
}

// `oml lt download`: GET only. The .oap stays in the work folder; with extract, so do its modules.
export async function ltDownload(app, env, extract, button, out, showFiles) {
  const data = await task(button, out, `Downloading ${app} from ${env}…`, () => api('lt/download', { app, env, extract }));
  if (!data) return null;
  const o = data.output || {};
  const failed = data.code !== 0;
  add(out, data.download ? h('div', { class: 'row' }, h('span', { class: 'badge ok' }, `${o.application} · ${bytes(o.bytes)}`), downloadLink(data.download, 'Download .oap', fileName(o.file))) : null,
    o.file && data.download ? h('p', { class: 'hint' }, 'Saved in ', h('code', { text: o.file })) : null,
    failed ? errorBox(new ApiError(problem(data), data)) : null,
    !failed && extract && showFiles ? showFiles(o.extracted || []) : null, failed ? null : cli(data.cli));
  return failed ? null : data;
}

// An architecture or check report: badges, the page in a sandboxed frame, and its files from /download/<id>.
export function reportView(data, title, name) {
  const report = data.output || {};
  const findings = report.summary && typeof report.summary === 'object' ? { high: report.summary.high || 0, medium: report.summary.medium || 0, low: report.summary.low || 0 }
    : report.findings && typeof report.findings === 'object' && !Array.isArray(report.findings) ? report.findings : null;
  return h('div', { class: 'stack' },
    h('div', { class: 'row' },
      report.modules !== undefined ? h('span', { class: 'badge' }, `${Array.isArray(report.modules) ? report.modules.length : report.modules} modules`) : null,
      report.applications !== undefined ? h('span', { class: 'badge' }, `${Array.isArray(report.applications) ? report.applications.length : report.applications} applications`) : null,
      findings ? h('span', { class: 'badge ' + (Object.values(findings).some(v => v > 0) ? 'warn' : 'ok') }, 'Findings: ' + Object.entries(findings).map(([k, v]) => `${v} ${k}`).join(', ')) : null,
      h('span', { class: 'grow' }),
      data.download ? downloadLink(data.download, 'Download HTML', name + '.html') : null,
      data.jsonDownload ? downloadLink(data.jsonDownload, 'Download JSON', name + '.json') : null),
    data.path ? h('p', { class: 'hint' }, 'Saved in ', h('code', { text: data.path })) : null,
    data.view ? viewer({ view: data.view, title, width: 1400, height: 1150, themed: true }) : null,
    cli(data.cli));
}

// A path field with the recent modules as suggestions (Review before/after, rules file, BDDFramework module).
let fieldCount = 0;
// modules: also suggest the working set (a module field, not a rules or baseline file).
export function pathField(label, value, placeholder, hint, modules) {
  const id = 'path-' + (++fieldCount);
  const input = h('input', { type: 'text', id, class: 'code', list: id + '-recent', value: value || '', placeholder, autocomplete: 'off', spellcheck: 'false' });
  const paths = [...(modules ? workset.list().map(m => m.path) : []), ...store.get('recent', [])].filter((p, i, all) => all.findIndex(q => q.toLowerCase() === p.toLowerCase()) === i);
  const el = h('div', { class: 'field' }, h('label', { for: id }, label), input,
    h('datalist', { id: id + '-recent' }, paths.map(p => h('option', { value: p }))), hint ? h('p', { class: 'hint' }, hint) : null);
  return { el, input, value: () => input.value.trim().replace(/^"(.*)"$/, '$1') };
}

export const staged = path => String(path).replace(/(\.staged)?\.oml$/i, '.staged.oml');
export const severityBadge = s => h('span', { class: 'badge ' + (s === 'high' ? 'fail' : s === 'medium' ? 'warn' : '') }, s);

export function selectFilter(label, values) {
  const id = 'filter-' + (++fieldCount);
  const select = h('select', { id }, h('option', { value: '' }, 'All'), values.map(v => h('option', { value: v }, v)));
  return { el: h('div', { class: 'field' }, h('label', { for: id }, label), select), select };
}

// A file dropped outside a drop zone would make the browser open it and leave the dashboard. Every drag of files over
// the page shows an overlay that says where to drop; a drop outside a zone is stopped and explained. A page that takes
// files anywhere sets onPageDrop(files => ...) (cleared when the page is left).
let pageDrop = null;
export const onPageDrop = fn => { pageDrop = fn; onLeave(() => { pageDrop = null; }); };

export function dropGuard() {
  const text = h('p', {});
  const overlay = h('div', { class: 'drop-overlay', hidden: true, 'aria-hidden': 'true' }, text);
  document.body.append(overlay);
  const root = document.documentElement;
  const hasFiles = e => !!e.dataTransfer && [...e.dataTransfer.types].includes('Files');
  const where = () => pageDrop ? 'Drop the files anywhere on this page.'
    : document.querySelector('#page .drop') ? 'Drop the files on the dashed box.'
    : 'This page takes no files. Images and fonts: open Fonts and images. Modules for an architecture page: open Architecture.';
  let depth = 0;
  const hide = () => { depth = 0; overlay.hidden = true; root.classList.remove('dragging-files'); };
  window.addEventListener('dragenter', e => {
    if (!hasFiles(e)) return;
    if (depth++ === 0) { text.textContent = where(); overlay.hidden = false; root.classList.add('dragging-files'); }
  });
  window.addEventListener('dragleave', e => { if (hasFiles(e) && --depth <= 0) hide(); });
  window.addEventListener('dragover', e => { if (hasFiles(e)) e.preventDefault(); }); // without this the drop opens the file
  window.addEventListener('drop', e => {
    hide();
    if (e.defaultPrevented || !hasFiles(e)) return; // a drop zone took it
    e.preventDefault();
    if (pageDrop) pageDrop([...e.dataTransfer.files]); else toast(where());
  });
}
