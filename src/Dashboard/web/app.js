// oml dashboard: the page shell, the API client and the pages that call oml. The developer tools live in tools.js.
// Plain JavaScript, no libraries. Every element is built with h(), so text from modules and files is never parsed as HTML.
'use strict';
(() => {
  const KEY = new URLSearchParams(location.search).get('key') || '';
  const root = document.documentElement;

  // ---------- small helpers ----------

  function h(tag, attrs, ...children) {
    const el = document.createElement(tag);
    for (const [name, value] of Object.entries(attrs || {})) {
      if (value === null || value === undefined || value === false) continue;
      if (name === 'class') el.className = value;
      else if (name === 'text') el.textContent = value;
      else if (name.startsWith('on')) el.addEventListener(name.slice(2), value);
      else if (name === 'value') el.value = value;
      else if (name === 'checked') el.checked = !!value;
      else el.setAttribute(name, value === true ? '' : value);
    }
    add(el, children);
    return el;
  }
  // Like Element.append, but skips null, undefined and false, so `cond ? node : null` is safe.
  function add(el, ...children) {
    for (const child of children.flat(Infinity)) {
      if (child === null || child === undefined || child === false) continue;
      el.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
    }
    return el;
  }

  const ICONS = {
    home: 'M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z',
    explorer: 'M4 5h6v6H4zM14 5h6M14 9h6M4 15h6M14 15h6v4h-6zM4 19h6',
    modules: 'M4 6h16v4H4zM4 14h16v4H4zM8 8h.01M8 16h.01',
    previews: 'M3 5h18v12H3zM8 21h8M12 17v4',
    prompt: 'M4 5h16v11H8l-4 4zM8 9h8M8 12h5',
    recipes: 'M6 3h9l3 3v15H6zM9 9h6M9 13h6M9 17h4',
    assets: 'M4 5h16v14H4zM4 15l4-4 4 4 3-3 5 5M15 9h.01',
    openapi: 'M8 4H6a2 2 0 0 0-2 2v4l-2 2 2 2v4a2 2 0 0 0 2 2h2M16 4h2a2 2 0 0 1 2 2v4l2 2-2 2v4a2 2 0 0 1-2 2h-2',
    architecture: 'M9 3h6v5H9zM3 16h6v5H3zM15 16h6v5h-6zM12 8v4M6 16v-4h12v4',
    lifetime: 'M12 3a9 9 0 1 0 9 9M12 7v5l3 3M17 3h4v4',
    activity: 'M3 12h4l3-8 4 16 3-8h4',
    cookbook: 'M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3zM5 17a3 3 0 0 1 3-3h11',
    rules: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM9 12l2 2 4-4',
    tools: 'M14 6a4 4 0 0 0-5 5l-6 6 3 3 6-6a4 4 0 0 0 5-5l-2 2-3-3z',
    check: 'M4 4h16v16H4zM8 12l3 3 5-6',
    review: 'M4 5h7v14H4zM13 5h7v14h-7zM6 9h3M15 9h3M6 13h3M15 13h3',
    docs: 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM20 20l-4.8-4.8',
    copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
    download: 'M12 4v11M7 10l5 5 5-5M5 20h14',
    upload: 'M12 20V9M7 14l5-5 5 5M5 4h14',
  };
  function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', ICONS[name] || '');
    add(svg, path);
    return svg;
  }

  const store = {
    get(name, fallback) { try { const v = localStorage.getItem('oml-dash-' + name); return v === null ? fallback : JSON.parse(v); } catch { return fallback; } },
    set(name, value) { try { localStorage.setItem('oml-dash-' + name, JSON.stringify(value)); } catch { /* private window: not remembered */ } },
  };

  let toastTimer;
  function toast(text) {
    const el = document.getElementById('toast');
    el.textContent = text;
    el.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('show'), 2200);
  }

  async function copy(text, what) {
    try { await navigator.clipboard.writeText(text); toast((what || 'Text') + ' copied'); }
    catch {
      const area = h('textarea', { class: 'sr-only', 'aria-hidden': 'true' });
      area.value = text; document.body.append(area); area.select();
      try { document.execCommand('copy'); toast((what || 'Text') + ' copied'); } catch { toast('Copy did not work. Select the text and press Ctrl+C.'); }
      area.remove();
    }
  }
  // aria: the accessible name when the same label repeats on a page ("Copy name" in every row).
  function copyButton(getText, label, what, aria) {
    return h('button', { class: 'button small', type: 'button', 'aria-label': aria, onclick: () => copy(typeof getText === 'function' ? getText() : getText, what) }, icon('copy'), label || 'Copy');
  }

  // The CLI command for an action, with a copy button: people learn the CLI from here. Right after a run of that
  // command (see api()), it also says how long the run took and, with "Show timings", the phases.
  function cli(command) {
    const run = runs.get(command);
    runs.delete(command);
    const box = h('div', { class: 'cli' }, h('span', { class: 'cli-label', 'aria-hidden': 'true' }, 'CLI'),
      h('code', { text: command }), run ? h('span', { class: 'tiny muted done' }, (run.ok ? 'Done in ' : 'Ended after ') + seconds(run.ms)) : null,
      copyButton(command, 'Copy', 'Command', 'Copy the command ' + command));
    return run && run.timings ? h('div', {}, box, phases(run.timings)) : box;
  }
  const seconds = ms => ms >= 1000 ? (ms / 1000).toFixed(1) + ' s' : ms + ' ms';
  // --timings: one bar per phase, as a share of the total.
  function phases(timings) {
    const total = timings.total || Object.values(timings).reduce((a, b) => a + b, 0) || 1;
    const list = Object.entries(timings).filter(([k]) => k !== 'total');
    return h('details', { class: 'phases', open: true }, h('summary', {}, `Timings: ${seconds(total)} in ${list.length} phases`),
      h('ul', { 'aria-label': 'Time per phase' }, list.map(([name, ms]) => h('li', {},
        h('span', { class: 'phase-name' }, name), h('span', { class: 'phase-bar', 'aria-hidden': 'true' }, bar(ms * 100 / total)),
        h('span', { class: 'num tiny' }, seconds(ms))))));
  }

  // The style attribute is blocked by the page's CSP; a CSSOM width is not.
  function bar(percent) { const el = h('span'); el.style.width = Math.max(0.5, Math.min(100, percent)).toFixed(1) + '%'; return el; }

  function download(name, data, type, base64) {
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
  const downloadUrl = id => '/download/' + encodeURIComponent(id) + '?key=' + encodeURIComponent(KEY);
  function downloadLink(id, label, name) {
    return h('a', { class: 'button small', href: downloadUrl(id), download: name || '', 'aria-label': name ? label + ': ' + name : label }, icon('download'), label);
  }
  function blobUrl(base64, type) {
    const bin = atob(base64); const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return URL.createObjectURL(new Blob([bytes], { type }));
  }

  function bytes(n) {
    if (n === undefined || n === null) return '';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / 1024 / 1024).toFixed(2) + ' MB';
  }
  const fileName = p => String(p || '').split(/[\\/]/).pop();

  // ---------- API ----------

  class ApiError extends Error {
    constructor(message, data) { super(message); this.data = data; }
  }

  // The latest run of each CLI line: time taken and --timings, read once by cli().
  const runs = new Map();
  function timingsOf(data) {
    const out = data.output;
    if (out && typeof out === 'object' && !Array.isArray(out) && out.timings) return out.timings;
    const line = (data.stderr || '').split(/\r?\n/).find(l => l.startsWith('{"timings"'));
    try { return line ? JSON.parse(line).timings : null; } catch { return null; }
  }

  async function api(path, body) {
    const started = Date.now();
    if (body && typeof body === 'object' && store.get('timings', false)) body = { ...body, timings: true };
    let res;
    try {
      res = await fetch('/api/' + path, {
        method: body === undefined ? 'GET' : 'POST',
        headers: { 'X-Oml-Key': KEY, 'Content-Type': 'application/json' },
        body: body === undefined ? undefined : JSON.stringify(body),
        cache: 'no-store',
      });
    } catch {
      throw new ApiError('The dashboard is not answering. Is the oml dashboard window still open?', null);
    }
    let data;
    try { data = await res.json(); } catch { data = { error: 'The dashboard sent an answer that is not JSON (HTTP ' + res.status + ').' }; }
    if (data && data.cli) runs.set(data.cli, { ms: Date.now() - started, ok: res.ok && (typeof data.code !== 'number' || data.code === 0), timings: timingsOf(data) });
    if (!res.ok) throw new ApiError(data.error || ('HTTP ' + res.status), data);
    return data;
  }

  // A command result with a non-zero exit code becomes an error with the CLI's own text attached.
  // A pull copy with errors (exit 1) is kept, but the person must compare it with Service Studio first.
  function pullWarning(r) {
    const o = r && r.output;
    if (!o || !o.attention) return null;
    const ids = (o.errors || []).map(e => `${e.id} (${e.count})`).join(', ');
    return h('div', { class: 'message warn', role: 'alert' }, h('strong', {}, 'Check this copy. '), o.attention, ids ? h('div', { class: 'small' }, 'Errors: ' + ids) : null);
  }
  function need(data, okCodes) {
    if (typeof data.code === 'number' && !(okCodes || [0]).includes(data.code)) throw new ApiError(problem(data), data);
    return data;
  }
  function problem(data) {
    const out = data.output;
    const first = v => {
      if (!v) return null;
      if (typeof v === 'string') return v;
      if (Array.isArray(v)) { for (const x of v) { const f = first(x); if (f) return f; } return null; }
      if (typeof v === 'object') {
        for (const k of ['error', 'message', 'problems', 'errors']) { const f = first(v[k]); if (f) return f; }
      }
      return null;
    };
    return first(out) || (data.stderr || '').split('\n')[0] || ('The command ended with exit code ' + data.code + '.');
  }

  function errorBox(err) {
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
    const seconds = h('span', { class: 'tiny' });
    const queue = h('span', { class: 'tiny queue' });
    const el = h('div', { class: 'working', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), h('span', {}, label), seconds, queue);
    const start = Date.now();
    let asking = false;
    const timer = setInterval(() => {
      if (!el.isConnected) { clearInterval(timer); return; }
      const s = Math.round((Date.now() - start) / 1000);
      seconds.textContent = s >= 2 ? `${s} s` : '';
      if (s >= 2 && s % 2 === 0 && !asking) {
        asking = true;
        api('queue').then(q => {
          queue.textContent = q.running ? `Now running: ${q.running} (${q.seconds} s)` + (q.waiting ? `. ${q.waiting} more waiting.` : '.') : '';
        }).catch(() => { /* the spinner still counts */ }).finally(() => { asking = false; });
      }
    }, 1000);
    return el;
  }

  // Runs one action: the button waits, the output area shows progress, then the result or a friendly error.
  async function task(button, out, label, fn) {
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

  // ---------- shared module choice ----------

  // Values after ? in the page address: #/review?before=...&after=..., #/docs?q=...
  const hashParams = () => new URLSearchParams(location.hash.split('?')[1] || '');

  const CONNECT_NOTE = 'Service Studio will ask you to allow the connection once.';
  const state = { module: store.get('module', ''), inspect: {}, info: null };
  function setModule(path) {
    state.module = path;
    store.set('module', path);
    remember(path);
  }
  function remember(path) {
    const recent = [path, ...store.get('recent', []).filter(p => p.toLowerCase() !== path.toLowerCase())].slice(0, 12);
    store.set('recent', recent);
  }

  // ---------- working set ----------
  // The modules a person works with now, from any source ("Get modules" page). Kept in the browser (store 'workset').
  // Other pages read it through window.omlWorkset (also omlUi.workset):
  //   list()                  -> [{ path, name, role, source }], a copy, in the order the modules were added
  //   add(path, source, role) adds a module, or updates its source (and its role when one is given)
  //   remove(path), setRole(path, role), clear()
  //   onChange(fn)            calls fn(list()) after every change; returns the function that stops it (pass it to onLeave)
  //   sourceLine(m)           one plain sentence: where the module came from
  //   changes(), reads()      the modules by role
  // role:   'change' (the agent may change it) or 'read' (read only: a producer, passed with --in).
  // source: { kind, at, ... } with kind 'studio' { module } (a copy saved by oml pull), 'folder' { folder },
  //         'package' { file } (extracted from an .oap), 'drop' { file } (uploaded), 'lifetime' { app, env }, 'path' {}.
  // Paths compare without case, as Windows does.
  const workset = (() => {
    const listeners = new Set();
    const same = (a, b) => String(a).toLowerCase() === String(b).toLowerCase();
    const read = () => store.get('workset', []).filter(m => m && typeof m.path === 'string');
    const save = list => { store.set('workset', list); for (const fn of [...listeners]) { try { fn(list.map(m => ({ ...m }))); } catch { /* a page that is gone */ } } };
    const ws = {
      list: () => read().map(m => ({ ...m })),
      add(path, source, role) {
        const list = read();
        const found = list.find(m => same(m.path, path));
        const src = { kind: 'path', ...(source || {}), at: new Date().toISOString() };
        if (found) { found.source = src; if (role) found.role = role; }
        else list.push({ path, name: fileName(path).replace(/\.oml$/i, ''), role: role === 'read' ? 'read' : 'change', source: src });
        const sources = store.get('sources', {}); sources[path.toLowerCase()] = src; store.set('sources', sources);
        remember(path);
        save(list);
      },
      remove(path) { save(read().filter(m => !same(m.path, path))); },
      setRole(path, role) { const list = read(); const m = list.find(x => same(x.path, path)); if (m) { m.role = role === 'read' ? 'read' : 'change'; save(list); } },
      clear() { save([]); },
      onChange(fn) { listeners.add(fn); return () => listeners.delete(fn); },
      changes: () => ws.list().filter(m => m.role !== 'read'),
      reads: () => ws.list().filter(m => m.role === 'read'),
      sourceLine(m) {
        const s = m.source || {};
        switch (s.kind) {
          case 'studio': return `copied from the module ${s.module || m.name} open in Service Studio (oml pull). This is a copy; do not install over the open module without asking.`;
          case 'folder': return `from the folder ${s.folder}.`;
          case 'package': return `extracted from the application package ${s.file}.`;
          case 'drop': return `uploaded from this computer (${s.file || fileName(m.path)}).`;
          case 'lifetime': return `downloaded from LifeTime: application ${s.app}, as it runs in ${s.env} (oml lt download).`;
          default: return 'a file chosen by path.';
        }
      },
    };
    return ws;
  })();
  window.omlWorkset = workset;

  async function inspect(path, fresh) {
    if (!fresh && state.inspect[path]) return state.inspect[path];
    // "Show change history" in the Explorer adds --history (who changed each element, and when).
    const data = need(await api('inspect', { path, history: store.get('history', false) || undefined }));
    state.inspect[path] = data;
    return data;
  }

  // The working set as buttons that fill a module field, or a link to Get modules when it is empty.
  function worksetQuick(onPick) {
    const set = workset.list();
    return set.length ? h('div', { class: 'row small', role: 'group', 'aria-label': 'Modules in the working set' }, h('span', { class: 'muted' }, 'Working set:'),
      set.map(m => h('button', { class: 'button small', type: 'button', 'aria-label': 'Open ' + m.name + ' from the working set', onclick: () => onPick(m.path) }, m.name)),
      h('a', { href: '#/modules', class: 'small' }, 'Get modules')) : h('p', { class: 'hint' }, h('a', { href: '#/modules' }, 'Get modules'), ' from Service Studio, a folder, a package or LifeTime.');
  }

  let pickerCount = 0;
  // A module picker: type or paste a path, pick a recent one, or pull the module open in Service Studio.
  function modulePicker(onOpen, opts) {
    const id = 'module-' + (++pickerCount);
    const listId = id + '-recent';
    const input = h('input', { type: 'text', id, class: 'code', list: listId, value: state.module, placeholder: 'C:\\Work\\Orders.oml', autocomplete: 'off', spellcheck: 'false' });
    const paths = [...workset.list().map(m => m.path), ...store.get('recent', [])].filter((p, i, all) => all.findIndex(q => q.toLowerCase() === p.toLowerCase()) === i);
    const recent = h('datalist', { id: listId }, paths.map(p => h('option', { value: p })));
    const quick = worksetQuick(path => { input.value = path; go(); });
    const status = h('div');
    const open = h('button', { class: 'button primary', type: 'button' }, (opts && opts.button) || 'Open module');
    const go = async () => {
      const path = input.value.trim().replace(/^"(.*)"$/, '$1');
      if (!path) { input.focus(); status.replaceChildren(h('div', { class: 'message error', role: 'alert' }, 'Type the path of an .oml file first.')); return; }
      setModule(path);
      status.replaceChildren();
      await onOpen(path, open, status);
    };
    open.addEventListener('click', go);
    input.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
    const pull = h('button', { class: 'button', type: 'button' }, 'Copy from Service Studio');
    pull.addEventListener('click', () => pullFlow(pull, status, path => { input.value = path; go(); }));
    return {
      el: h('section', { class: 'card', 'aria-label': 'Choose a module' },
        h('div', { class: 'field' }, h('label', { for: id }, 'Module file (.oml)'), h('div', { class: 'row' }, h('div', { class: 'grow' }, input), open, pull),
          h('p', { class: 'hint' }, 'Paste a full path. In File Explorer: Shift + right-click the file, then "Copy as path". Recent files appear as you type. ',
            'Copy from Service Studio saves a copy of the open module to a file; the open module does not change. ', CONNECT_NOTE), quick),
        recent, status),
      input, go,
    };
  }

  // Asks Service Studio for its open modules; one open module is pulled at once, several give a choice.
  async function pullFlow(button, out, done) {
    const data = await task(button, out, 'Asking Service Studio for its open modules…', async () => need(await api('studio')));
    if (!data) { out.prepend(h('div', { class: 'message info' }, 'Start Service Studio\'s MCP server first: in Service Studio, open Edit > MCP Server > Start MCP server. Then try again.')); return; }
    const open = ((data.output && data.output.apps) || []).filter(a => a.isOpen).map(a => a.name);
    if (open.length === 0) { out.replaceChildren(h('div', { class: 'message info' }, 'No module is open in Service Studio. Open the module there first.'), cli(data.cli)); return; }
    const pullOne = async (name, btn) => {
      const r = await task(btn, out, `Saving ${name} from Service Studio… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: name }), [0, 1]));
      if (r) { workset.add(r.path, { kind: 'studio', module: name }); toast('Saved ' + fileName(r.path)); out.replaceChildren(h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path })), pullWarning(r), cli(r.cli)); done(r.path); }
    };
    if (open.length === 1) { await pullOne(open[0], button); return; }
    out.replaceChildren(h('div', { class: 'message' }, h('p', {}, 'Several modules are open. Which one?'),
      h('div', { class: 'row' }, open.map(name => { const b = h('button', { class: 'button small', type: 'button', 'aria-label': `Copy ${name} from Service Studio` }, name); b.addEventListener('click', () => pullOne(name, b)); return b; }))));
  }

  // ---------- markdown (for the embedded skill pages) ----------

  function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }
  // Code spans are set aside first, so **bold text with `code` inside** still works.
  function inline(text) {
    const codes = [];
    let s = esc(String(text).replace(/`([^`]+)`/g, (m, c) => { codes.push(c); return `\u0000${codes.length - 1}\u0000`; }));
    s = s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
    s = s.replace(/(^|[\s(])\*([^*\s][^*]*?)\*(?=[\s).,:;]|$)/g, '$1<em>$2</em>');
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, label, href) =>
      /^https?:\/\//.test(href.replace(/&amp;/g, '&')) ? `<a href="${href}" target="_blank" rel="noopener noreferrer">${label}</a>` : label);
    return s.replace(/\u0000(\d+)\u0000/g, (m, n) => '<code>' + esc(codes[+n]) + '</code>');
  }
  function markdown(text) {
    const lines = String(text).replace(/\r/g, '').split('\n');
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (/^```/.test(line)) {
        const code = []; i++;
        while (i < lines.length && !/^```/.test(lines[i])) code.push(lines[i++]);
        i++; out.push('<pre><code>' + esc(code.join('\n')) + '</code></pre>'); continue;
      }
      const head = /^(#{1,4})\s+(.*)$/.exec(line);
      if (head) { const n = Math.min(head[1].length + 1, 4); out.push(`<h${n}>${inline(head[2])}</h${n}>`); i++; continue; }
      if (/^\s*\|/.test(line)) {
        const rows = [];
        while (i < lines.length && /^\s*\|/.test(lines[i])) rows.push(lines[i++]);
        const cells = r => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
        const body = rows.filter((r, n) => !(n === 1 && /^[\s|:-]+$/.test(r)));
        out.push('<div class="table-wrap"><table><thead><tr>' + cells(body[0]).map(c => '<th>' + inline(c) + '</th>').join('') + '</tr></thead><tbody>' +
          body.slice(1).map(r => '<tr>' + cells(r).map(c => '<td>' + inline(c) + '</td>').join('') + '</tr>').join('') + '</tbody></table></div>');
        continue;
      }
      const item = /^(\s*)([-*]|\d+\.)\s+(.*)$/.exec(line);
      if (item) {
        const ordered = /\d/.test(item[2]);
        const items = [];
        while (i < lines.length) {
          const m = /^(\s*)([-*]|\d+\.)\s+(.*)$/.exec(lines[i]);
          if (m) { items.push(m[3]); i++; continue; }
          if (/^\s{2,}\S/.test(lines[i]) && items.length) { items[items.length - 1] += ' ' + lines[i].trim(); i++; continue; }
          break;
        }
        out.push((ordered ? '<ol>' : '<ul>') + items.map(t => '<li>' + inline(t) + '</li>').join('') + (ordered ? '</ol>' : '</ul>'));
        continue;
      }
      if (!line.trim()) { i++; continue; }
      const para = [];
      while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|```|\s*\||\s*([-*]|\d+\.)\s)/.test(lines[i])) para.push(lines[i++].trim());
      out.push('<p>' + inline(para.join(' ')) + '</p>');
    }
    const div = h('div', { class: 'doc' });
    div.innerHTML = out.join('\n');
    return div;
  }

  // Light colouring for describe text and JSON(C): keywords, strings, comments, numbers.
  function colorCode(text, kind) {
    const pre = h('pre', { class: 'code-view' });
    const code = h('code');
    const pattern = kind === 'json'
      ? /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("(?:[^"\\\n]|\\.)*")(\s*:)?|\b(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null)\b/g
      : /(^\s*(?:Block|Screen|ScreenAction|ServerAction|ClientAction|ServiceAction|Entity|Structure|locals|inputs|outputs|if|else|for each|end|set|call|javascript|aggregate|sql|navigate|raise|exception|OnRender|OnReady|OnInitialize|Container|Expression|Link|Button|Text|Input|List|If)\b)|("(?:[^"\\\n]|\\.)*")|\b(\d+(?:\.\d+)?|True|False|NullIdentifier\(\))\b/gm;
    let last = 0, m;
    while ((m = pattern.exec(text))) {
      if (m.index > last) add(code, text.slice(last, m.index));
      if (kind === 'json') {
        if (m[1]) add(code, h('span', { class: 'c', text: m[1] }));
        else if (m[2]) { add(code, h('span', { class: m[3] ? 'k' : 's', text: m[2] })); if (m[3]) add(code, m[3]); }
        else add(code, h('span', { class: 'n', text: m[4] }));
      } else {
        if (m[1]) add(code, h('span', { class: 'k', text: m[1] }));
        else if (m[2]) add(code, h('span', { class: 's', text: m[2] }));
        else add(code, h('span', { class: 'n', text: m[3] }));
      }
      last = pattern.lastIndex;
      if (m[0].length === 0) pattern.lastIndex++;
    }
    add(code, text.slice(last));
    add(pre, code);
    return pre;
  }

  // ---------- drop zone ----------

  function dropZone(accept, label, onFiles) {
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
  function readFiles(files) {
    const total = files.reduce((s, f) => s + f.size, 0);
    if (total > MAX_UPLOAD) return Promise.reject(new ApiError(`These files are ${bytes(total)}. Send at most ${bytes(MAX_UPLOAD)} at once.`));
    return Promise.all(files.map(f => new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve({ name: f.name, data: String(reader.result).split(',')[1] || '' });
      reader.onerror = () => reject(new ApiError('Could not read ' + f.name + '.'));
      reader.readAsDataURL(f);
    })));
  }

  // ---------- viewer: one way to show every image and frame ----------
  //
  // viewer(opts) returns an element: a toolbar and the picture. viewer.open(opts) shows the same in a full-window dialog
  // (Esc closes it). Use it for every image and /view/ page, so they all zoom and open the same way.
  //   title       required: the accessible name (the image alt text or the frame title)
  //   src         an image address (blob: or data:), or
  //   view        the id of a /view/<id> page (a preview or a report), with width and height: its size in pixels
  //   themed      true for reports (architecture, check, review): the frame follows the dashboard theme (#theme=)
  //   fullHeight  true for previews: offers "Full height"; the page reports its height (see ReportsHeight in DashboardApi.cs)
  //   before      { src, label }: the original image, for a Before / After switch; label describes it
  //   label       describes the shown image (with before: the result)
  //   maxHeight   the height of an inline image box in pixels (default 520)
  // Controls: Fit, 100%, zoom - and +, Full screen, Open in a new tab. On a focused picture: F fit, 1 real size, + and -
  // zoom, 0 back to fit, arrows scroll. Ctrl + wheel zooms at the pointer and dragging pans (images; a frame keeps its
  // clicks and scrolls with its scroll bars).
  const ZOOMS = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8];
  const viewSrc = opts => '/view/' + opts.view + (opts.themed ? '#theme=' + (root.dataset.theme || 'light') : '');
  function viewer(opts, dialog) {
    const isFrame = !!opts.view;
    let natW = opts.width || 1200, natH = opts.height || 800, fit = true, scale = 1, tall = false, reported = 0, before = false;
    const content = isFrame
      ? h('iframe', { src: viewSrc(opts), title: opts.title, sandbox: 'allow-scripts', loading: 'lazy', 'data-themed': opts.themed ? opts.view : null })
      : h('img', { src: opts.src, alt: opts.title, draggable: 'false' });
    const box = h('div', { class: 'viewer-box' }, content);
    const stage = h('div', { class: 'viewer-stage ' + (isFrame ? 'frame' : 'image'), tabindex: '0', role: 'group',
      'aria-label': opts.title + '. Keys: F fit, 1 real size, plus and minus zoom.' }, box);
    if (!isFrame && !dialog) stage.style.maxHeight = (opts.maxHeight || 520) + 'px';
    const zoomText = h('span', { class: 'mono small zoom-text' });
    const label = h('span', { class: 'small muted grow' }, opts.label || '');
    const button = (text, aria, onclick) => h('button', { class: 'button small', type: 'button', 'aria-label': aria + ': ' + opts.title, onclick }, text);
    const fitButton = button('Fit', 'Fit to the box', () => { fit = true; layout(); });
    const realButton = button('100%', 'Real size, 100%', () => zoomTo(1));
    const fitScale = () => {
      const width = stage.clientWidth || natW;
      if (isFrame) return Math.min(1, width / natW);
      const height = dialog ? stage.clientHeight : (opts.maxHeight || 520);
      return Math.min(width / natW, (height - 4) / natH, 8); // 4: the border, so fit never scrolls
    };
    function layout() {
      const s = fit ? fitScale() : scale;
      const height = tall && reported ? reported : natH;
      if (isFrame) Object.assign(content.style, { width: natW + 'px', height: height + 'px', transform: `scale(${s})` });
      else { Object.assign(content.style, { width: natW * s + 'px', height: natH * s + 'px' }); content.classList.toggle('pixels', s >= 2); }
      box.style.width = Math.round(natW * s) + 'px';
      box.style.height = Math.round(height * s) + 'px';
      zoomText.textContent = Math.round(s * 100) + '%';
      fitButton.setAttribute('aria-pressed', String(fit));
      realButton.setAttribute('aria-pressed', String(!fit && scale === 1));
    }
    // Keeps the point under the pointer (or the middle of the box) in place.
    function zoomTo(next, x = stage.clientWidth / 2, y = stage.clientHeight / 2) {
      const old = fit ? fitScale() : scale;
      scale = Math.min(8, Math.max(Math.min(old, 0.1), next)); fit = false; // a fit below 10% stays the floor
      const k = scale / old, left = (stage.scrollLeft + x) * k - x, top = (stage.scrollTop + y) * k - y;
      layout();
      stage.scrollLeft = left; stage.scrollTop = top;
    }
    const step = dir => {
      const now = fit ? fitScale() : scale;
      return dir > 0 ? (ZOOMS.find(z => z > now + 1e-6) || 8) : ([...ZOOMS].reverse().find(z => z < now - 1e-6) || Math.min(now, 0.1));
    };
    stage.addEventListener('keydown', e => {
      if (e.ctrlKey || e.altKey || e.metaKey) return;
      if (e.key === 'f' || e.key === 'F' || e.key === '0') { fit = true; layout(); }
      else if (e.key === '1') zoomTo(1);
      else if (e.key === '+' || e.key === '=') zoomTo(step(1));
      else if (e.key === '-') zoomTo(step(-1));
      else return;
      e.preventDefault();
    });
    stage.addEventListener('wheel', e => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      const r = stage.getBoundingClientRect();
      zoomTo(step(e.deltaY < 0 ? 1 : -1), e.clientX - r.left, e.clientY - r.top);
    }, { passive: false });
    if (!isFrame) {
      content.addEventListener('load', () => { natW = content.naturalWidth || 1; natH = content.naturalHeight || 1; layout(); });
      let drag = null;
      stage.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        drag = { x: e.clientX, y: e.clientY, left: stage.scrollLeft, top: stage.scrollTop };
        stage.setPointerCapture(e.pointerId); stage.classList.add('dragging');
      });
      stage.addEventListener('pointermove', e => { if (drag) { stage.scrollLeft = drag.left - (e.clientX - drag.x); stage.scrollTop = drag.top - (e.clientY - drag.y); } });
      const end = () => { drag = null; stage.classList.remove('dragging'); };
      stage.addEventListener('pointerup', end); stage.addEventListener('pointercancel', end);
    // While Full height is on, the frame is taller, so the page reports the new frame size: keep the height measured
    // at the natural size, or each report would grow the frame again.
    } else content.omlHeight = px => { if (tall) return; reported = Math.min(20000, Math.max(100, Math.round(px))); content.dataset.height = reported; };
    const tallButton = isFrame && opts.fullHeight ? button('Full height', 'Show the whole page height', () => {
      tall = !tall; tallButton.setAttribute('aria-pressed', String(tall)); layout();
    }) : null;
    if (tallButton) tallButton.setAttribute('aria-pressed', 'false');
    const sides = opts.before ? h('div', { class: 'segmented', role: 'group', 'aria-label': 'Before or after' }, [['Before', true], ['After', false]].map(([text, value]) => {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === before) }, text);
      b.addEventListener('click', () => {
        before = value; sides.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b)));
        content.src = before ? opts.before.src : opts.src; content.alt = before ? 'Before: ' + opts.title : opts.title;
        label.textContent = before ? opts.before.label || '' : opts.label || '';
      });
      return b;
    })) : null;
    const tab = h('a', { class: 'button small', href: isFrame ? viewSrc(opts) : opts.src, target: '_blank', rel: 'noopener', 'aria-label': 'Open in a new tab: ' + opts.title }, 'New tab');
    tab.addEventListener('click', () => { tab.href = isFrame ? viewSrc(opts) : (before ? opts.before.src : opts.src); }); // the theme may have changed
    const bar = h('div', { class: 'viewer-bar' }, sides, label, fitButton, realButton,
      button('−', 'Zoom out', () => zoomTo(step(-1))), zoomText, button('+', 'Zoom in', () => zoomTo(step(1))), tallButton,
      dialog ? button('Close', 'Close the full-screen view', () => dialog.close()) : button('Full screen', 'Full screen', () => viewer.open(opts)), tab);
    requestAnimationFrame(layout);
    if (window.ResizeObserver) {
      // Disconnected when the page changes, and when the viewer was shown and is then removed (a card rendered again).
      let shown = false;
      const watch = new ResizeObserver(() => {
        if (!stage.isConnected) { if (shown) watch.disconnect(); return; }
        shown = true;
        if (fit) layout();
      });
      watch.observe(stage);
      onLeave(() => watch.disconnect());
    }
    return h('div', { class: 'viewer' }, bar, stage);
  }
  viewer.open = opts => {
    const dialog = h('dialog', { class: 'viewer-dialog', 'aria-label': opts.title });
    add(dialog, viewer(opts, dialog));
    document.body.append(dialog);
    dialog.addEventListener('close', () => dialog.remove());
    dialog.showModal();
    dialog.querySelector('.viewer-stage').focus();
    onLeave(() => { if (dialog.open) dialog.close(); });
  };
  // A sandboxed /view/ page has no origin; a preview reports its height with postMessage (only a number is taken).
  window.addEventListener('message', e => {
    const height = e.data && typeof e.data.omlHeight === 'number' ? e.data.omlHeight : null;
    if (height === null) return;
    for (const f of document.querySelectorAll('.viewer iframe')) if (f.contentWindow === e.source && f.omlHeight) f.omlHeight(height);
  });
  // ---------- pages ----------

  // A row of toggle buttons (one pressed): theme, width, tabs.
  function segmented(label, options, current, set) {
    const group = h('div', { class: 'segmented', role: 'group', 'aria-label': label });
    for (const [value, text] of options) {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === current) }, text);
      b.addEventListener('click', () => { group.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); set(value); });
      add(group, b);
    }
    return group;
  }


  const pages = [];
  const page = (id, group, title, desc, render) => pages.push({ id, group, title, desc, render });
  let cleanups = [];
  const onLeave = fn => cleanups.push(fn);

  // 1. Home / Setup
  page('home', 'Start', 'Home', 'Check your setup, open the module you work on, and find your way around.', async main => {
    const doctorOut = h('div');
    const again = h('button', { class: 'button', type: 'button' }, 'Check again');
    // The page itself never calls `studio` or `pull`: they ask Service Studio for a session, which shows a dialog
    // there. The server check below comes from `doctor` (an MCP initialize only); the rest waits for a click.
    const studioStatus = h('div', { class: 'row small' }, h('span', { class: 'muted' }, 'Checking whether the MCP server answers…'));
    const studioOut = h('div');
    const studioAgain = h('button', { class: 'button', type: 'button' }, 'Show open modules in Service Studio');
    add(main, 
      h('section', { class: 'card', 'aria-labelledby': 'setup-title' },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'setup-title' }, 'Setup check'), h('p', { class: 'muted small' }, 'What oml needs on this computer, and how to fix what is missing.')), again),
        doctorOut),
      h('section', { class: 'card', 'aria-labelledby': 'studio-title' },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'studio-title' }, 'Service Studio'), h('p', { class: 'muted small' }, 'Modules open in Service Studio. Copy one to a file to explore it here. The open module does not change.'))),
        studioStatus,
        h('div', { class: 'field' }), studioAgain, h('p', { class: 'hint' }, CONNECT_NOTE),
        studioOut),
      h('section', { class: 'card', 'aria-labelledby': 'links-title' },
        h('h2', { id: 'links-title' }, 'Go to'),
        h('div', { class: 'links' }, pages.filter(p => p.id !== 'home').map(p =>
          h('a', { class: 'link-card', href: '#/' + p.id }, h('strong', {}, icon(p.id), p.title), h('span', {}, p.desc)))),
        state.info ? h('p', { class: 'muted small' }, 'Files the dashboard makes (pulled modules, OpenAPI, architecture pages) are saved in ', h('code', { text: state.info.workFolder }), '. Nothing is overwritten.') : null));

    // The server keeps the last result for this dashboard run (doctor contacts Service Studio's MCP server);
    // "Check again" asks for a fresh one.
    const runDoctor = async fresh => {
      const data = await task(again, doctorOut, 'Checking your setup…', async () => need(await api('doctor' + (fresh ? '?fresh=1' : '')), [0, 1]));
      if (!data || !data.output || !data.output.checks) return;
      const label = { ok: 'OK', warn: 'Check', fail: 'Fix needed', skip: 'Skipped' };
      const checks = data.output.checks;
      const count = status => checks.filter(c => c.status === status).length;
      const open = checks.filter(c => c.status === 'warn' || c.status === 'fail');
      const names = open.map(c => window.omlCheckNames[c.name] || c.name).join(', ');
      const failed = checks.filter(c => c.status === 'fail').map(c => window.omlCheckNames[c.name] || c.name).join(', ');
      const summary = !data.output.ok ? `Something must be fixed: ${failed}.`
        : `Ready: ${count('ok')} OK` + (open.length ? `, ${open.length} to check (${names})` : '') + (count('skip') ? `, ${count('skip')} skipped` : '') + '.';
      const mcp = data.output.checks.find(c => c.name === 'studioMcp');
      if (mcp) studioStatus.replaceChildren(h('span', { class: 'badge ' + (mcp.status === 'ok' ? 'ok' : 'warn') }, h('span', { class: 'dot' }), mcp.status === 'ok' ? 'MCP server answers' : 'MCP server not reachable'),
        h('span', { class: 'muted' }, mcp.status === 'ok' ? mcp.detail : 'Start it in Service Studio: Edit > MCP Server > Start MCP server.'));
      add(doctorOut,
        h('div', { class: 'row' }, h('span', { class: 'badge ' + (!data.output.ok ? 'fail' : open.length ? 'warn' : 'ok') }, h('span', { class: 'dot' }), data.output.ok ? 'Ready to use' : 'Fix needed'),
          h('span', {}, summary), data.checkedAt ? h('span', { class: 'muted small' }, 'Checked at ' + data.checkedAt) : null),
        // All passed: one line, details on demand. Something to check or fix: the details are open.
        h('details', { class: 'doctor-details', open: open.length > 0 }, h('summary', {}, `Show the ${checks.length} checks`),
          h('ul', { class: 'checks', 'aria-label': 'Setup checks' }, checks.map(c => h('li', {},
            h('div', {}, h('span', { class: 'badge ' + c.status }, h('span', { class: 'dot' }), label[c.status] || c.status)),
            h('div', {}, h('div', { class: 'what' }, window.omlCheckNames[c.name] || c.name), h('div', { class: 'detail', text: c.detail }),
              c.fix ? h('div', { class: 'fix' + (c.status === 'fail' ? ' fail' : '') }, h('strong', {}, 'Fix: '), c.fix) : null))))),
        cli('oml doctor --text'));
    };
    const runStudio = async () => {
      const data = await task(studioAgain, studioOut, 'Asking Service Studio…', async () => api('studio'));
      if (!data) return;
      if (data.code !== 0) {
        add(studioOut, h('div', { class: 'message info' }, h('p', {}, 'Service Studio\'s MCP server does not answer. You only need it to pull, publish or refresh references.'),
          h('p', { class: 'small muted' }, 'To start it: in Service Studio, open Edit > MCP Server > Start MCP server.')), cli(data.cli));
        return;
      }
      const apps = (data.output && data.output.apps) || [];
      const open = apps.filter(a => a.isOpen);
      const out = h('div');
      add(studioOut, open.length === 0 ? h('p', { class: 'muted' }, 'Service Studio answers, but no module is open.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', {}, 'Open module'), h('th', {}, ''))),
          h('tbody', {}, open.map(a => {
            const b = h('button', { class: 'button small', type: 'button', 'aria-label': `Copy ${a.name} from Service Studio to a file` }, 'Copy to a file');
            b.addEventListener('click', async () => {
              const r = await task(b, out, `Saving ${a.name}… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: a.name }), [0, 1]));
              if (r) { setModule(r.path); workset.add(r.path, { kind: 'studio', module: a.name }); add(out, h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path }), ' ', h('a', { href: '#/explorer' }, 'Open it in the Module explorer')), pullWarning(r), cli(r.cli)); }
            });
            return h('tr', {}, h('td', {}, a.name), h('td', { class: 'num' }, b));
          })))), out, cli(data.cli));
    };
    again.addEventListener('click', () => runDoctor(true));
    studioAgain.addEventListener('click', runStudio);
    await runDoctor(false);
  });

  // 1b. Get modules: the working set, and every way to get a module into it.
  page('modules', 'Start', 'Get modules', 'Choose the modules you work on: copy them from Service Studio, list a folder, open a package, drop files or get them from LifeTime.', async main => {
    const setOut = h('div');
    const roleLabel = { change: 'Change', read: 'Read only' };
    const drawSet = () => {
      const list = workset.list();
      setOut.replaceChildren();
      add(setOut, list.length === 0 ? h('p', { class: 'muted' }, 'No modules yet. Get them below. Every page offers the modules you add here.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Module', 'Role', 'Where it came from', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, list.map(m => {
            const id = 'role-' + Math.random().toString(36).slice(2);
            const role = h('select', { id, onchange: e => workset.setRole(m.path, e.target.value) },
              Object.entries(roleLabel).map(([v, t]) => h('option', { value: v }, t)));
            role.value = m.role;
            return h('tr', {},
              h('td', {}, h('strong', {}, m.name), h('div', { class: 'tiny muted mono', text: m.path })),
              h('td', {}, h('label', { for: id, class: 'sr-only' }, 'Role of ' + m.name), role),
              h('td', { class: 'small' }, workset.sourceLine(m)),
              h('td', { class: 'num' }, h('div', { class: 'row' },
                h('a', { class: 'button small', href: '#/explorer?module=' + encodeURIComponent(m.path), 'aria-label': 'Explore ' + m.name }, 'Explore'),
                h('button', { class: 'button small', type: 'button', 'aria-label': 'Remove ' + m.name + ' from the working set', onclick: () => workset.remove(m.path) }, 'Remove'))));
          })))),
        list.length ? h('div', { class: 'row' }, h('a', { class: 'button small', href: '#/prompt' }, 'Write a task for an agent'),
          h('button', { class: 'button small', type: 'button', onclick: () => { if (confirm('Remove every module from the working set? The files stay.')) workset.clear(); } }, 'Clear the list')) : null,
        h('p', { class: 'hint' }, 'Change: the agent may change this module. Read only: the agent reads it as a producer, with --in. Removing a module here keeps its file.'));
    };
    onLeave(workset.onChange(drawSet));
    drawSet();

    // A list of modules with a checkbox each, and one button that adds the checked ones.
    function pickList(items, source, label) {
      const boxes = items.map(item => {
        const id = 'pick-' + Math.random().toString(36).slice(2);
        const usable = item.path && /\.oml$/i.test(item.path);
        return { item, box: h('input', { type: 'checkbox', id, checked: usable, disabled: !usable }), id, usable };
      });
      const addButton = h('button', { class: 'button small primary', type: 'button' }, label || 'Add to the working set');
      addButton.addEventListener('click', () => {
        const chosen = boxes.filter(b => b.box.checked);
        if (!chosen.length) { toast('Check at least one module.'); return; }
        chosen.forEach(b => workset.add(b.item.path, typeof source === 'function' ? source(b.item) : source));
        toast(`Added ${chosen.length} module${chosen.length > 1 ? 's' : ''}`);
      });
      return h('div', { class: 'stack' },
        h('ul', { class: 'checklist', 'aria-label': 'Modules' }, boxes.map(b => h('li', {},
          h('label', { class: 'check', for: b.id }, b.box, h('span', {}, h('strong', {}, b.item.name), ' ', h('span', { class: 'tiny muted' }, b.item.note || ''),
            !b.usable ? h('span', { class: 'tiny muted' }, ' (not a module file you can add)') : null))))),
        addButton);
    }

    // An .oap: `app list` on a click, then `app extract` into a new work folder on another click.
    function packageView(file, out) {
      const list = h('button', { class: 'button small', type: 'button', 'aria-label': 'Show the modules of ' + fileName(file) }, 'Show modules');
      list.addEventListener('click', async () => {
        const data = await task(list, out, `Reading ${fileName(file)}…`, async () => need(await api('modules/app', { path: file })));
        if (!data) return;
        const modules = (data.output && data.output.modules) || [];
        const apps = (data.output && data.output.applications) || [];
        const extract = h('button', { class: 'button small primary', type: 'button' }, 'Extract the modules');
        const extracted = h('div');
        extract.addEventListener('click', async () => {
          const r = await task(extract, extracted, 'Extracting…', async () => need(await api('modules/extract', { path: file })));
          if (!r) return;
          const files = ((r.output && r.output.extracted) || []).map(f => ({ name: f.name, path: f.path, note: bytes(f.size) }));
          add(extracted, h('p', { class: 'small' }, 'Saved in ', h('code', { text: r.folder })), pickList(files, { kind: 'package', file }), cli(r.cli));
        });
        add(out, h('p', {}, apps.map(a => a.name).join(', ') || fileName(file), ': ', `${modules.length} module${modules.length === 1 ? '' : 's'}.`),
          h('ul', { class: 'small' }, modules.map(m => h('li', {}, h('strong', {}, m.name), ` · ${m.kind} · ${bytes(m.size)}`))),
          extract, extracted, cli(data.cli));
      });
      return list;
    }

    let source = store.get('modules-source', 'studio');
    const tabs = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Where the modules come from' });
    const body = h('div', { class: 'stack' });
    const sources = [['studio', 'Service Studio'], ['folder', 'Folder'], ['drop', 'Drop files'], ['recent', 'Recent'], ['lifetime', 'LifeTime']];
    for (const [value, label] of sources) {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === source) }, label);
      b.addEventListener('click', () => { source = value; store.set('modules-source', value); tabs.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); draw(); });
      add(tabs, b);
    }
    add(main,
      h('section', { class: 'card', 'aria-labelledby': 'ws-title' }, h('h2', { id: 'ws-title' }, 'Working set'), setOut),
      h('section', { class: 'card', 'aria-labelledby': 'get-title' }, h('h2', { id: 'get-title' }, 'Get modules'), h('div', { class: 'field' }, tabs), body));

    function draw() {
      const out = h('div', { class: 'stack', 'aria-live': 'polite' });
      if (source === 'studio') {
        const show = h('button', { class: 'button primary', type: 'button' }, 'Show open modules');
        body.replaceChildren(h('p', { class: 'muted small' }, 'Copies the modules open in Service Studio to new files. The open modules do not change. Nothing is copied until you click.'),
          h('div', { class: 'row' }, show), h('p', { class: 'hint' }, CONNECT_NOTE), out);
        show.addEventListener('click', async () => {
          const data = await task(show, out, 'Asking Service Studio for its open modules…', async () => need(await api('studio')));
          if (!data) { out.prepend(h('div', { class: 'message info' }, 'Start Service Studio\'s MCP server first: in Service Studio, open Edit > MCP Server > Start MCP server.')); return; }
          const apps = (data.output && data.output.apps) || [];
          if (!apps.some(a => a.isOpen)) { add(out, h('p', { class: 'muted' }, 'Service Studio answers, but no module is open.'), cli(data.cli)); return; }
          const rows = apps.map(a => ({ a, box: h('input', { type: 'checkbox', id: 'ss-' + Math.random().toString(36).slice(2), checked: a.isOpen && !a.isReference, disabled: !a.isOpen }) }));
          const copyButton = h('button', { class: 'button primary', type: 'button' }, 'Copy the checked modules');
          const result = h('div', { class: 'stack' });
          copyButton.addEventListener('click', async () => {
            const chosen = rows.filter(r => r.box.checked).map(r => r.a.name);
            if (!chosen.length) { toast('Check at least one module.'); return; }
            result.replaceChildren();
            for (const name of chosen) {
              const line = h('div');
              add(result, line);
              const r = await task(copyButton, line, `Copying ${name}… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: name }), [0, 1]));
              if (!r) continue;
              workset.add(r.path, { kind: 'studio', module: name });
              add(line, h('div', { class: 'message' }, 'Copied ', h('strong', {}, name), ' to ', h('code', { text: r.path })), pullWarning(r), cli(r.cli));
            }
          });
          add(out, h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['', 'Module', 'In Service Studio'].map(t => h('th', {}, t)))),
            h('tbody', {}, rows.map(({ a, box }) => h('tr', {},
              h('td', {}, h('label', { for: box.id, class: 'sr-only' }, 'Copy ' + a.name), box),
              h('td', {}, h('strong', {}, a.name)),
              h('td', {}, a.isOpen ? h('span', { class: 'badge ok' }, 'Open') : h('span', { class: 'badge' }, 'Not open'), a.isReference ? h('span', { class: 'badge' }, 'Reference') : null)))))),
          h('p', { class: 'hint' }, 'Only open modules can be copied. Each copy goes to a new folder; the module in Service Studio stays as it is.'),
          copyButton, result, cli(data.cli));
        });
      } else if (source === 'folder') {
        const folder = h('input', { type: 'text', id: 'm-folder', class: 'code', value: store.get('modules-folder', ''), placeholder: 'C:\\Work\\Modules', spellcheck: 'false' });
        const list = h('button', { class: 'button primary', type: 'button' }, 'List files');
        const run = async () => {
          const path = folder.value.trim().replace(/^"(.*)"$/, '$1');
          if (!path) { folder.focus(); return; }
          store.set('modules-folder', path);
          const data = await task(list, out, 'Listing the folder…', () => api('modules/folder', { folder: path }));
          if (!data) return;
          const files = data.files || [];
          const omls = files.filter(f => f.kind === 'oml'), oaps = files.filter(f => f.kind === 'oap');
          add(out, files.length === 0 ? h('p', { class: 'muted' }, 'No .oml or .oap files in this folder or its subfolders.') : null,
            data.truncated ? h('p', { class: 'hint' }, 'Only the first 500 files are shown. Choose a smaller folder.') : null,
            data.stoppedEarly ? h('p', { class: 'hint', role: 'status' }, 'Stopped early: listing this folder took more than 10 seconds. The list may be incomplete. Choose a smaller folder.') : null,
            omls.length ? h('div', { class: 'stack' }, h('h3', {}, `Modules (${omls.length})`), pickList(omls.map(f => ({ name: f.name, path: f.path, note: `${bytes(f.bytes)} · ${new Date(f.modified).toLocaleString()}` })), { kind: 'folder', folder: data.folder })) : null,
            oaps.length ? h('div', { class: 'stack' }, h('h3', {}, `Application packages (${oaps.length})`),
              oaps.map(f => { const o = h('div', { class: 'stack' }); return h('div', { class: 'panel' }, h('div', { class: 'row' }, h('strong', {}, f.name), h('span', { class: 'tiny muted' }, bytes(f.bytes)), packageView(f.path, o)), o); })) : null,
            cli(data.cli));
        };
        list.addEventListener('click', run);
        folder.addEventListener('keydown', e => { if (e.key === 'Enter') run(); });
        body.replaceChildren(h('div', { class: 'field' }, h('label', { for: 'm-folder' }, 'Folder with .oml or .oap files'), h('div', { class: 'row' }, h('div', { class: 'grow' }, folder), list),
          h('p', { class: 'hint' }, 'Subfolders are listed too. A package (.oap) shows its modules; extracting them makes new files and never changes the package.')), out);
      } else if (source === 'drop') {
        body.replaceChildren(h('p', { class: 'muted small' }, 'Dropped files are copied to a new folder in the work folder and stay there.'),
          dropZone('.oml,.oap', 'Drop .oml or .oap files', async files => {
            const data = await task(null, out, 'Uploading…', async () => api('modules/upload', { files: await readFiles(files) }));
            if (!data) return;
            const omls = (data.files || []).filter(f => f.kind === 'oml');
            omls.forEach(f => workset.add(f.path, { kind: 'drop', file: f.name }));
            add(out, h('p', { class: 'small' }, 'Saved in ', h('code', { text: data.folder })),
              omls.length ? h('div', { class: 'message' }, `Added ${omls.length} module${omls.length > 1 ? 's' : ''} to the working set: ${omls.map(f => f.name).join(', ')}.`) : null,
              (data.files || []).filter(f => f.kind === 'oap').map(f => { const o = h('div', { class: 'stack' }); return h('div', { class: 'panel' }, h('div', { class: 'row' }, h('strong', {}, f.name), packageView(f.path, o)), o); }));
          }), out);
      } else if (source === 'recent') {
        const inSet = new Set(workset.list().map(m => m.path.toLowerCase()));
        const sourcesSeen = store.get('sources', {});
        const recent = store.get('recent', []).filter(p => !inSet.has(p.toLowerCase()));
        body.replaceChildren(recent.length === 0 ? h('p', { class: 'muted' }, 'No other recent modules. Modules you open or add appear here.') :
          pickList(recent.map(p => ({ name: fileName(p), path: p, note: p })), item => sourcesSeen[item.path.toLowerCase()] || { kind: 'path' }));
      } else {
        const lt = state.info && state.info.lifeTime;
        if (!lt || !lt.configured) { body.replaceChildren(h('p', {}, 'LifeTime is not connected. ', h('a', { href: '#/lifetime' }, 'See how to connect it'), '.')); return; }
        const env = h('select', { id: 'm-lt-env' });
        const listApps = h('button', { class: 'button primary', type: 'button' }, 'List applications');
        body.replaceChildren(h('p', { class: 'muted small' }, 'Downloads the package of an application as it runs in one environment (read only), then extracts its modules into the work folder.'),
          h('div', { class: 'field' }, h('label', { for: 'm-lt-env' }, 'Environment'), h('div', { class: 'row' }, env, listApps)), out);
        api('lt/environments').then(r => (Array.isArray(r.output) ? r.output : []).forEach(e => add(env, h('option', { value: e.Name }, e.Name)))).catch(e => out.replaceChildren(errorBox(e)));
        listApps.addEventListener('click', async () => {
          const environment = env.value;
          if (!environment) { env.focus(); return; }
          const data = await task(listApps, out, 'Reading applications… LifeTime can take some seconds.', async () => need(await api('lt/apps?env=' + encodeURIComponent(environment))));
          if (!data) return;
          const apps = Array.isArray(data.output) ? data.output : [];
          add(out, apps.length === 0 ? h('p', { class: 'muted' }, 'No applications run in this environment.') :
            h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Application', 'Version', ''].map(t => h('th', {}, t)))),
              h('tbody', {}, apps.map(a => {
                const result = h('div', { class: 'stack' });
                const get = h('button', { class: 'button small', type: 'button', 'aria-label': 'Download and extract ' + a.name }, 'Get modules');
                get.addEventListener('click', () => ltDownload(a.name, environment, true, get, result, files =>
                  pickList(files.map(f => ({ name: f.name, path: f.path, note: bytes(f.size) })), { kind: 'lifetime', app: a.name, env: environment })));
                const e = (a.environments || [])[0] || {};
                return [h('tr', {}, h('td', {}, h('strong', {}, a.name)), h('td', { class: 'mono small' }, e.version || '—'), h('td', { class: 'num' }, get)),
                  h('tr', {}, h('td', { colspan: '3' }, result))];
              })))), cli(data.cli));
        });
      }
    }
    draw();
  });

  // `oml lt download`: GET only. The .oap stays in the work folder; with extract, so do its modules.
  async function ltDownload(app, env, extract, button, out, showFiles) {
    const data = await task(button, out, `Downloading ${app} from ${env}…`, () => api('lt/download', { app, env, extract }));
    if (!data) return null;
    const o = data.output || {};
    const failed = data.code !== 0;
    // The package can arrive while the extraction fails: then the .oap link stays, with the error under it.
    add(out, data.download ? h('div', { class: 'row' }, h('span', { class: 'badge ok' }, `${o.application} · ${bytes(o.bytes)}`), downloadLink(data.download, 'Download .oap', fileName(o.file))) : null,
      o.file && data.download ? h('p', { class: 'hint' }, 'Saved in ', h('code', { text: o.file })) : null,
      failed ? errorBox(new ApiError(problem(data), data)) : null,
      !failed && extract && showFiles ? showFiles(o.extracted || []) : null, failed ? null : cli(data.cli));
    return failed ? null : data;
  }

  // 2. Module explorer
  page('explorer', 'Module', 'Module explorer', 'Browse the entities, actions, screens and blocks of a module. Select one to read what it does.', async main => {
    const findOut = h('div');
    const body = h('div', { class: 'stack' });
    const picker = modulePicker(async (path, button, status) => {
      const data = await task(button, status, 'Reading the module… The first time can take some seconds.', () => inspect(path, true));
      if (data) showModule(path, data);
    });
    const findName = h('input', { type: 'search', id: 'find-name', class: 'code', placeholder: 'Customer_Validate or Order*', spellcheck: 'false' });
    const findIn = h('input', { type: 'text', id: 'find-in', class: 'code', placeholder: 'C:\\Work\\Producers', value: store.get('producers', ''), spellcheck: 'false' });
    const findButton = h('button', { class: 'button', type: 'button' }, 'Find');
    const runFind = async () => {
      const name = findName.value.trim();
      if (!name) { findName.focus(); return; }
      store.set('producers', findIn.value.trim());
      const folders = findIn.value.split(';').map(s => s.trim()).filter(Boolean);
      const data = await task(findButton, findOut, 'Searching…', async () => need(await api('find', { name, path: state.module || undefined, in: folders })));
      if (!data) return;
      const matches = (data.output && data.output.matches) || [];
      add(findOut, matches.length === 0 ? h('p', { class: 'muted' }, 'Nothing found with that name.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Name', 'Kind', 'Where', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, matches.map(m => h('tr', {},
            h('td', {}, h('strong', {}, m.qualified || m.name), m.inputs ? h('div', { class: 'tiny muted' }, 'Inputs: ' + m.inputs.map(i => `${i.name} (${i.type})`).join(', ')) : null),
            h('td', {}, m.kind), h('td', { class: 'small' }, m.source, m.file && m.source !== 'this module' ? h('div', { class: 'tiny muted', text: fileName(m.file) }) : null),
            h('td', { class: 'num' }, copyButton(m.qualified || m.name, 'Copy name', 'Name', 'Copy the name ' + (m.qualified || m.name)))))))),
        data.output && data.output.hint ? h('p', { class: 'hint' }, data.output.hint) : null, cli(data.cli));
    };
    findButton.addEventListener('click', runFind);
    findName.addEventListener('keydown', e => { if (e.key === 'Enter') runFind(); });

    add(main, picker.el, body,
      h('section', { class: 'card', 'aria-labelledby': 'find-title' },
        h('h2', { id: 'find-title' }, 'Find an element'), h('p', { class: 'muted small' }, 'Which module owns a name: this module, its references, or producer modules in a folder.'),
        h('div', { class: 'grid-2' },
          h('div', { class: 'field' }, h('label', { for: 'find-name' }, 'Name or pattern'), findName),
          h('div', { class: 'field' }, h('label', { for: 'find-in' }, 'Producer folders (optional)'), findIn, h('p', { class: 'hint' }, 'Separate several folders with ;'))),
        findButton, findOut),
      extensionCard(), translationsCard());

    // `oml extension inspect`: the actions, entities and structures of an extension (.xif).
    function extensionCard() {
      const path = h('input', { type: 'text', id: 'xif-path', class: 'code', value: store.get('xif-path', ''), placeholder: 'C:\\Work\\PdfTools.xif', spellcheck: 'false' });
      const run = h('button', { class: 'button', type: 'button' }, 'Inspect');
      const out = h('div');
      const go = async () => {
        const file = path.value.trim().replace(/^"(.*)"$/, '$1');
        if (!file) { path.focus(); return; }
        store.set('xif-path', file);
        const data = await task(run, out, 'Reading the extension…', async () => need(await api('extension/inspect', { path: file })));
        if (!data) return;
        const x = data.output || {};
        const list = (title, items, line) => (items || []).length ? h('div', {}, h('h3', {}, `${title} (${items.length})`), h('ul', { class: 'small' }, items.map(i => h('li', {}, line(i))))) : null;
        const params = ps => (ps || []).map(p => `${p.name}: ${p.type}${p.direction ? ' (' + p.direction + ')' : ''}`).join(', ');
        add(out, h('p', {}, h('strong', {}, x.name || fileName(file)), x.key ? h('span', { class: 'tiny muted' }, ' · ' + x.key) : null),
          list('Actions', x.actions, a => [h('strong', {}, a.name), ' ', h('span', { class: 'muted' }, params(a.parameters || a.inputs))]),
          list('Entities', x.entities, e => e.name),
          list('Structures', x.structures, st => st.name),
          h('details', {}, h('summary', {}, 'Show the JSON'), colorCode(JSON.stringify(x, null, 2), 'json')), cli(data.cli));
      };
      run.addEventListener('click', go);
      path.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
      return h('section', { class: 'card', 'aria-labelledby': 'xif-title' }, h('h2', { id: 'xif-title' }, 'Inspect an extension'),
        h('p', { class: 'muted small' }, 'The actions with their parameters, entities and structures of an Integration Studio extension (.xif).'),
        h('div', { class: 'field' }, h('label', { for: 'xif-path' }, 'Extension file (.xif)'), h('div', { class: 'row' }, h('div', { class: 'grow' }, path), run)), out);
    }

    // `oml translations export`: the module's translations as Excel or ResX, into a new work folder.
    function translationsCard() {
      const format = h('select', { id: 'tr-format' }, h('option', { value: 'Excel' }, 'Excel (.xlsx)'), h('option', { value: 'ResX' }, 'ResX (a folder)'));
      const run = h('button', { class: 'button', type: 'button' }, 'Export translations');
      const out = h('div');
      run.addEventListener('click', async () => {
        if (!state.module) { out.replaceChildren(h('div', { class: 'message error', role: 'alert' }, 'Open a module above first.')); return; }
        const data = await task(run, out, 'Exporting translations…', async () => need(await api('translations/export', { path: state.module, format: format.value })));
        if (!data) return;
        add(out, h('p', { class: 'small' }, 'Saved in ', h('code', { text: data.folder })),
          h('div', { class: 'row' }, (data.files || []).map(f => downloadLink(f.download, 'Download', f.name))), cli(data.cli));
      });
      return h('section', { class: 'card', 'aria-labelledby': 'tr-title' }, h('h2', { id: 'tr-title' }, 'Export translations'),
        h('p', { class: 'muted small' }, 'The texts of the open module in every language, to give to translators. Some Service Studio versions refuse file export; then the error says so.'),
        h('div', { class: 'field' }, h('label', { for: 'tr-format' }, 'Format'), h('div', { class: 'row' }, format, run)), out);
    }

    function showModule(path, data) {
      const out = data.output || {};
      const groups = [
        ['entities', 'Entities'], ['staticEntities', 'Static entities'], ['structures', 'Structures'],
        ['serverActions', 'Server actions'], ['serviceActions', 'Service actions'], ['clientActions', 'Client actions'],
        ['screens', 'Screens'], ['blocks', 'Blocks'], ['restServices', 'Exposed REST APIs'], ['restClients', 'Consumed REST APIs'],
        ['timers', 'Timers'], ['processes', 'Processes'], ['roles', 'Roles'], ['siteProperties', 'Site properties'], ['exceptions', 'Exceptions'],
      ];
      const detail = h('div', { class: 'card' }, h('div', { class: 'empty' }, 'Select an element on the left to read what it does.'));
      const filter = h('input', { type: 'search', placeholder: 'Filter names', 'aria-label': 'Filter element names' });
      const tree = h('div');
      let current = null;
      const items = [];
      const byName = new Map();
      for (const [key, title] of groups) {
        let list = out[key] || [];
        if (key === 'restServices' || key === 'restClients') list = list.flatMap(api => [{ name: api.name }, ...(api.methods || []).map(m => ({ name: m.name, sub: `${m.method} ${m.url}` }))]);
        if (!list.length) continue;
        const ul = h('ul');
        for (const item of list) {
          const changed = item.lastModifiedBy ? `${item.lastModifiedBy}, ${String(item.lastModifiedDate || '').slice(0, 10)}` : '';
          const b = h('button', { type: 'button', title: item.sub || '' }, item.name, item.sub ? h('span', { class: 'tiny muted' }, '  ' + item.sub) : null,
            changed ? h('span', { class: 'tiny muted' }, ' · ' + changed) : null);
          b.addEventListener('click', () => { if (current) current.removeAttribute('aria-current'); current = b; b.setAttribute('aria-current', 'true'); open(item.name, key, item); });
          const li = h('li', {}, b);
          items.push([item.name.toLowerCase(), li]);
          byName.set(item.name, () => b.click());
          add(ul, li);
        }
        add(tree, h('details', { open: list.length <= 40 }, h('summary', {}, title, h('span', { class: 'count' }, String(list.length))), ul));
      }
      filter.addEventListener('input', () => {
        const q = filter.value.trim().toLowerCase();
        for (const [name, li] of items) li.hidden = q && !name.includes(q);
        if (q) tree.querySelectorAll('details').forEach(d => { d.open = true; });
      });
      const reload = h('button', { class: 'button small', type: 'button' }, 'Reload');
      reload.addEventListener('click', async () => {
        const fresh = await task(reload, detail, 'Reading the module again…', () => inspect(path, true));
        if (fresh) showModule(path, fresh);
      });
      const history = h('input', { type: 'checkbox', id: 'x-history', checked: store.get('history', false) });
      history.addEventListener('change', async () => {
        store.set('history', history.checked);
        const fresh = await task(reload, detail, 'Reading the module again…', () => inspect(path, true));
        if (fresh) showModule(path, fresh);
      });
      const validateOut = h('div');
      const validate = h('button', { class: 'button small', type: 'button' }, 'Validate');
      validate.addEventListener('click', () => validateModule(path, validate, validateOut));
      body.replaceChildren(
        h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, out.name || fileName(path)), h('p', { class: 'muted small' }, out.kind ? `${out.kind} module · ` : '', fileName(path))),
          h('div', { class: 'row' }, h('label', { class: 'check small', for: 'x-history' }, history, 'Show change history'), validate, reload)),
        validateOut,
        h('div', { class: 'explorer' }, h('div', { class: 'panel tree' }, h('div', { class: 'field' }, filter), items.length ? tree : h('p', { class: 'muted' }, 'This module has no own elements yet.')), detail),
        cli(data.cli),
        textSearch(path, (name, kind) => open(name, null, { kind })));
      // #/explorer?module=<path>&element=<Name>&tab=paths opens that element and tab: a link a person can keep.
      const linked = hashParams();
      if (linked.get('tab')) store.set('explorer-tab', linked.get('tab'));
      if (byName.has(linked.get('element'))) byName.get(linked.get('element'))();

      // One element: Describe, its object Paths (tree), its Style sheet (describe --css), or Test screens (test scaffold).
      function open(name, key, item) {
        const tabs = [['describe', 'Describe'], ['paths', 'Paths']];
        const kind = (item && item.kind) || '';
        if (key === 'screens' || key === 'blocks' || /^(Screen|Block|WebBlock|WebScreen|Email|Theme)$/.test(kind)) tabs.push(['css', 'Style sheet']);
        if (key === 'serverActions' && item && item.public) tabs.push(['tests', 'Test screens']);
        let tab = store.get('explorer-tab', 'describe');
        if (!tabs.some(t => t[0] === tab)) tab = 'describe';
        const pane = h('div', { class: 'tab-pane' });
        const draw = () => ({ describe, paths: pathsTab, css: cssTab, tests: testsTab })[tab](name, pane);
        detail.replaceChildren(h('div', { class: 'card-head' }, h('h2', {}, name), copyButton(name, 'Copy name', 'Name')),
          segmented('Show', tabs, tab, v => { tab = v; store.set('explorer-tab', v); draw(); }), pane);
        draw();
      }

      async function describe(name, pane) {
        const result = await task(null, pane, `Reading ${name}…`, async () => need(await api('describe', { path, name, history: store.get('history', false) || undefined,
          in: (store.get('producers', '') || '').split(';').map(s => s.trim()).filter(Boolean) })));
        if (!result) return;
        const d = Array.isArray(result.output) ? result.output[0] : result.output || {};
        const sig = d.signature || {};
        add(pane,
          h('div', { class: 'row small' }, h('strong', {}, d.element || name), d.kind ? h('span', { class: 'badge accent' }, d.kind) : null, d.public === true ? h('span', { class: 'badge' }, 'Public') : null),
          (sig.inputs || sig.outputs) ? h('div', { class: 'signature' },
            (sig.inputs || []).map(p => h('span', { class: 'chip' }, h('span', { class: 'muted' }, 'in'), `${p.name}: ${p.type}`)),
            (sig.outputs || []).map(p => h('span', { class: 'chip' }, h('span', { class: 'muted' }, 'out'), `${p.name}: ${p.type}`))) : null,
          d.text ? colorCode(d.text, 'describe') : colorCode(JSON.stringify(d, null, 2), 'json'),
          cli(result.cli));
      }

      async function pathsTab(name, pane) {
        const depth = h('select', { id: 'x-depth' }, [1, 2, 3, 4, 6].map(n => h('option', { value: n }, String(n))));
        depth.value = String(store.get('tree-depth', 2));
        const out = h('div');
        const load = async () => {
          store.set('tree-depth', Number(depth.value));
          const data = await task(depth, out, `Reading the parts of ${name}…`, async () => need(await api('tree', { path, name, depth: Number(depth.value) })));
          if (data) add(out, h('ul', { class: 'ptree', 'aria-label': 'Parts of ' + name }, treeNode(data.output)),
            data.output.truncated ? h('p', { class: 'hint' }, String(data.output.truncated)) : null, cli(data.cli));
        };
        depth.addEventListener('change', load);
        pane.replaceChildren(h('p', { class: 'hint' }, 'Exact paths for describe, tree and edits (for example the at path of replaceWidgets). Copy one into a spec.'),
          h('div', { class: 'row' }, h('label', { for: 'x-depth' }, 'Levels'), depth), out);
        await load();
      }
      function treeNode(node) {
        const children = h('ul');
        const more = node.more ? h('button', { class: 'button small', type: 'button', 'aria-label': `Show the ${node.more} parts under ${node.path}` }, `Show ${node.more} more`) : null;
        if (more) more.addEventListener('click', async () => {
          const data = await task(more, children, 'Reading…', async () => need(await api('tree', { path, name: node.path, depth: 2 })));
          if (data) { more.remove(); add(children, (data.output.children || []).map(treeNode)); }
        });
        add(children, (node.children || []).map(treeNode));
        return h('li', {}, h('div', { class: 'row' }, h('span', { class: 'badge' }, node.kind || node.type), node.name ? h('strong', { class: 'small' }, node.name) : null,
          node.text ? h('code', { class: 'tiny', text: node.text.length > 60 ? node.text.slice(0, 60) + '…' : node.text }) : null,
          h('code', { class: 'tiny muted', text: node.path }), copyButton(node.path, 'Copy path', 'Path'), more), children);
      }

      async function cssTab(name, pane) {
        const data = await task(null, pane, `Reading the style sheet of ${name}…`, async () => need(await api('describe', { path, name, css: true })));
        if (!data) return;
        const d = Array.isArray(data.output) ? data.output[0] : data.output || {};
        const css = d.styleSheet || '';
        add(pane, h('div', { class: 'card-head' }, h('p', { class: 'hint' }, 'The whole style sheet. A styleSheet in a spec replaces all of it, so start from this text.'), css ? copyButton(css, 'Copy CSS', 'CSS') : null),
          css ? h('pre', { class: 'code-view' }, h('code', { text: css })) : h('p', { class: 'muted' }, 'This element has no style sheet.'), cli(data.cli));
      }

      function testsTab(name, pane) {
        const bdd = pathField('BDDFramework module (.oml)', store.get('bdd', ''), 'C:\\Work\\BDDFramework.oml',
          'Install BDDFramework Client Side from Forge, open it in Service Studio, and save a copy: oml pull BDDFramework --out BDDFramework.oml.');
        const make = h('button', { class: 'button primary', type: 'button', 'aria-label': 'Make the test spec for ' + name }, 'Make the test spec');
        const out = h('div');
        make.addEventListener('click', async () => {
          if (!bdd.value()) { bdd.input.focus(); return; }
          store.set('bdd', bdd.value());
          const data = await task(make, out, 'Writing the test spec…', async () => need(await api('test/scaffold', { path, actions: [name], bdd: bdd.value() })));
          if (!data) return;
          const r = data.output || {};
          const next = (r.next || []).join('\n');
          add(out, h('div', { class: 'message' }, 'Spec saved to ', h('code', { text: data.path }), `. Replace its ${r.todo || 0} TODO values before you trust a test.`),
            h('ul', { class: 'checklist' }, (r.screens || []).map(x => h('li', {}, h('strong', {}, x.screen), ': ', (x.scenarios || []).join(', ')))),
            (r.notes || []).length ? h('details', {}, h('summary', {}, 'Notes'), h('ul', { class: 'checklist' }, r.notes.map(n => h('li', {}, n)))) : null,
            next ? h('div', {}, h('div', { class: 'card-head' }, h('h3', {}, 'Next commands'), copyButton(next, 'Copy the next commands', 'Commands')), h('pre', { class: 'code-view' }, h('code', { text: next }))) : null,
            data.spec ? h('details', {}, h('summary', {}, 'Show the spec'), colorCode(data.spec, 'json')) : null,
            cli(data.cli));
        });
        pane.replaceChildren(h('p', { class: 'hint' }, 'A spec for BDD test screens in a separate test module. It never changes this module. Apply it only to the test module.'), bdd.el, make, out);
      }
    }

    // oml validate --warnings, each message with a link to oml warn in Docs.
    async function validateModule(path, button, out) {
      const data = await task(button, out, 'Validating…', async () => need(await api('validate', { path }), [0, 1]));
      if (!data) return;
      const r = data.output || {};
      const list = [...(r.errors || []).map(x => ({ ...x, error: true })), ...(r.warnings || [])];
      add(out, h('section', { class: 'card', 'aria-label': 'Validation' },
        h('div', { class: 'card-head' }, h('h3', {}, 'Validation'), h('div', { class: 'row' }, h('span', { class: 'badge ' + (r.errorCount ? 'fail' : 'ok') }, `${r.errorCount || 0} errors`),
          h('span', { class: 'badge ' + (r.warningCount ? 'warn' : 'ok') }, `${r.warningCount || 0} warnings`))),
        list.length ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Message', 'Where', 'Fix', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, list.map(x => {
            const sample = (x.samples || [])[0] || x;
            return h('tr', {},
              h('td', {}, h('span', { class: 'badge ' + (x.error ? 'fail' : 'warn') }, x.id || (x.error ? 'Error' : 'Warning')), x.count > 1 ? h('span', { class: 'tiny muted' }, ` ×${x.count}`) : null,
                h('div', { class: 'small' }, sample.message || '', sample.detail ? ': ' + sample.detail : '')),
              h('td', { class: 'mono tiny' }, sample.owner || ''), h('td', { class: 'small' }, x.hint || ''),
              h('td', { class: 'num' }, x.id && x.hint ? h('a', { class: 'button small', href: '#/docs?open=' + encodeURIComponent('oml warn ' + x.id), 'aria-label': 'Explain ' + x.id }, 'Explain') : null));
          })))) : h('p', { class: 'muted' }, 'No errors and no warnings.'),
        cli(data.cli)));
    }

    // oml find <module> --text: the module's own elements ranked by name, description and the text inside them.
    function textSearch(path, read) {
      const q = h('input', { type: 'search', id: 'x-text', class: 'code', placeholder: 'GetOrders or status closed', spellcheck: 'false' });
      const go = h('button', { class: 'button', type: 'button' }, 'Search');
      const out = h('div');
      const run = async () => {
        if (!q.value.trim()) { q.focus(); return; }
        const data = await task(go, out, 'Searching the module…', async () => need(await api('find/text', { path, text: q.value.trim(), top: 15 })));
        if (!data) return;
        const hits = (data.output && data.output.hits) || [];
        add(out, hits.length === 0 ? h('p', { class: 'muted' }, 'Nothing in this module matches.') :
          h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Element', 'Why', 'Score', ''].map((t, i) => h('th', { class: i === 2 ? 'num' : null }, t)))),
            h('tbody', {}, hits.map(x => h('tr', {},
              h('td', {}, h('strong', {}, x.name || x.kind), h('div', { class: 'tiny muted' }, x.kind), h('code', { class: 'tiny', text: x.path })),
              h('td', { class: 'small' }, x.why), h('td', { class: 'num small' }, String(x.score)),
              h('td', { class: 'num' }, h('div', { class: 'row' }, h('button', { class: 'button small', type: 'button', 'aria-label': 'Read ' + x.path, onclick: () => read(x.path, x.kind) }, 'Read'),
                copyButton(x.path, 'Copy path', 'Path')))))))),
          data.output && data.output.searched ? h('p', { class: 'hint' }, `${data.output.searched} elements searched.`) : null, cli(data.cli));
      };
      go.addEventListener('click', run);
      q.addEventListener('keydown', e => { if (e.key === 'Enter') run(); });
      return h('section', { class: 'card', 'aria-labelledby': 'x-text-title' }, h('h2', { id: 'x-text-title' }, 'Search inside the module'),
        h('p', { class: 'muted small' }, 'Names, descriptions, expressions, SQL, labels and scripts of this module, best match first.'),
        h('div', { class: 'field' }, h('label', { for: 'x-text' }, 'Words'), h('div', { class: 'row' }, h('div', { class: 'grow' }, q), go)), out);
    }

    if (state.module && state.inspect[state.module]) showModule(state.module, state.inspect[state.module]);
    else if (state.module) picker.go();
  });

  // 3. Preview gallery
  page('previews', 'Module', 'Preview gallery', 'See every screen and block of a module, in light and dark, at desktop and phone width, with the automatic UI checks.', async main => {
    const body = h('div');
    const picker = modulePicker(async (path, button, status) => {
      const data = await task(button, status, 'Reading the module…', () => inspect(path));
      if (data) show(path, data);
    });
    add(main, picker.el, body);
    let theme = store.get('preview-theme', 'light'), width = store.get('preview-width', 1200);

    // Inputs (--arg), --height and interaction steps (--click, --type, --wait, --eval) of one card.
    function previewOptions(el) {
      const id = 'pv-' + el.name;
      const inputs = el.inputs.map(name => [name, h('input', { type: 'text', id: `${id}-arg-${name}`, class: 'code', placeholder: 'Default value', spellcheck: 'false' })]);
      const height = h('input', { type: 'number', id: id + '-height', min: '100', max: '10000', placeholder: '900' });
      const steps = h('ol', { class: 'steps' });
      const addStep = () => {
        const n = steps.children.length + 1;
        const kind = h('select', { 'aria-label': `Step ${n} kind` }, [['click', 'Click'], ['type', 'Type'], ['wait', 'Wait (ms)'], ['eval', 'Run JavaScript']].map(([v, t]) => h('option', { value: v }, t)));
        const value = h('input', { type: 'text', class: 'code', 'aria-label': `Step ${n} value`, placeholder: '#SaveButton', spellcheck: 'false' });
        const hints = { click: '#SaveButton', type: '#Name=Ann', wait: '300', eval: "document.title" };
        kind.addEventListener('change', () => { value.placeholder = hints[kind.value]; });
        const li = h('li', { class: 'row' }, kind, h('div', { class: 'grow' }, value),
          h('button', { class: 'button small', type: 'button', 'aria-label': `Remove step ${n}`, onclick: () => li.remove() }, 'Remove'));
        li.read = () => value.value.trim() ? { kind: kind.value, value: kind.value === 'wait' ? Number(value.value) : value.value.trim() } : null;
        add(steps, li);
        value.focus();
      };
      const el2 = h('details', { class: 'pv-options' }, h('summary', {}, 'Inputs and interaction steps'),
        inputs.length ? h('div', { class: 'grid-2' }, inputs.map(([name, input]) => h('div', { class: 'field' }, h('label', { for: input.id }, name), input))) : h('p', { class: 'hint' }, 'No inputs.'),
        h('p', { class: 'hint' }, 'Use OutSystems literals: 3, True, "Ann". Other text is used as plain text.'),
        h('div', { class: 'field' }, h('label', { for: height.id }, 'Screenshot height (px)'), height),
        h('p', { class: 'small' }, h('strong', {}, 'Steps'), ' run in order after the page loaded, before the screenshot. They need Edge or Chrome.'), steps,
        h('button', { class: 'button small', type: 'button', 'aria-label': 'Add a step for ' + el.name, onclick: addStep }, 'Add a step'));
      return {
        el: el2,
        body: () => ({
          args: inputs.filter(([, i]) => i.value.trim()).map(([name, i]) => ({ name, value: i.value.trim() })),
          height: height.value ? Number(height.value) : undefined,
          steps: [...steps.children].map(li => li.read()).filter(Boolean),
        }),
      };
    }
    // What the page did in the browser: steps, console output, blocked requests.
    function pageReport(report) {
      const list = (title, items, row) => items && items.length ? h('div', {}, h('p', { class: 'small' }, h('strong', {}, title)), h('ul', { class: 'checklist' }, items.map(row))) : null;
      return h('div', {},
        list('Steps', report.steps, x => h('li', {}, h('span', { class: 'badge ' + (x.ok ? 'ok' : 'fail') }, x.ok ? 'OK' : 'Failed'), h('span', {}, h('code', { text: x.step }),
          x.result !== undefined ? ' → ' + x.result : '', x.error ? h('span', { class: 'detail' }, ' — ' + x.error) : null))),
        list('Console', report.console, x => h('li', {}, h('span', { class: 'badge' + (x.level === 'error' ? ' fail' : x.level === 'warn' ? ' warn' : '') }, x.level), h('code', { text: x.text }))),
        list('Blocked (no network in the preview)', report.blocked, x => h('li', {}, h('code', { text: typeof x === 'string' ? x : JSON.stringify(x) }))),
        list('JavaScript errors', report.scriptErrors, x => h('li', {}, h('span', { class: 'badge fail' }, x.node), h('span', {}, x.message))),
        report.scriptsRan === false ? h('p', { class: 'hint' }, 'The page did not finish its scripts, so these lists are unknown.') : null);
    }

    function show(path, data) {
      const out = data.output || {};
      const elements = [...(out.screens || []).map(s => ({ name: s.name, kind: 'Screen', inputs: s.inputs || [] })), ...(out.blocks || []).map(b => ({ name: b.name, kind: 'Block', inputs: b.inputs || [] }))];
      if (!elements.length) { body.replaceChildren(h('div', { class: 'empty' }, 'This module has no screens or blocks.')); return; }
      const cards = [];
      const renderAll = h('button', { class: 'button primary', type: 'button' }, 'Render all');
      const gallery = h('div', { class: 'gallery' });
      const browserNote = state.info && !state.info.browser ? h('p', { class: 'hint' }, 'Screenshots need Edge or Chrome. The live previews work without them.') : null;
      for (const el of elements) {
        const out = h('div');
        const render = h('button', { class: 'button small', type: 'button', 'aria-label': 'Render ' + el.name }, 'Render');
        const shot = h('button', { class: 'button small', type: 'button', disabled: state.info && !state.info.browser, 'aria-label': 'Screenshot of ' + el.name }, 'Screenshot');
        const options = previewOptions(el);
        const go = async screenshot => {
          const w = width, t = theme;
          const r = await task(screenshot ? shot : render, out, screenshot ? 'Taking a screenshot… about 5 seconds.' : 'Rendering…', async () =>
            need(await api('preview', { path, element: el.name, theme: t, width: w, screenshot, ...options.body() })));
          if (!r) return;
          const report = r.output || {};
          const frameHeight = w <= 500 ? 760 : 640;
          let view;
          if (r.png) {
            const url = blobUrl(r.png, 'image/png');
            onLeave(() => URL.revokeObjectURL(url));
            view = viewer({ src: url, title: `Screenshot of ${el.name}, ${t} theme, ${w} px wide` });
          } else view = viewer({ view: r.view, title: `Preview of ${el.name}, ${t} theme, ${w} px wide`, width: w, height: frameHeight, fullHeight: true });
          const failed = (report.checklist || []).filter(c => !c.ok);
          add(out, view,
            r.png ? h('button', { class: 'button small', type: 'button', 'aria-label': `Download the PNG of ${el.name}`, onclick: () => download(`${el.name}-${t}-${w}.png`, r.png, 'image/png', true) }, icon('download'), 'Download PNG') : null,
            h('div', { class: 'row small' }, failed.length ? h('span', { class: 'badge warn' }, `${failed.length} of ${report.checklist.length} checks need work`) : h('span', { class: 'badge ok' }, 'All UI checks pass'),
              (report.scriptErrors || []).length ? h('span', { class: 'badge fail' }, `${report.scriptErrors.length} JavaScript error(s)`) : null,
              report.horizontalOverflow ? h('span', { class: 'badge fail' }, 'Wider than the frame') : null,
              (report.unsupported || []).length ? h('span', { class: 'badge' }, 'Not drawn: ' + report.unsupported.map(u => `${u.kind} ×${u.count}`).join(', ')) : null),
            h('ul', { class: 'checklist', 'aria-label': 'UI checks' }, (report.checklist || []).map(c => h('li', {},
              h('span', { class: 'badge ' + (c.ok ? 'ok' : 'warn') }, c.ok ? 'OK' : 'Check'), h('span', {}, c.name, c.detail ? h('span', { class: 'detail' }, ' — ' + c.detail) : null)))),
            pageReport(report),
            cli(r.cli));
        };
        render.addEventListener('click', () => go(false));
        shot.addEventListener('click', () => go(true));
        cards.push(() => go(false));
        add(gallery, h('section', { class: 'card', 'aria-label': el.name }, h('div', { class: 'card-head' },
          h('div', {}, h('h3', {}, el.name), h('span', { class: 'badge' }, el.kind)), h('div', { class: 'row' }, render, shot)), options.el, out));
      }
      // The first ones render at once and again when the theme or width changes (a preview takes well under a
      // second); the rest wait for a click. A newer round stops an older one.
      let round = 0;
      const auto = async () => { const mine = ++round; for (const go of cards.slice(0, 12)) { if (!gallery.isConnected || mine !== round) return; await go(); } };
      // Render all: one at a time with "n of N". Stop lets the current one finish (a running command cannot be cut off).
      const progress = h('span', { class: 'small muted', 'aria-live': 'polite' });
      const stop = h('button', { class: 'button', type: 'button', hidden: true }, 'Stop');
      let stopping = false;
      stop.addEventListener('click', () => { stopping = true; stop.disabled = true; progress.textContent += ' Stopping after this one…'; });
      renderAll.addEventListener('click', async () => {
        const mine = ++round; // also stops the automatic first round
        renderAll.disabled = true; stop.hidden = false; stop.disabled = false; stopping = false;
        let done = 0;
        try {
          for (const go of cards) {
            if (stopping || mine !== round || !gallery.isConnected) break;
            progress.textContent = `Rendering ${done + 1} of ${cards.length}.`;
            await go();
            done++;
          }
        } finally {
          renderAll.disabled = false; stop.hidden = true;
          progress.textContent = done === cards.length ? `Rendered all ${cards.length}.` : `Rendered ${done} of ${cards.length}. Stopped.`;
        }
      });
      body.replaceChildren(
        h('div', { class: 'card' }, h('div', { class: 'row' },
          segmented('Theme', [['light', 'Light'], ['dark', 'Dark']], theme, v => { theme = v; store.set('preview-theme', v); auto(); }),
          segmented('Width', [[1200, 'Desktop 1200 px'], [390, 'Phone 390 px']], width, v => { width = v; store.set('preview-width', v); auto(); }),
          h('span', { class: 'grow' }), progress, stop, renderAll),
          h('p', { class: 'hint' }, 'The preview does not load OutSystems UI, so its classes look plain. Judge your own CSS, texts and layout.'), browserNote),
        gallery);
      auto();
    }
    if (state.module) picker.go();
  });

  // 4. Prompt builder
  // The modules come from the working set: Change modules are the ones to change, Read only modules go in as --in
  // producers. A module typed in the field and not in the working set counts as a Change module from a path.
  page('prompt', 'Agents', 'Prompt builder', 'Write a clear task for an AI agent: the modules, what to build, the exact elements to use and what it may not do.', async main => {
    const saved = store.get('prompt', {});
    const defaults = { task: '', elements: [], noInstall: true, noPublish: true, preview: false, validate: true, review: true };
    const opts = { ...defaults, ...saved };
    const moduleInput = h('input', { type: 'text', id: 'p-module', class: 'code', value: workset.list().length ? '' : state.module, placeholder: 'C:\\Work\\Orders.oml', spellcheck: 'false' });
    const taskInput = h('textarea', { id: 'p-task', placeholder: 'Add an entity Invoice (Number Text 20 mandatory, Amount Decimal, IssuedOn Date) with Invoice_CreateOrUpdate and Invoice_Delete server actions, and a Reactive list screen InvoiceList.' });
    taskInput.value = opts.task;
    const elements = [...opts.elements];
    const chips = h('div', { class: 'signature', 'aria-live': 'polite' });
    const setList = h('div');
    const findName = h('input', { type: 'search', id: 'p-find', class: 'code', placeholder: 'Customer_Validate or Customer*', spellcheck: 'false' });
    const findIn = h('input', { type: 'text', id: 'p-in', class: 'code', placeholder: 'C:\\Work\\Producers', value: store.get('producers', ''), spellcheck: 'false' });
    const findButton = h('button', { class: 'button', type: 'button' }, 'Find');
    const findOut = h('div', { class: 'stack' });
    const checks = {
      validate: ['Stage the result and validate it', opts.validate],
      review: ['Review the change: oml review … --md', opts.review],
      noInstall: ['Do not install', opts.noInstall],
      noPublish: ['Do not publish', opts.noPublish],
      preview: ['Use preview for UI: light, dark and phone width', opts.preview],
    };
    const boxes = Object.fromEntries(Object.entries(checks).map(([k, [, v]]) => [k, h('input', { type: 'checkbox', id: 'p-' + k, checked: v })]));
    const output = h('textarea', { id: 'p-out', class: 'code', readonly: true, rows: 14 });

    const clean = p => p.trim().replace(/^"(.*)"$/, '$1');
    const staged = p => p.replace(/\.oml$/i, '') + '.staged.oml';
    const specFile = m => (m.name || fileName(m.path).replace(/\.oml$/i, '')) + '.spec.json';
    // The modules of this task: the working set, plus a typed module that is not in it.
    function modules() {
      const list = workset.list();
      const typed = clean(moduleInput.value);
      if (typed && !list.some(m => m.path.toLowerCase() === typed.toLowerCase())) list.unshift({ path: typed, name: fileName(typed).replace(/\.oml$/i, ''), role: 'change', source: { kind: 'path' } });
      return { change: list.filter(m => m.role !== 'read'), read: list.filter(m => m.role === 'read'), all: list };
    }

    function text() {
      const { change, read, all } = modules();
      const lines = [];
      lines.push(change.length === 0 ? 'Module: (choose a module to change).' : change.length === 1 ? `Module: ${change[0].path}.` : `Modules to change: ${change.map(m => m.path).join(', ')}.`);
      const taskText = taskInput.value.trim();
      if (taskText) lines.push(/[.!?]$/.test(taskText) ? taskText : taskText + '.');
      if (read.length) lines.push(`Read only (producers; read them with --in, never change them): ${read.map(m => m.path).join(', ')}.`);
      if (elements.length) {
        lines.push(`Use these elements from other modules: ${elements.map(e => e.name).join(', ')}.`);
        const known = new Set(all.map(m => m.path.toLowerCase()));
        const files = [...new Set(elements.map(e => e.file).filter(f => f && !known.has(f.toLowerCase())))];
        if (files.length) lines.push(`The producer module file${files.length > 1 ? 's are' : ' is'} ${files.join(', ')}.`);
      }
      if (all.length) {
        lines.push('', 'Where the modules come from:');
        for (const m of all) lines.push(`- ${fileName(m.path)}: ${workset.sourceLine(m)}`);
        lines.push('');
      }
      if (boxes.preview.checked) lines.push('Preview the UI in light and dark and at phone width, follow the UI checklist, and send me the screenshots.');
      if (boxes.validate.checked) lines.push('Stage the result and validate it.');
      if (boxes.review.checked && !change.length) lines.push('Then run oml review on the module and its staged file with --md, and show me the Markdown.');
      if (boxes.review.checked) for (const m of change)
        lines.push(`Then run oml review ${m.path} ${staged(m.path)} --md ${m.path.replace(/\.oml$/i, '')}.review.md and show me the Markdown.`);
      const not = [boxes.noInstall.checked && 'install', boxes.noPublish.checked && 'publish'].filter(Boolean);
      if (not.length) lines.push(`Do not ${not.join(' or ')}.`);
      return lines.join('\n').replace(/\n{3,}/g, '\n\n');
    }

    // The same task as `oml run` steps: read the modules, then (after the agent wrote spec.json) apply, validate and review.
    function steps() {
      const { change, read } = modules();
      const ins = read.flatMap(m => ['--in', m.path]);
      const list = [];
      for (const m of change) list.push(['inspect', m.path, '--own']);
      if (change.length && elements.length) list.push(['describe', change[0].path, ...elements.map(e => e.name), ...ins]);
      for (const m of change) {
        list.push(['apply', m.path, specFile(m), staged(m.path)]);
        if (boxes.validate.checked) list.push(['validate', staged(m.path), '--warnings']);
        if (boxes.review.checked) list.push(['review', m.path, staged(m.path), '--md', m.path.replace(/\.oml$/i, '') + '.review.md']);
      }
      return `// Write ${change.map(specFile).join(', ')} first, then: oml run steps.json --continue. Install and publish are not run steps: they need your OK.\n` +
        JSON.stringify({ steps: list }, null, 2) + '\n';
    }

    const stepsButton = copyButton(() => steps(), 'Copy as oml run steps', 'Steps');
    const stepsHint = h('p', { class: 'hint', 'aria-live': 'polite' });
    function build() {
      output.value = text();
      const changing = modules().change.length > 0;
      stepsButton.disabled = !changing;
      stepsHint.textContent = changing ? '' : 'Run steps need a module to change: choose one in Get modules, or type one.';
      store.set('prompt', { task: taskInput.value, elements, ...Object.fromEntries(Object.entries(boxes).map(([k, b]) => [k, b.checked])) });
      chips.replaceChildren(...elements.map((e, i) => h('span', { class: 'chip' }, e.name,
        h('button', { type: 'button', 'aria-label': 'Remove ' + e.name, onclick: () => { elements.splice(i, 1); build(); } }, '×'))));
      if (!elements.length) add(chips, h('span', { class: 'muted small' }, 'No elements yet. Find them below and add them, so the names are exact.'));
      const set = workset.list();
      setList.replaceChildren(set.length === 0 ? h('p', { class: 'hint' }, 'No working set yet. ', h('a', { href: '#/modules' }, 'Get modules'), ', or type one module below.') :
        h('ul', { class: 'checklist', 'aria-label': 'Modules of the task' }, set.map(m => {
          const id = 'p-role-' + Math.random().toString(36).slice(2);
          const role = h('select', { id, onchange: e => workset.setRole(m.path, e.target.value) }, h('option', { value: 'change' }, 'Change'), h('option', { value: 'read' }, 'Read only'));
          role.value = m.role;
          return h('li', {}, h('label', { for: id, class: 'sr-only' }, 'Role of ' + m.name), role, h('span', {}, h('strong', {}, m.name), h('span', { class: 'tiny muted' }, ' · ' + workset.sourceLine(m))));
        })));
    }
    [moduleInput, taskInput].forEach(i => i.addEventListener('input', build));
    Object.values(boxes).forEach(b => b.addEventListener('change', build));
    onLeave(workset.onChange(build));

    const runFind = async () => {
      const name = findName.value.trim();
      if (!name) { findName.focus(); return; }
      store.set('producers', findIn.value.trim());
      const { change, read } = modules();
      const folders = [...findIn.value.split(';').map(s => s.trim()).filter(Boolean), ...read.map(m => m.path)];
      const module = change[0] && change[0].path;
      const data = await task(findButton, findOut, 'Searching…', async () => need(await api('find', { name, path: module || undefined, in: folders })));
      if (!data) return;
      const matches = ((data.output && data.output.matches) || []).filter(m => m.source !== 'this module');
      add(findOut, matches.length === 0 ? h('p', { class: 'muted' }, 'No element with that name in referenced or producer modules.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('tbody', {}, matches.map(m => h('tr', {},
          h('td', {}, h('strong', {}, m.qualified), h('div', { class: 'tiny muted' }, `${m.kind} · ${m.source}`)),
          h('td', { class: 'num' }, h('button', { class: 'button small', type: 'button', 'aria-label': 'Add ' + m.qualified, onclick: () => {
            if (!elements.some(e => e.name === m.qualified)) elements.push({ name: m.qualified, file: m.source === 'producer file' ? m.file : null });
            build(); toast('Added ' + m.qualified);
          } }, 'Add'))))))), cli(data.cli));
    };
    findButton.addEventListener('click', runFind);
    findName.addEventListener('keydown', e => { if (e.key === 'Enter') runFind(); });

    add(main, h('div', { class: 'grid-2' },
      h('section', { class: 'card', 'aria-label': 'Task details' },
        h('div', { class: 'field' }, h('span', { class: 'label' }, h('strong', { class: 'small' }, 'Modules (working set)')), setList),
        h('div', { class: 'field' }, h('label', { for: 'p-module' }, 'Another module to change (optional)'), moduleInput),
        h('div', { class: 'field' }, h('label', { for: 'p-task' }, 'What should the agent build or change?'), taskInput,
          h('p', { class: 'hint' }, 'One task per message works best. Split big features: data model first, then logic, then screens.')),
        h('div', { class: 'field' }, h('span', { class: 'label', id: 'p-el-label' }, h('strong', { class: 'small' }, 'Elements from other modules')), chips),
        h('div', { class: 'grid-2' },
          h('div', { class: 'field' }, h('label', { for: 'p-find' }, 'Find an element'), findName),
          h('div', { class: 'field' }, h('label', { for: 'p-in' }, 'Producer folders (optional)'), findIn, h('p', { class: 'hint' }, 'Read only modules are searched too.'))),
        h('div', { class: 'row' }, findButton), findOut,
        h('fieldset', { class: 'field' }, h('legend', { class: 'sr-only' }, 'Steps and limits for the agent'),
          h('p', {}, h('strong', { class: 'small' }, 'Steps and limits')),
          Object.entries(checks).map(([k, [label]]) => h('label', { class: 'check', for: 'p-' + k }, boxes[k], label)))),
      h('section', { class: 'card', 'aria-label': 'Task for the agent' },
        h('div', { class: 'card-head' }, h('h2', {}, 'Task for the agent'),
          h('div', { class: 'row' }, copyButton(() => output.value, 'Copy task', 'Task'), stepsButton)), stepsHint,
        h('label', { for: 'p-out', class: 'sr-only' }, 'Task text'), output,
        h('p', { class: 'hint' }, 'Paste it into your agent. Agents ask you before anything that changes a real module or an environment.'),
        cli('oml run steps.json --continue'))));
    build();

    // tests/dashboard-check.ps1 opens #/prompt?selftest=1: the working set API and the prompt text are checked in the
    // page itself, then the person's own working set is put back. Success sets <html data-selftest="ok">.
    if (new URLSearchParams(location.hash.split('?')[1] || '').get('selftest') === '1') selfTest();
    function selfTest() {
      const saved = ['workset', 'sources', 'recent'].map(k => [k, store.get(k, null)]);
      const fail = m => { throw new Error('Prompt self-test: ' + m); };
      const has = (t, part) => { if (!t.includes(part)) fail('missing "' + part + '" in:\n' + t); };
      try {
        workset.clear();
        let calls = 0;
        const stop = workset.onChange(() => calls++);
        workset.add('C:\\T\\Orders.oml', { kind: 'studio', module: 'Orders' });
        workset.add('C:\\T\\Shop.oml', { kind: 'lifetime', app: 'Shop', env: 'Development' }, 'read');
        workset.add('c:\\t\\orders.oml', { kind: 'studio', module: 'Orders' }); // the same file in other case: still one entry
        stop();
        workset.add('C:\\T\\Extra.oml', { kind: 'folder', folder: 'C:\\T' });
        workset.remove('C:\\T\\Extra.oml');
        const list = workset.list();
        if (list.length !== 2 || calls !== 3) fail(`list ${list.length}, calls ${calls}`);
        if (workset.changes().map(m => m.name).join() !== 'Orders' || workset.reads().map(m => m.name).join() !== 'Shop') fail('roles');
        if (list[0].source.kind !== 'studio' || !list[0].source.at) fail('source');
        moduleInput.value = '';
        elements.splice(0, elements.length, { name: 'Shop.Customer_Get', file: null });
        boxes.review.checked = true; boxes.validate.checked = true;
        const t = text();
        has(t, 'Module: C:\\T\\Orders.oml.');
        has(t, 'Read only (producers; read them with --in, never change them): C:\\T\\Shop.oml.');
        has(t, '- Orders.oml: copied from the module Orders open in Service Studio (oml pull). This is a copy; do not install over the open module without asking.');
        has(t, '- Shop.oml: downloaded from LifeTime: application Shop, as it runs in Development (oml lt download).');
        has(t, 'Then run oml review C:\\T\\Orders.oml C:\\T\\Orders.staged.oml --md C:\\T\\Orders.review.md and show me the Markdown.');
        has(t, 'Use these elements from other modules: Shop.Customer_Get.');
        const run = JSON.parse(steps().replace(/^\/\/[^\n]*\n/, '')).steps.map(s => s.join(' '));
        const expect = ['inspect C:\\T\\Orders.oml --own', 'describe C:\\T\\Orders.oml Shop.Customer_Get --in C:\\T\\Shop.oml', 'apply C:\\T\\Orders.oml Orders.spec.json C:\\T\\Orders.staged.oml',
          'validate C:\\T\\Orders.staged.oml --warnings', 'review C:\\T\\Orders.oml C:\\T\\Orders.staged.oml --md C:\\T\\Orders.review.md'];
        if (run.join('|') !== expect.join('|')) fail('steps: ' + run.join(' | '));
        if (stepsButton.disabled || !steps().startsWith('// Write Orders.spec.json first')) fail('steps button or comment');
        boxes.review.checked = false;
        if (text().includes('oml review')) fail('review step not optional');
        workset.setRole('C:\\T\\Shop.oml', 'change');
        if (!text().includes('Modules to change: C:\\T\\Orders.oml, C:\\T\\Shop.oml.')) fail('two change modules');
        workset.setRole('C:\\T\\Orders.oml', 'read'); workset.setRole('C:\\T\\Shop.oml', 'read');
        if (!stepsButton.disabled || !stepsHint.textContent) fail('run steps must be off without a module to change');
        root.dataset.selftest = 'ok';
      } finally {
        for (const [k, v] of saved) { if (v === null) { try { localStorage.removeItem('oml-dash-' + k); } catch { /* nothing kept */ } } else store.set(k, v); }
        elements.splice(0, elements.length);
        boxes.review.checked = true;
        build();
      }
    }
  });

  // 5. Recipe gallery
  page('recipes', 'Agents', 'Recipe gallery', 'Ready specs for common changes. Copy one as the start of a spec, or copy a prompt that asks an agent to use it.', async main => {
    const out = h('div', { class: 'stack' });
    add(main, out);
    const data = await task(null, out, 'Loading recipes…', () => api('recipes'));
    if (!data) return;
    const filter = h('input', { type: 'search', placeholder: 'Filter recipes', 'aria-label': 'Filter recipes' });
    const list = h('div', { class: 'recipe-list' });
    const cards = data.recipes.map(r => {
      const title = r.file.replace(/\.jsonc$/, '').replace(/-/g, ' ').replace(/^./, c => c.toUpperCase());
      const first = (r.description.split('\n')[0] || title).replace(/\.$/, '');
      const prompt = `Module: ${state.module || 'C:\\Work\\Orders.oml'}. Use the recipe skill/recipes/${r.file} as the start of the spec: ${first.charAt(0).toLowerCase() + first.slice(1)}. ` +
        'Rename the example names to fit this module. Stage the result and validate it. Do not install or publish.';
      const code = h('div', { hidden: true }, colorCode(r.text, 'json'));
      const toggle = h('button', { class: 'button small', type: 'button', 'aria-expanded': 'false', 'aria-label': 'Show the JSON of ' + title }, 'Show JSON');
      toggle.addEventListener('click', () => { code.hidden = !code.hidden; toggle.textContent = code.hidden ? 'Show JSON' : 'Hide JSON'; toggle.setAttribute('aria-label', toggle.textContent.replace('JSON', 'the JSON of ') + title); toggle.setAttribute('aria-expanded', String(!code.hidden)); });
      const card = h('section', { class: 'card', 'aria-label': title },
        h('h2', {}, title), h('p', { class: 'tiny mono muted' }, r.file), h('p', {}, r.description),
        h('div', { class: 'row' }, copyButton(r.text, 'Copy spec', 'Spec', 'Copy the spec of ' + title), copyButton(prompt, 'Copy prompt', 'Prompt', 'Copy the prompt for ' + title), toggle), code,
        cli(`oml apply ${state.module || 'C:\\Work\\Orders.oml'} skill\\recipes\\${r.file} ${(state.module || 'C:\\Work\\Orders.oml').replace(/\.oml$/i, '')}.staged.oml`));
      return [(r.file + ' ' + r.description).toLowerCase(), card];
    });
    cards.forEach(([, c]) => add(list, c));
    filter.addEventListener('input', () => { const q = filter.value.toLowerCase(); cards.forEach(([t, c]) => { c.hidden = q && !t.includes(q); }); });
    add(out, h('div', { class: 'field' }, filter), list, templatesCard());

    // `oml templates`: the local template and sample modules, to start a module with `oml new --template`.
    function templatesCard() {
      const run = h('button', { class: 'button', type: 'button' }, 'Show local templates');
      const result = h('div');
      run.addEventListener('click', async () => {
        const data = await task(run, result, 'Reading the templates… This can take some seconds.', async () => need(await api('templates')));
        if (!data) return;
        const o = data.output;
        const items = Array.isArray(o) ? o : (o && (o.templates || o.items)) || [];
        add(result, items.length ? h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Template', 'Kind', 'Theme'].map(t => h('th', {}, t)))),
          h('tbody', {}, items.map(t => h('tr', {}, h('td', {}, h('strong', {}, t.name || fileName(t.file || t.path)), h('div', { class: 'tiny muted mono', text: t.file || t.path || '' })),
            h('td', {}, t.available === false ? h('span', { class: 'small muted' }, t.problem || 'Not usable') : t.kind || ''), h('td', {}, t.theme || '')))))) : h('details', { open: true }, h('summary', {}, 'Result'), colorCode(JSON.stringify(o, null, 2), 'json')),
          cli(data.cli), cli('oml new C:\\Work\\New.oml --template <name> --name New'));
      });
      return h('section', { class: 'card', 'aria-labelledby': 'tpl-title' }, h('h2', { id: 'tpl-title' }, 'Module templates'),
        h('p', { class: 'muted small' }, 'Templates and samples installed with Service Studio on this computer.'), run, result);
    }
  });

  // 6. Fonts and images
  page('assets', 'Assets', 'Fonts and images', 'Make fonts and images smaller for the web. Files are converted on this computer and deleted after you get the result.', async main => {
    let mode = store.get('assets-mode', 'images');
    const tabs = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Kind of file' });
    const body = h('div', { class: 'stack' });
    for (const [value, label] of [['images', 'Images'], ['fonts', 'Fonts']]) {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === mode) }, label);
      b.addEventListener('click', () => { mode = value; store.set('assets-mode', value); tabs.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); draw(); });
      add(tabs, b);
    }
    add(main, h('div', { class: 'field' }, tabs), body);

    function draw() {
      const out = h('div', { class: 'stack' });
      if (mode === 'fonts') {
        body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Fonts to WOFF2'),
          h('p', { class: 'muted small' }, 'TTF, OTF and WOFF fonts become WOFF2, the smallest web font format. All glyphs and features stay.'),
          dropZone('.ttf,.otf,.woff,.woff2', 'Drop fonts (.ttf, .otf, .woff)', files => convert('convert/fonts', files, {}, out)), out));
      } else drawImages(out);
    }

    // The image form shows only the options of the chosen format; each maps to one `oml image` option.
    function drawImages(out) {
      const more = store.get('img-more', {});
      const field = (id, text, input, hint) => h('div', { class: 'field' }, h('label', { for: id }, text), input, hint ? h('p', { class: 'hint' }, hint) : null);
      const number = (id, min, max, placeholder, value, step) => h('input', { type: 'number', id, min: String(min), max: String(max), step: step || '1', placeholder, value: value === undefined ? '' : value });
      const box = (id, value) => h('input', { type: 'checkbox', id, checked: !!value });
      const check = (input, text, hint) => h('div', { class: 'field' }, h('label', { class: 'check', for: input.id }, input, h('span', {}, text, hint ? h('span', { class: 'hint block' }, hint) : null)));

      const to = h('select', { id: 'img-to' }, [['', 'Same as input'], ['webp', 'WebP'], ['png', 'PNG'], ['jpeg', 'JPEG'],
        ['svg', 'SVG (trace or optimize)'], ['ico', 'ICO (icon, several sizes)']].map(([v, t]) => h('option', { value: v }, t)));
      to.value = store.get('img-to', 'webp');
      const formatHint = h('p', { class: 'hint' });
      const maxWidth = number('img-width', 1, 100000, 'No limit', store.get('img-width', ''));
      const maxHeight = number('img-height', 1, 100000, 'No limit', more.maxHeight);
      const quality = h('input', { type: 'range', id: 'img-quality', min: '1', max: '100', value: store.get('img-quality', 85) });
      const qualityText = h('span', { class: 'mono small' }, quality.value);
      quality.addEventListener('input', () => { qualityText.textContent = quality.value; });
      const sizeFields = [field('img-width', 'Max width (px)', maxWidth), field('img-height', 'Max height (px)', maxHeight)];
      const qualityField = h('div', { class: 'field' }, h('label', { for: 'img-quality' }, 'Quality ', qualityText), quality, h('p', { class: 'hint' }, 'JPEG and WebP only. 85 is a good start; lower is smaller.'));

      const mode = h('select', { id: 'img-mode' }, [['trace', 'Trace: vector paths (logos, icons)'], ['embed', 'Embed: the picture inside an SVG (photos)']].map(([v, t]) => h('option', { value: v }, t)));
      mode.value = more.mode === 'embed' ? 'embed' : 'trace';
      const colors = number('img-colors', 1, 32, 'Auto (up to 16)', more.colors);
      const tolerance = number('img-tolerance', 0.1, 5, '0.3', more.tolerance, '0.1');
      const minArea = number('img-min-area', 0, 1000000, 'Auto', more.minArea);
      const alpha = number('img-alpha', 1, 255, '128', more.alphaThreshold);
      const transparentBg = box('img-transparent', more.transparentBg);
      const traceOnly = h('div', {},
        h('div', { class: 'grid-3' },
          field('img-colors', 'Colours', colors, 'Most colours in the result.'),
          field('img-tolerance', 'Smoothness (px)', tolerance, 'How far a curve may be from the edge: 0.1 to 5. Higher is smoother and smaller.'),
          field('img-min-area', 'Remove specks under (px²)', minArea, 'Shapes and holes smaller than this go.'),
          field('img-alpha', 'Alpha threshold', alpha, 'From this alpha (1 to 255) a pixel counts as solid.')),
        check(transparentBg, 'Transparent background', 'Leave out the colour around the border, such as a white background.'));
      const traceGroup = h('fieldset', { class: 'options' }, h('legend', {}, 'For PNG and JPEG files'),
        field('img-mode', 'Mode', mode), traceOnly);

      const precision = number('img-precision', 0, 10, 'Auto', more.precision);
      const removeIds = box('img-remove-ids', more.removeIds), aggressive = box('img-aggressive', more.aggressive), stripTitle = box('img-strip-title', more.stripTitle);
      const optimizeGroup = h('fieldset', { class: 'options' }, h('legend', {}, 'For SVG files (optimize)'),
        h('div', { class: 'grid-3' }, field('img-precision', 'Decimals', precision, '0 to 10. Auto: from the image size; more are used when the result would look different.')),
        check(removeIds, 'Remove unused ids', 'Only when no other file or page points into this SVG.'),
        check(aggressive, 'Smallest result', 'Only for an SVG used as an image or a CSS background, not one inlined in a page.'),
        check(stripTitle, 'Remove the title and description', 'Screen readers read them: keep them on a meaningful image.'));

      const SIZES = [16, 24, 32, 48, 64, 128, 256];
      const picked = new Set(Array.isArray(more.sizes) && more.sizes.length ? more.sizes : [16, 32, 48]);
      const sizeBoxes = SIZES.map(n => box('img-size-' + n, picked.has(n)));
      const preset = (text, list) => h('button', { class: 'button small', type: 'button', onclick: () => sizeBoxes.forEach((b, i) => { b.checked = list.includes(SIZES[i]); }) }, text);
      const icoGroup = h('fieldset', { class: 'options' }, h('legend', {}, 'Icon sizes (px)'),
        h('div', { class: 'row' }, sizeBoxes.map((b, i) => h('label', { class: 'check inline', for: b.id }, b, String(SIZES[i])))),
        h('div', { class: 'row' }, preset('Favicon (16, 32, 48)', [16, 32, 48]), preset('App icon (all sizes)', SIZES)),
        h('p', { class: 'hint' }, 'Each size is drawn from the source. A non-square image is centred on a transparent square.'));

      const hints = {
        '': 'Each file keeps its format. GIF, BMP and ICO files need a format: choose one first.',
        webp: 'WebP: the smallest file for photos and pictures. Every current browser shows it.',
        png: 'PNG: lossless, keeps transparency.',
        jpeg: 'JPEG: for photos. Transparent parts become white.',
        svg: 'SVG: a PNG or JPEG logo or icon is traced to vector paths; an SVG file is optimized. Each file uses the options for its type.',
        ico: 'ICO: one file with several icon sizes, for a favicon or an app icon. It is often larger than the source.',
      };
      const show = () => {
        const v = to.value;
        formatHint.textContent = hints[v];
        sizeFields.forEach(f => { f.hidden = v === 'ico'; });
        qualityField.hidden = !['', 'webp', 'jpeg'].includes(v);
        traceGroup.hidden = optimizeGroup.hidden = v !== 'svg';
        traceOnly.hidden = mode.value === 'embed';
        icoGroup.hidden = v !== 'ico';
      };
      to.addEventListener('change', show); mode.addEventListener('change', show);
      show();

      // Only what applies is sent; an empty field keeps the CLI default.
      const options = files => {
        const svgs = files.filter(f => /\.svg$/i.test(f.name)).length, rasters = files.length - svgs;
        const v = to.value, n = el => el.value === '' ? undefined : Number(el.value), on = el => el.checked || undefined;
        const sizes = SIZES.filter((s, i) => sizeBoxes[i].checked);
        store.set('img-to', v); store.set('img-width', maxWidth.value); store.set('img-quality', Number(quality.value));
        store.set('img-more', { maxHeight: maxHeight.value, mode: mode.value, colors: colors.value, tolerance: tolerance.value, minArea: minArea.value, alphaThreshold: alpha.value,
          transparentBg: transparentBg.checked, precision: precision.value, removeIds: removeIds.checked, aggressive: aggressive.checked, stripTitle: stripTitle.checked, sizes });
        const o = { to: v || undefined };
        if (v !== 'ico') Object.assign(o, { maxWidth: n(maxWidth), maxHeight: n(maxHeight) });
        if (!qualityField.hidden) o.quality = Number(quality.value);
        if (v === 'ico') o.sizes = sizes;
        if (v === 'svg') {
          // Tracing options need a PNG or JPEG among the files; the SVG-only ones need an SVG.
          if (rasters && mode.value === 'embed') o.mode = 'embed';
          else if (rasters) Object.assign(o, { colors: n(colors), tolerance: n(tolerance), minArea: n(minArea), alphaThreshold: n(alpha), transparentBg: on(transparentBg) });
          o.precision = n(precision);
          if (svgs) Object.assign(o, { removeIds: on(removeIds), aggressive: on(aggressive), stripTitle: on(stripTitle) });
        }
        return o;
      };
      const refuse = (text, focus) => { out.replaceChildren(h('div', { class: 'message error', role: 'alert' }, h('strong', {}, 'Not sent. '), text)); if (focus) focus.focus(); };
      const send = files => {
        // The CLI refuses these inputs without --to; say so before the upload instead of after it.
        const needFormat = to.value === '' ? files.filter(f => /\.(ico|gif|bmp)$/i.test(f.name)) : [];
        if (needFormat.length) return refuse(`${needFormat.map(f => f.name).join(', ')}: ICO, GIF and BMP files need a format. Choose one under Format, then drop the files again.`, to);
        if (to.value === 'ico' && !sizeBoxes.some(b => b.checked)) return refuse('Choose at least one icon size.', sizeBoxes[0]);
        const bad = [...body.querySelectorAll('input[type="number"]')].find(i => !i.closest('[hidden]') && !i.checkValidity());
        if (bad) { bad.reportValidity(); return refuse(`${bad.labels[0] ? bad.labels[0].textContent : 'A field'}: ${bad.validationMessage}`, bad); }
        convert('convert/images', files, options(files), out);
      };
      body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Images'),
        h('p', { class: 'muted small' }, 'PNG, JPEG, WebP, GIF, BMP, ICO and SVG. Images are never scaled up, and metadata is removed. ' +
          'A converted file can be larger than the original (ICO and embedded SVG often are): check the Saved column.'),
        h('div', { class: 'grid-3' }, h('div', { class: 'field' }, h('label', { for: 'img-to' }, 'Format'), to, formatHint), sizeFields, qualityField),
        traceGroup, optimizeGroup, icoGroup,
        dropZone('.png,.jpg,.jpeg,.gif,.bmp,.webp,.ico,.svg', 'Drop images', send), out));
    }

    async function convert(path, files, options, out) {
      const data = await task(null, out, `Converting ${files.length} file${files.length > 1 ? 's' : ''}…`, async () => {
        const uploads = await readFiles(files);
        return api(path, { files: uploads, ...options });
      });
      if (!data) return;
      const rows = data.files || [];
      const done = rows.filter(r => r.data);
      const totalIn = done.reduce((s, r) => s + (r.inputBytes || 0), 0), totalOut = done.reduce((s, r) => s + (r.outputBytes || 0), 0);
      const fonts = path === 'convert/fonts';
      // An ICO holds several sizes, so "saved" means nothing for it.
      const comparable = r => r.outputFormat !== 'ico';
      const size = (dims, n) => [dims ? `${dims.width} × ${dims.height} px` : '', bytes(n)].filter(Boolean).join(', ');
      add(out,
        done.length > 1 ? h('div', { class: 'row' }, h('span', { class: 'badge ok' }, `${done.length} files: ${bytes(totalIn)} → ${bytes(totalOut)}` + (done.every(comparable) ? `, ${saved(totalIn, totalOut)}` : '')),
          data.zipDownload ? downloadLink(data.zipDownload, 'Download all (zip)', fonts ? 'fonts-woff2.zip' : 'images-optimized.zip') : null) : null,
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, [fonts ? '' : 'Result', 'File', 'Before', 'After', 'Saved', ''].map((t, i) => h('th', { class: i >= 2 && i <= 4 ? 'num' : null }, t)))),
          h('tbody', {}, rows.map(r => {
            let thumb = null;
            if (!fonts && r.data) {
              const type = { webp: 'image/webp', png: 'image/png', jpeg: 'image/jpeg', svg: 'image/svg+xml', ico: 'image/x-icon' }[r.outputFormat] || 'application/octet-stream';
              const url = blobUrl(r.data, type); onLeave(() => URL.revokeObjectURL(url));
              const original = files.find(f => f.name === r.name);
              const before = original ? URL.createObjectURL(original) : null;
              if (before) onLeave(() => URL.revokeObjectURL(before));
              const title = 'Result: ' + r.output;
              thumb = h('button', { class: 'thumb-button', type: 'button', 'aria-label': `View ${r.output}` + (before ? ', before and after' : ''),
                onclick: () => viewer.open({ src: url, title, label: `After: ${r.output}, ${size(r.after, r.outputBytes)}`, before: before && { src: before, label: `Before: ${r.name}, ${size(r.before, r.inputBytes)}` } }) },
                h('img', { class: 'thumb', src: url, alt: title }));
            }
            const d = r.details || {};
            const facts = [
              d.sizes ? `Sizes ${d.sizes.join(', ')} px` : null,
              d.mode === 'trace' ? `Traced: ${d.colors} colour${d.colors === 1 ? '' : 's'}, ${d.paths} path${d.paths === 1 ? '' : 's'}` : d.mode === 'embed' ? 'Picture embedded in an SVG' : null,
              d.removedBackground ? `Background ${d.removedBackground} left out` : null,
              d.kept === 'original' ? 'Kept the original' + (d.reason ? ': ' + d.reason : '') : null,
              d.note || null,
            ].filter(Boolean);
            return h('tr', {},
              h('td', {}, thumb),
              h('td', {}, h('strong', {}, r.output || r.name), r.output ? h('div', { class: 'tiny muted' }, 'from ' + r.name, r.after ? ` · ${r.after.width}×${r.after.height}` : '') : null,
                facts.map(f => h('div', { class: 'tiny muted' }, f)),
                r.error ? h('div', { class: 'small', role: 'alert' }, h('span', { class: 'saved-bad' }, 'Not converted: '), r.error) : null,
                r.note ? h('div', { class: 'small muted' }, r.note === 'already woff2' ? 'Already WOFF2: nothing to do.' : r.note) : null),
              h('td', { class: 'num' }, bytes(r.inputBytes)), h('td', { class: 'num' }, bytes(r.outputBytes)),
              h('td', { class: 'num' }, !r.data ? '' : !comparable(r) ? h('span', { class: 'muted', title: 'An ICO holds several sizes, so it is not compared with the source.' }, 'n/a')
                : h('span', { class: r.outputBytes <= r.inputBytes ? 'saved-good' : 'saved-bad' }, saved(r.inputBytes, r.outputBytes))),
              h('td', { class: 'num' }, r.data ? h('button', { class: 'button small', type: 'button', 'aria-label': 'Download ' + r.output, onclick: () => download(r.output, r.data, 'application/octet-stream', true) }, icon('download'), 'Download') : null));
          })))),
        fonts && done.length ? fontFace(done) : null,
        cli(data.cli));
    }
    const saved = (a, b) => a ? `${Math.round((1 - b / a) * 100)}%` : '';
    function fontFace(done) {
      const css = done.map(r => {
        const base = r.output.replace(/\.woff2$/i, '');
        const weight = /black|heavy/i.test(base) ? 900 : /extrabold|ultrabold/i.test(base) ? 800 : /semibold|demibold/i.test(base) ? 600 : /bold/i.test(base) ? 700 : /medium/i.test(base) ? 500 : /extralight|ultralight/i.test(base) ? 200 : /light/i.test(base) ? 300 : /thin/i.test(base) ? 100 : 400;
        const family = base.replace(/[-_ ]?(thin|extralight|ultralight|light|regular|medium|semibold|demibold|bold|extrabold|ultrabold|black|heavy|italic)+$/gi, '') || base;
        return `@font-face {\n  font-family: "${family}";\n  src: url("${r.output}") format("woff2");\n  font-weight: ${weight};\n  font-style: ${/italic/i.test(base) ? 'italic' : 'normal'};\n  font-display: swap;\n}`;
      }).join('\n\n');
      return h('div', { class: 'stack' }, h('div', { class: 'card-head' }, h('h3', {}, 'CSS for the theme'), copyButton(css, 'Copy CSS', 'CSS')),
        h('p', { class: 'hint' }, 'Upload the WOFF2 files as resources of the module, then check the file names in url(…).'), h('pre', { class: 'code-view' }, h('code', { text: css })));
    }
    draw();
  });

  // 7. OpenAPI
  page('openapi', 'Module', 'OpenAPI', 'Export the exposed REST APIs of a module as OpenAPI 3.0.3, fill in the missing details, and download the finished file.', async main => {
    const body = h('div', { class: 'stack' });
    const picker = modulePicker(async (path, button, status) => {
      const data = await task(button, status, 'Reading the module…', () => inspect(path));
      if (data) show(path, data);
    });
    const servers = h('input', { type: 'text', id: 'oa-server', class: 'code', value: store.get('openapi-servers', ''), placeholder: 'https://dev.example.com/Orders/rest/OrdersAPI', spellcheck: 'false' });
    add(main, picker.el,
      h('section', { class: 'card', 'aria-label': 'OpenAPI options' }, h('div', { class: 'field' }, h('label', { for: 'oa-server' }, 'Server addresses (--server, optional)'), servers,
        h('p', { class: 'hint' }, 'The base address of the API in each environment. Separate several with ;. Empty: the file keeps a <FILL: …> placeholder.'))),
      body);

    function show(path, data) {
      const apis = (data.output && data.output.restServices) || [];
      if (!apis.length) { body.replaceChildren(h('div', { class: 'empty' }, 'This module exposes no REST API.')); return; }
      const out = h('div', { class: 'stack' });
      body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Exposed REST APIs'),
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['API', 'Methods', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, apis.map(a => {
            const b = h('button', { class: 'button small primary', type: 'button', 'aria-label': 'Generate OpenAPI for ' + a.name }, 'Generate');
            b.addEventListener('click', () => generate(path, a.name, b, out));
            return h('tr', {}, h('td', {}, h('strong', {}, a.name), h('div', { class: 'tiny muted' }, a.baseUrl || '')),
              h('td', {}, (a.methods || []).map(m => h('div', { class: 'small mono' }, `${m.method} ${m.url}`))), h('td', { class: 'num' }, b));
          }))))), out);
    }

    async function generate(path, apiName, button, out) {
      store.set('openapi-servers', servers.value.trim());
      const list = servers.value.split(';').map(x => x.trim()).filter(Boolean);
      const data = await task(button, out, `Generating OpenAPI for ${apiName}…`, async () => need(await api('openapi', { path, api: apiName, servers: list })));
      if (!data) return;
      for (const file of data.files || []) add(out, openApiFile(file, data.cli));
    }

    function openApiFile(file, command) {
      let doc;
      try { doc = JSON.parse(file.text); } catch { return h('div', { class: 'message error' }, 'The generated file is not valid JSON.'); }
      const endpoints = [];
      for (const [p, ops] of Object.entries(doc.paths || {}))
        for (const [method, op] of Object.entries(ops || {})) if (typeof op === 'object') endpoints.push([method.toUpperCase(), p, op.summary || op.operationId || '']);
      const fills = [];
      (function walk(node, trail) {
        if (typeof node === 'string') { if (node.includes('<FILL:')) fills.push({ trail, text: node }); return; }
        if (node && typeof node === 'object') for (const [k, v] of Object.entries(node)) walk(v, trail.concat(Array.isArray(node) ? Number(k) : k));
      })(doc, []);
      const inputs = fills.map((f, i) => {
        const id = `fill-${Math.random().toString(36).slice(2)}-${i}`;
        const hint = (/<FILL:\s*([^>]*)>/.exec(f.text) || [])[1] || '';
        const input = h('input', { type: 'text', id, placeholder: hint });
        return { f, input, el: h('div', { class: 'field' }, h('label', { for: id }, f.trail.join(' › ')), input, h('p', { class: 'hint' }, 'Now: ', h('code', { text: f.text }))) };
      });
      const progress = h('span', { class: 'badge' });
      const update = () => { const n = inputs.filter(x => x.input.value.trim()).length; progress.className = 'badge ' + (n === inputs.length ? 'ok' : 'warn'); progress.textContent = `${n} of ${inputs.length} filled`; };
      inputs.forEach(x => x.input.addEventListener('input', update));
      update();
      const finished = () => {
        const copyDoc = JSON.parse(file.text);
        for (const { f, input } of inputs) {
          const value = input.value.trim();
          if (!value) continue;
          let node = copyDoc;
          for (const k of f.trail.slice(0, -1)) node = node[k];
          // A value that is only a placeholder becomes the typed text; a placeholder inside a longer text is replaced in place.
          node[f.trail[f.trail.length - 1]] = /^\s*<FILL:[^>]*>\s*$/.test(f.text) ? value : f.text.replace(/<FILL:[^>]*>/, value);
        }
        return JSON.stringify(copyDoc, null, 2) + '\n';
      };
      return h('section', { class: 'card', 'aria-label': file.name },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, file.name), h('p', { class: 'muted small' }, 'Saved without your values in ', h('code', { text: file.path }))),
          h('div', { class: 'row' }, copyButton(finished, 'Copy JSON', 'OpenAPI'), h('button', { class: 'button small primary', type: 'button', onclick: () => download(file.name, finished(), 'application/json') }, icon('download'), 'Download'))),
        h('h3', {}, `Endpoints (${endpoints.length})`),
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Method', 'Path', 'Summary'].map(t => h('th', {}, t)))),
          h('tbody', {}, endpoints.map(([m, p, s]) => h('tr', {}, h('td', {}, h('span', { class: 'badge accent' }, m)), h('td', { class: 'mono small' }, p), h('td', {}, s)))))),
        h('div', { class: 'card-head' }, h('h3', {}, `Placeholders (${fills.length})`), progress),
        fills.length ? h('p', { class: 'hint' }, 'Fill in what you know. Empty fields keep their <FILL: …> text, so you can finish later.') : h('p', { class: 'muted' }, 'Nothing to fill in.'),
        h('div', { class: 'grid-2' }, inputs.map(x => x.el)),
        cli(command));
    }
    if (state.module) picker.go();
  });

  // 8. Architecture
  // An architecture or check report: badges, the page in a sandboxed frame, and its files from /download/<id>.
  function reportView(data, title, name) {
    const report = data.output || {};
    // architecture: findings by severity; check: a list of findings with the counts in summary.
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
      // The shared viewer: zoom, full screen and "Open in a new tab"; the report follows the dashboard theme.
      data.view ? viewer({ view: data.view, title, width: 1400, height: 1150, themed: true }) : null,
      cli(data.cli));
  }
  const lines = text => String(text || '').split(/\r?\n|;/).map(s => s.trim()).filter(Boolean);

  page('architecture', 'Module', 'Architecture', 'Make one page with the applications, modules, layers, dependencies and findings of a folder of .oap or .oml files.', async main => {
    const folders = h('textarea', { id: 'arch-in', class: 'code', rows: 3, placeholder: 'C:\\Exports\\Payments', spellcheck: 'false' });
    folders.value = store.get('arch-in', '');
    const apps = h('input', { type: 'text', id: 'arch-apps', placeholder: 'Payments; Orders', value: store.get('arch-apps', '') });
    const layers = h('input', { type: 'text', id: 'arch-layers', class: 'code', placeholder: 'C:\\Exports\\layers.json', value: store.get('arch-layers', ''), spellcheck: 'false' });
    const json = h('input', { type: 'checkbox', id: 'arch-json', checked: store.get('arch-json', false) });
    const run = h('button', { class: 'button primary', type: 'button' }, 'Make the page');
    const useSet = h('button', { class: 'button', type: 'button', disabled: !workset.list().length }, 'Use the working set');
    useSet.addEventListener('click', () => { folders.value = workset.list().map(m => m.path).join('\n'); });
    const out = h('div', { class: 'stack' });
    const options = () => {
      store.set('arch-apps', apps.value); store.set('arch-layers', layers.value.trim()); store.set('arch-json', json.checked);
      return { apps: lines(apps.value), layers: layers.value.trim().replace(/^"(.*)"$/, '$1') || undefined, json: json.checked };
    };
    const show = data => { if (data) add(out, reportView(data, 'Architecture page', 'architecture')); };
    run.addEventListener('click', async () => {
      const list = lines(folders.value);
      if (!list.length) { folders.focus(); return; }
      store.set('arch-in', folders.value);
      show(await task(run, out, 'Reading the modules… This can take a minute for many modules.', async () => need(await api('architecture', { in: list, ...options() }))));
    });
    add(main, h('div', { class: 'grid-2' },
      h('section', { class: 'card' }, h('h2', {}, 'From a folder'),
        h('div', { class: 'field' }, h('label', { for: 'arch-in' }, 'Folders or files, one per line'), folders, h('p', { class: 'hint' }, 'Folders are searched with their subfolders for .oap and .oml files.')),
        h('div', { class: 'row' }, run, useSet)),
      h('section', { class: 'card' }, h('h2', {}, 'From files'),
        dropZone('.oap,.oml', 'Drop .oap or .oml files', async files => show(await task(null, out, 'Uploading and reading the modules…', async () => need(await api('architecture', { files: await readFiles(files), ...options() }))))))),
      h('section', { class: 'card', 'aria-labelledby': 'arch-opt-title' }, h('h2', { id: 'arch-opt-title' }, 'Options'),
        h('div', { class: 'grid-3' },
          h('div', { class: 'field' }, h('label', { for: 'arch-apps' }, 'Only these applications (--app)'), apps, h('p', { class: 'hint' }, 'Separate names with ;. Empty: all.')),
          h('div', { class: 'field' }, h('label', { for: 'arch-layers' }, 'Layers file (--layers)'), layers, h('p', { class: 'hint' }, 'JSON such as { "Shop*": "Core" }. Empty: oml guesses.')),
          h('div', { class: 'field' }, h('span', { class: 'label' }, h('strong', { class: 'small' }, 'Data')), h('label', { class: 'check', for: 'arch-json' }, json, 'Also save the JSON (--json)')))),
      out);
  });

  // A path field with the recent modules as suggestions (Review before/after, rules file, BDDFramework module).
  let fieldCount = 0;
  // modules: also suggest the working set (a module field, not a rules or baseline file).
  function pathField(label, value, placeholder, hint, modules) {
    const id = 'path-' + (++fieldCount);
    const input = h('input', { type: 'text', id, class: 'code', list: id + '-recent', value: value || '', placeholder, autocomplete: 'off', spellcheck: 'false' });
    const paths = [...(modules ? workset.list().map(m => m.path) : []), ...store.get('recent', [])].filter((p, i, all) => all.findIndex(q => q.toLowerCase() === p.toLowerCase()) === i);
    const el = h('div', { class: 'field' }, h('label', { for: id }, label), input,
      h('datalist', { id: id + '-recent' }, paths.map(p => h('option', { value: p }))), hint ? h('p', { class: 'hint' }, hint) : null);
    return { el, input, value: () => input.value.trim().replace(/^"(.*)"$/, '$1') };
  }
  const staged = path => String(path).replace(/(\.staged)?\.oml$/i, '.staged.oml');
  const severityBadge = s => h('span', { class: 'badge ' + (s === 'high' ? 'fail' : s === 'medium' ? 'warn' : '') }, s);
  function selectFilter(label, values) {
    const id = 'filter-' + (++fieldCount);
    const select = h('select', { id }, h('option', { value: '' }, 'All'), values.map(v => h('option', { value: v }, v)));
    return { el: h('div', { class: 'field' }, h('label', { for: id }, label), select), select };
  }

  // A prompt that asks an agent to fix one finding with oml, in the order the skill asks for.
  // source: { file } for one module, or { folders, apps } (module name -> application) for folders; rules: the rules file.
  function fixPrompt(f, source) {
    const file = source.file || `${f.module}.oml`;
    const where = f.element ? `${f.module}/${f.element}` : `the module ${f.module}`;
    const read = f.element && !/^(upward|side|cycle|core-ui|foundation|large|unused-public)/.test(f.rule)
      ? `oml describe "${file}" "${f.element}"` : `oml inspect "${file}" --own`;
    const app = source.apps && source.apps[f.module];
    const folders = (source.folders || []).join(', ');
    // A module with an application came from an .oap package (the check reads packages without extracting them).
    const origin = source.file ? `Fix one oml check finding in ${file}.`
      : app && app !== '(no application)'
        ? `Fix one oml check finding in the module ${f.module} of the application ${app}, from an .oap package in ${folders}. ` +
          `Extract it first: oml app list "<file.oap>", then oml app extract "<file.oap>" "<folder>". Work on the extracted ${f.module}.oml.`
        : `Fix one oml check finding in the module file ${f.module}.oml, in ${folders} or a subfolder.`;
    const rules = source.rules ? ` --rules "${source.rules}"` : '';
    return [
      origin,
      `Finding: ${f.title}. Rule ${f.rule}, severity ${f.severity}, ${f.category}. Where: ${where}.`,
      `Detail: ${f.detail}`,
      `How to fix: ${f.fix}`,
      `Read it first: ${read}`,
      `Write a spec with only this change. Run oml spec check on it, then oml apply "${file}" spec.json "${staged(file)}".`,
      `Then run oml check "${staged(file)}"${rules} and confirm that ${f.rule} at ${where} is gone.`,
      `Then run oml review "${file}" "${staged(file)}" --md review.md and show me the .md.`,
      'Do not install. Do not publish.',
    ].join('\n');
  }

  // Check
  page('check', 'Module', 'Check', 'Find AI Mentor-style code patterns in a module or a folder: security, performance, architecture and maintainability.', async main => {
    const out = h('div', { class: 'stack' });
    const folders = h('textarea', { id: 'check-in', class: 'code', rows: 2, placeholder: 'C:\\Exports\\Payments', spellcheck: 'false' });
    folders.value = store.get('check-in', '');
    const rules = pathField('Rules file (optional)', store.get('check-rules', ''), 'C:\\Work\\oml-rules.json', 'The team rules: which rules run, their severity and ignored findings.');
    const baseline = pathField('Baseline (optional)', store.get('check-baseline', ''), 'C:\\Work\\check.json', 'An earlier check report (.json). Findings then show as new or existing.');
    const runFolders = h('button', { class: 'button', type: 'button' }, 'Check folders');
    const makeRules = h('button', { class: 'button', type: 'button' }, 'Make a rules file');
    const run = async (input, button, status) => {
      store.set('check-rules', rules.value()); store.set('check-baseline', baseline.value());
      const data = await task(button, status || out, 'Checking… Service Studio starts once, then each module takes about a second.', async () =>
        need(await api('check', { ...input, rules: rules.value() || undefined, baseline: baseline.value() || undefined })));
      if (data) show(data, { ...input, rules: rules.value() || undefined });
    };
    const picker = modulePicker((path, button, status) => run({ path }, button, status), { button: 'Check module' });
    runFolders.addEventListener('click', () => {
      const list = folders.value.split(/\r?\n|;/).map(s => s.trim()).filter(Boolean);
      if (!list.length) { folders.focus(); return; }
      store.set('check-in', folders.value);
      run({ in: list }, runFolders);
    });
    makeRules.addEventListener('click', async () => {
      const data = await task(makeRules, out, 'Writing the rules file…', async () => need(await api('check/init-rules', {})));
      if (!data) return;
      rules.input.value = data.path;
      add(out, h('div', { class: 'message' }, 'Saved ', h('code', { text: data.path }), '. Edit it to match the AI Mentor rules your team uses. It is now the rules file of this page.'), cli(data.cli));
    });
    add(main, picker.el,
      h('section', { class: 'card', 'aria-label': 'Check options' }, h('div', { class: 'grid-2' },
        h('div', {}, h('div', { class: 'field' }, h('label', { for: 'check-in' }, 'Or folders with .oml and .oap files, one per line'), folders), runFolders),
        h('div', {}, rules.el, baseline.el, makeRules))),
      out);
    if (state.module) await picker.go();

    function show(data, input) {
      out.replaceChildren();
      const report = data.output || {};
      const s = report.summary || {};
      const findings = report.findings || [];
      const source = { file: input.path, folders: input.in || [], rules: input.rules,
        apps: Object.fromEntries((report.modules || []).map(m => [m.name, m.application])) };
      const severity = selectFilter('Severity', ['high', 'medium', 'low']);
      const category = selectFilter('Category', [...new Set(findings.map(f => f.category))].sort());
      const module = selectFilter('Module', [...new Set(findings.map(f => f.module))].sort());
      const count = h('p', { class: 'small muted', 'aria-live': 'polite' });
      const rows = findings.map(f => [f, h('tr', {},
        h('td', { class: 'nowrap' }, severityBadge(f.severity), f.status ? h('div', { class: 'tiny muted' }, f.status) : null),
        h('td', {}, h('strong', {}, f.title), h('div', { class: 'tiny muted mono' }, f.rule),
          h('div', { class: 'small' }, h('strong', {}, 'Where: '), f.module, f.element ? [' ', h('code', { class: 'tiny', text: f.element })] : ' (the module)'),
          h('div', { class: 'small' }, f.detail), h('div', { class: 'small' }, h('strong', {}, 'Fix: '), f.fix),
          h('button', { class: 'button small', type: 'button', 'aria-label': `Copy a fix prompt for ${f.rule} at ${f.element || f.module}`, onclick: () => copy(fixPrompt(f, source), 'Fix prompt') }, icon('copy'), 'Copy a fix prompt')))]);
      const filter = () => {
        let shown = 0;
        for (const [f, tr] of rows) {
          tr.hidden = (severity.select.value && f.severity !== severity.select.value) || (category.select.value && f.category !== category.select.value) || (module.select.value && f.module !== module.select.value);
          if (!tr.hidden) shown++;
        }
        count.textContent = `${shown} of ${findings.length} findings shown.`;
      };
      [severity, category, module].forEach(x => x.select.addEventListener('change', filter));
      filter();
      add(out,
        h('section', { class: 'card', 'aria-label': 'Check results' },
          h('div', { class: 'card-head' }, h('h2', {}, findings.length === 1 ? '1 finding' : findings.length ? `${findings.length} findings` : 'No findings'),
            h('div', { class: 'row' }, h('span', { class: 'badge' + (s.high ? ' fail' : '') }, `${s.high || 0} high`), h('span', { class: 'badge' + (s.medium ? ' warn' : '') }, `${s.medium || 0} medium`), h('span', { class: 'badge' }, `${s.low || 0} low`),
              s.new !== undefined ? h('span', { class: 'badge accent' }, `${s.new} new`) : null, s.ignored ? h('span', { class: 'badge' }, `${s.ignored} ignored`) : null)),
          (s.skipped || []).length ? h('div', { class: 'message info' }, 'Not checked: ', s.skipped.map(x => `${x.input || x.module}${x.rule ? ' / ' + x.rule : ''} (${x.reason})`).join('; ')) : null,
          (report.modules || []).length ? h('p', { class: 'small muted' }, 'Modules: ', report.modules.map(m => m.name).join(', ')) : null,
          findings.length ? h('div', {},
            h('div', { class: 'grid-3' }, severity.el, category.el, module.el), count,
            h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Severity', 'Finding'].map(t => h('th', { class: 'nowrap' }, t)))), h('tbody', {}, rows.map(r => r[1]))))) : null,
          h('p', { class: 'hint' }, 'oml check covers part of the AI Mentor patterns. 0 findings does not mean the code is correct.'),
          data.json ? h('div', { class: 'row' }, h('span', { class: 'small muted' }, 'Report data: ', h('code', { text: data.json })),
            h('button', { class: 'button small', type: 'button', onclick: () => { baseline.input.value = data.json; store.set('check-baseline', data.json); toast('Baseline set'); } }, 'Use as the baseline next time')) : null,
          cli(data.cli)),
        data.view ? h('section', { class: 'card', 'aria-label': 'Check report page' }, h('div', { class: 'card-head' }, h('h2', {}, 'Report page'),
          h('div', { class: 'row' }, data.download ? downloadLink(data.download, 'Download HTML', 'check.html') : null,
            data.jsonDownload ? downloadLink(data.jsonDownload, 'Download JSON', 'check.json') : null)),
          h('p', { class: 'hint' }, 'Saved in ', h('code', { text: data.path })), viewer({ view: data.view, title: 'Check report', width: 1200, height: 900, themed: true })) : null);
    }
  });

  // Review
  page('review', 'Module', 'Review', 'Compare a module with its changed copy: production risks first, then what changed and the new check findings.', async main => {
    const before = pathField('Before (the module as it is now)', store.get('review-before', state.module), 'C:\\Work\\Orders.oml', null, true);
    const after = pathField('After (the changed copy)', store.get('review-after', state.module ? staged(state.module) : ''), 'C:\\Work\\Orders.staged.oml', 'Usually the staged file of an apply, next to the module.', true);
    // A working-set module fills Before, and its staged copy fills After.
    const quick = worksetQuick(path => { before.input.value = path; after.input.value = staged(path); });
    const rules = pathField('Rules file (optional)', store.get('check-rules', ''), 'C:\\Work\\oml-rules.json');
    const run = h('button', { class: 'button primary', type: 'button' }, 'Review');
    const out = h('div', { class: 'stack' });
    before.input.addEventListener('change', () => { if (!after.value() && before.value()) after.input.value = staged(before.value()); });
    const go = async () => {
      if (!before.value()) { before.input.focus(); return; }
      if (!after.value()) after.input.value = staged(before.value());
      store.set('review-before', before.value()); store.set('review-after', after.value());
      const data = await task(run, out, 'Comparing… about 3 seconds for a big module.', async () =>
        need(await api('review', { before: before.value(), after: after.value(), rules: rules.value() || undefined })));
      if (data) show(data);
    };
    run.addEventListener('click', go);
    add(main, h('section', { class: 'card', 'aria-label': 'Modules to compare' }, h('div', { class: 'grid-2' }, before.el, after.el), h('div', { class: 'field' }, quick), rules.el, run), out);
    // #/review?before=<path>&after=<path> runs at once: a link a person or a script can keep.
    const linked = hashParams();
    if (linked.get('before') && linked.get('after')) { before.input.value = linked.get('before'); after.input.value = linked.get('after'); await go(); }

    function show(data) {
      out.replaceChildren();
      const r = data.output || {};
      const s = r.summary || {};
      const table = (heads, rows) => h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, heads.map(t => h('th', {}, t)))), h('tbody', {}, rows)));
      add(out,
        h('section', { class: 'card', 'aria-label': 'Review results' },
          h('div', { class: 'card-head' }, h('h2', {}, r.module || 'Review'),
            h('div', { class: 'row' }, h('span', { class: 'badge ' + (s.risks ? 'fail' : 'ok') }, `${s.risks || 0} risks`), s.info ? h('span', { class: 'badge' }, `${s.info} to look at`) : null,
              h('span', { class: 'badge ' + (s.newFindings ? 'warn' : 'ok') }, `${s.newFindings || 0} new findings`),
              h('span', { class: 'badge' }, `${s.added || 0} added, ${s.changed || 0} changed, ${s.removed || 0} removed, ${s.renamed || 0} renamed, ${s.recreated || 0} recreated`))),
          h('div', { class: 'row' }, data.markdown ? copyButton(data.markdown, 'Copy Markdown', 'Markdown') : null, data.notes ? copyButton(data.notes, 'Copy release notes', 'Release notes') : null,
            data.markdownDownload ? downloadLink(data.markdownDownload, 'Download Markdown', 'review.md') : null,
            data.download ? downloadLink(data.download, 'Download HTML', 'review.html') : null),
          h('h3', {}, `Risks (${(r.risks || []).length})`),
          (r.risks || []).length ? table(['Rule', 'Element', 'What'], r.risks.map(x => h('tr', {},
            h('td', {}, h('span', { class: 'badge ' + (x.info ? '' : 'fail') }, x.info ? 'Look' : 'Rule ' + x.rule), h('div', { class: 'tiny muted' }, x.ruleName)),
            h('td', { class: 'mono small' }, x.element), h('td', {}, x.text)))) : h('p', { class: 'muted' }, 'No change breaks a production rule.'),
          h('h3', {}, `Changes (${(r.changes || []).length})`),
          (r.changes || []).length ? table(['Area', 'Element', 'Change'], r.changes.map(c => h('tr', {},
            h('td', {}, h('span', { class: 'badge' }, c.area)), h('td', {}, h('strong', {}, c.name), h('div', { class: 'tiny muted' }, c.kind)),
            h('td', {}, h('span', { class: 'badge ' + ({ added: 'ok', removed: 'fail', recreated: 'fail' }[c.change] || 'warn') }, c.change), (c.details || []).length ? h('ul', { class: 'checklist' }, c.details.map(d => h('li', {}, d)), c.moreDetails ? h('li', { class: 'muted' }, `${c.moreDetails} more in the Markdown`) : null) : null)))) : h('p', { class: 'muted' }, 'No change found.'),
          h('h3', {}, `New findings (${(r.newFindings || []).length})`),
          (r.newFindings || []).length ? table(['Severity', 'Finding', 'Element'], r.newFindings.map(f => h('tr', {}, h('td', {}, severityBadge(f.severity)),
            h('td', {}, h('strong', {}, f.title), h('div', { class: 'small' }, f.fix)), h('td', { class: 'mono small' }, f.element || '(module)')))) : h('p', { class: 'muted' }, 'No new check finding.'),
          r.truncated ? h('p', { class: 'hint' }, 'The lists here are cut. The Markdown and the report page have everything.') : null,
          h('p', { class: 'hint' }, r.note || 'Logic bugs are not covered; 0 findings does not mean the code is correct.'),
          data.folder ? h('p', { class: 'hint' }, 'Markdown, release notes and the page are saved in ', h('code', { text: data.folder })) : null,
          cli(data.cli)),
        data.view ? h('section', { class: 'card', 'aria-label': 'Review report page' }, h('div', { class: 'card-head' }, h('h2', {}, 'Report page')),
          viewer({ view: data.view, title: 'Review report', width: 1200, height: 900, themed: true })) : null);
    }
  });

  // 9. LifeTime
  page('lifetime', 'Team', 'LifeTime', 'Environments, applications and deployments, read only. Tagging and deploying stay in the CLI, where you confirm each step.', async main => {
    const lt = state.info && state.info.lifeTime;
    if (!lt || !lt.configured) {
      add(main, h('section', { class: 'card' }, h('h2', {}, 'Connect LifeTime'),
        h('p', {}, 'The dashboard reads LifeTime with a service account token. For your safety, the token is never typed into this page.'),
        h('ol', { class: 'doc' },
          h('li', {}, 'In LifeTime, open User Management > Service Accounts and create an account with read permissions. Copy its token.'),
          h('li', {}, 'Stop this dashboard: use Stop dashboard at the bottom of the page list, or press Ctrl+C in its window.'),
          h('li', {}, 'In PowerShell, set the two variables and start the dashboard again:')),
        cli('$env:OML_LIFETIME_HOST = "lifetime.example.com"'),
        cli('$env:OML_LIFETIME_TOKEN = Read-Host -MaskInput "LifeTime token"'),
        cli('oml dashboard'),
        h('p', { class: 'hint' }, 'The host is the LifeTime hostname without https://. The token stays in that PowerShell window only.')));
      return;
    }
    const envOut = h('div'), appOut = h('div'), statusOut = h('div'), teamOut = h('div', { class: 'stack' });
    const envSelect = h('select', { id: 'lt-env' }, h('option', { value: '' }, 'All environments'));
    const teamSelect = h('select', { id: 'lt-team' }, h('option', { value: '' }, 'All teams'));
    const reportTeam = h('select', { id: 'lt-report-team' });
    const keyInput = h('input', { type: 'text', id: 'lt-key', class: 'code', placeholder: '6f1c2a9e-3b7d-4c55-9e0a-1d2b3c4d5e6f', spellcheck: 'false' });
    const lookup = h('button', { class: 'button', type: 'button' }, 'Look up');
    const archButton = h('button', { class: 'button primary', type: 'button' }, 'Architecture page');
    const checkButton = h('button', { class: 'button', type: 'button' }, 'Check');
    add(main,
      h('p', { class: 'muted small' }, 'Connected to ', h('code', { text: lt.host }), '. Read only: this page never tags, deploys, aborts or continues.'),
      h('section', { class: 'card' }, h('h2', {}, 'Environments'), envOut),
      h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', {}, 'Applications'),
        h('div', { class: 'row' }, h('label', { for: 'lt-team', class: 'sr-only' }, 'Team'), teamSelect, h('label', { for: 'lt-env', class: 'sr-only' }, 'Environment'), envSelect)), appOut),
      h('section', { class: 'card', 'aria-labelledby': 'lt-team-title' }, h('h2', { id: 'lt-team-title' }, 'Team reports'),
        h('p', { class: 'muted small' }, "Downloads the team's applications (read only) to a temporary folder, makes the page, then deletes the packages."),
        h('div', { class: 'field' }, h('label', { for: 'lt-report-team' }, 'Team'), h('div', { class: 'row' }, reportTeam, archButton, checkButton)), teamOut),
      h('section', { class: 'card' }, h('h2', {}, 'Deployment status'),
        h('div', { class: 'field' }, h('label', { for: 'lt-key' }, 'Deployment key'), h('div', { class: 'row' }, h('div', { class: 'grow' }, keyInput), lookup)), statusOut));

    const envs = await task(null, envOut, 'Reading environments…', async () => need(await api('lt/environments')));
    if (envs) {
      const list = Array.isArray(envs.output) ? envs.output : [];
      add(envOut, h('div', { class: 'links' }, list.map(e => h('div', { class: 'link-card' }, h('strong', {}, e.Name), h('span', {}, [e.EnvironmentType, e.Key].filter(Boolean).join(' · '))))), cli(envs.cli));
      list.forEach(e => add(envSelect, h('option', { value: e.Name }, e.Name)));
    }
    let teamsCli = '';
    try {
      const teams = need(await api('lt/teams'));
      teamsCli = teams.cli;
      (Array.isArray(teams.output) ? teams.output : []).forEach(t => { const name = t.name || t.Name; add(teamSelect, h('option', { value: name }, name)); add(reportTeam, h('option', { value: name }, name)); });
    } catch (e) { add(teamOut, errorBox(e)); }

    const loadApps = async () => {
      const env = envSelect.value, team = teamSelect.value;
      const data = await task(envSelect, appOut, 'Reading applications… LifeTime can take some seconds.', async () => need(await api('lt/apps' + (env ? '?env=' + encodeURIComponent(env) : ''))));
      if (!data) return;
      let apps = Array.isArray(data.output) ? data.output : [];
      let teamData = null;
      if (team) {
        try { teamData = need(await api('lt/team-apps?team=' + encodeURIComponent(team))); }
        catch (e) { add(appOut, errorBox(e)); return; }
        const keys = new Set((Array.isArray(teamData.output) ? teamData.output : []).map(a => a.Key || a.key));
        apps = apps.filter(a => keys.has(a.key));
      }
      const rows = apps.flatMap(a => (a.environments || []).length ? a.environments.map((e, i) => [i === 0 ? a.name : '', a.name, e.environment, e.version, e.isModified]) : [[a.name, a.name, '', '', null]]);
      add(appOut, apps.length === 0 ? h('p', { class: 'muted' }, team ? 'No applications of this team.' : 'No applications.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Application', 'Environment', 'Version (tag)', 'Changed since tag'].map(t => h('th', {}, t)))),
          h('tbody', {}, rows.map(([shown, name, env, version, modified]) => {
            const result = h('div');
            const get = env ? h('button', { class: 'button small', type: 'button', 'aria-label': `Download ${name} from ${env}` }, 'Download .oap') : null;
            if (get) get.addEventListener('click', () => ltDownload(name, env, false, get, result));
            return h('tr', {}, h('td', {}, h('strong', {}, shown)), h('td', {}, env || '', get ? h('div', {}, get) : null, result), h('td', { class: 'mono small' }, version || '—'),
              h('td', {}, modified === true ? h('span', { class: 'badge warn' }, 'Yes, not tagged') : modified === false ? h('span', { class: 'badge ok' }, 'No') : ''));
          })))),
        h('p', { class: 'hint' }, 'Download .oap saves the package as it runs in that environment. To work on its modules, use ', h('a', { href: '#/modules' }, 'Get modules'), ' > LifeTime.'),
        cli(data.cli), teamData ? cli(teamData.cli) : null);
    };
    envSelect.addEventListener('change', loadApps);
    teamSelect.addEventListener('change', loadApps);
    const teamReport = async (kind, button) => {
      const team = reportTeam.value;
      if (!team) { reportTeam.focus(); return; }
      const data = await task(button, teamOut, `Downloading the applications of ${team} and making the ${kind === 'check' ? 'check report' : 'architecture page'}… This can take minutes.`,
        async () => need(await api('lt/report', { team, kind })));
      if (data) add(teamOut, reportView(data, (kind === 'check' ? 'Check report of team ' : 'Architecture page of team ') + team, kind));
    };
    archButton.addEventListener('click', () => teamReport('architecture', archButton));
    checkButton.addEventListener('click', () => teamReport('check', checkButton));
    if (teamsCli) add(teamOut, cli(teamsCli));
    lookup.addEventListener('click', async () => {
      const key = keyInput.value.trim();
      if (!key) { keyInput.focus(); return; }
      const data = await task(lookup, statusOut, 'Reading the deployment…', async () => need(await api('lt/status?key=' + encodeURIComponent(key)), [0, 1]));
      if (!data) return;
      const s = (data.output && data.output.status) || {};
      const status = String(s.DeploymentStatus || '').toLowerCase();
      add(statusOut, h('div', { class: 'row' }, h('span', { class: 'badge ' + (status === 'finished_successful' ? 'ok' : /error|abort/.test(status) ? 'fail' : 'warn') }, status || 'unknown')),
        (s.Messages || []).length ? h('ul', { class: 'checklist' }, s.Messages.map(m => h('li', {}, typeof m === 'string' ? m : JSON.stringify(m)))) : null,
        cli(data.cli));
    });
    keyInput.addEventListener('keydown', e => { if (e.key === 'Enter') lookup.click(); });
    await loadApps();
  });

  // 10. Agent activity
  page('activity', 'Team', 'Agent activity', 'What agents ran with oml on this computer: commands, results and time. From the local usage log.', async main => {
    const out = h('div', { class: 'stack' });
    add(main, out);
    const first = await task(null, out, 'Reading the usage log…', () => api('log'));
    if (!first) return;
    if (!first.enabled) {
      add(out, h('section', { class: 'card' }, h('h2', {}, 'Turn on the usage log'),
        h('p', {}, 'oml can write one line per command to a file on this computer. It never sends anything, and never writes arguments, specs, tokens or folders.'),
        h('p', {}, 'For the current PowerShell window (start your agent and this dashboard from it):'), cli('$env:OML_LOG = "1"'),
        h('p', {}, 'For every new window:'), cli('setx OML_LOG 1'),
        h('p', { class: 'hint' }, 'Then start the dashboard again. The log is ', h('code', { text: first.file }), '.')));
      return;
    }
    const live = h('input', { type: 'checkbox', id: 'live', checked: true });
    const table = h('div'), summary = h('div');
    add(out, h('section', { class: 'card' },
      h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, 'Latest commands'), h('p', { class: 'muted small' }, 'From ', h('code', { text: first.file }))),
        h('label', { class: 'check', for: 'live' }, live, 'Refresh every 5 seconds')), table),
      h('section', { class: 'card' }, h('h2', {}, 'Summary'), summary));
    const draw = data => {
      const lines = data.lines || [];
      table.replaceChildren(lines.length === 0 ? h('p', { class: 'muted' }, 'No commands yet.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Time', 'Command', 'Module', 'Result', 'Time taken', 'Problem'].map((t, i) => h('th', { class: i === 4 ? 'num' : null }, t)))),
          h('tbody', {}, lines.slice(0, 100).map(l => h('tr', {},
            h('td', { class: 'small' }, l.ts ? new Date(l.ts).toLocaleString() : ''),
            h('td', {}, h('code', { text: 'oml ' + l.cmd }), l.run ? h('span', { class: 'tiny muted' }, ' in run') : null),
            h('td', { class: 'small' }, l.module || ''),
            h('td', {}, h('span', { class: 'badge ' + (l.exit === 0 ? 'ok' : l.exit === 1 ? 'warn' : 'fail') }, l.exit === 0 ? 'OK' : 'Exit ' + l.exit)),
            h('td', { class: 'num small' }, l.ms !== undefined ? (l.ms >= 1000 ? (l.ms / 1000).toFixed(1) + ' s' : l.ms + ' ms') : ''),
            h('td', { class: 'small' }, l.error || '')))))));
    };
    const loadSummary = async () => {
      const data = await task(null, summary, 'Counting…', async () => need(await api('log/summary')));
      if (!data || !data.output) return;
      add(summary, h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Command', 'Count', 'Failures', 'Median time', 'Most common problems'].map((t, i) => h('th', { class: i >= 1 && i <= 3 ? 'num' : null }, t)))),
        h('tbody', {}, (data.output.commands || []).map(c => h('tr', {}, h('td', {}, h('code', { text: 'oml ' + c.command })), h('td', { class: 'num' }, c.count), h('td', { class: 'num' }, c.failures),
          h('td', { class: 'num' }, c.medianMs >= 1000 ? (c.medianMs / 1000).toFixed(1) + ' s' : c.medianMs + ' ms'),
          h('td', { class: 'small' }, (c.topFailures || []).map(f => h('div', {}, `${f.count}× ${f.message}`)))))))), cli(data.cli));
    };
    draw(first);
    await loadSummary();
    const timer = setInterval(async () => {
      if (!live.checked) return;
      try { draw(await api('log')); } catch { /* the next tick tries again */ }
    }, 5000);
    onLeave(() => clearInterval(timer));
  });

  // 11. Command cookbook
  page('cookbook', 'Learn', 'Command cookbook', 'Every oml command with a real example. Copy one and paste it into PowerShell.', async main => {
    const search = h('input', { type: 'search', placeholder: 'Search commands, for example preview or font', 'aria-label': 'Search commands' });
    const list = h('div');
    const entries = [];
    for (const group of window.omlCookbook) {
      const section = h('section', { class: 'card cook-group', 'aria-label': group.group }, h('h2', {}, group.group));
      const rows = group.items.map(([command, text]) => {
        const row = h('div', { class: 'cook' }, h('p', {}, text), cli(command));
        entries.push([(command + ' ' + text + ' ' + group.group).toLowerCase(), row, section]);
        return row;
      });
      add(section, rows);
      add(list, section);
    }
    search.addEventListener('input', () => {
      const q = search.value.trim().toLowerCase();
      const visible = new Set();
      for (const [t, row, section] of entries) { row.hidden = q && !q.split(/\s+/).every(w => t.includes(w)); if (!row.hidden) visible.add(section); }
      list.querySelectorAll('.cook-group').forEach(s => { s.hidden = !visible.has(s); });
    });
    add(main, h('div', { class: 'field' }, search), list,
      h('p', { class: 'hint' }, 'Full reference: skill/reference/commands.md. Run oml --help for the short list.'));
  });

  // Docs and search (Ctrl+K anywhere opens this page with the search box focused)
  const KINDS = { doc: 'Doc', spec: 'Spec', key: 'Spec key', warning: 'Warning', recipe: 'Recipe', command: 'Command', file: 'File' };
  page('docs', 'Learn', 'Docs and search', 'Search the OutSystems rules, the spec reference, the validation warnings, the recipes and the commands. Open a result to read it here.', async main => {
    const params = hashParams();
    let scope = store.get('docs-scope', 'all');
    const q = h('input', { type: 'search', id: 'docs-q', value: params.get('q') || '', placeholder: 'confirm popup before delete', spellcheck: 'false', 'aria-describedby': 'docs-q-hint' });
    const go = h('button', { class: 'button primary', type: 'button' }, 'Search');
    const results = h('div'), reader = h('div', { class: 'stack' });
    const scopes = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Search in' });
    for (const [value, label] of [['all', 'Everything'], ['spec', 'Spec only'], ['warn', 'Warnings']]) {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === scope) }, label);
      b.addEventListener('click', () => { scope = value; store.set('docs-scope', value); scopes.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); if (q.value.trim()) search(); });
      add(scopes, b);
    }
    const search = async () => {
      const text = q.value.trim();
      if (!text) { q.focus(); return; }
      const data = await task(go, results, 'Searching…', async () => need(await api('search', { q: text, scope }), scope === 'warn' ? [0, 2] : [0]));
      if (!data) return;
      if (scope === 'warn') {
        // oml warn prints the matching rows of warnings.md, or exits 2 with the closest ids.
        if (data.code === 0) add(results, h('section', { class: 'card' }, markdown(data.output), cli(data.cli)));
        else add(results, h('div', { class: 'message info' }, data.stderr || 'No validation message matches.'), cli(data.cli));
        return;
      }
      const out = data.output || {};
      const hits = out.hits || [];
      add(results,
        out.weak ? h('div', { class: 'message info', role: 'status' }, h('strong', {}, 'Weak match. '), out.hint || 'No result is a strong match. Use OutSystems words (entity, screen, aggregate) or open the top results.') : null,
        hits.length === 0 ? h('p', { class: 'muted' }, out.hint || 'Nothing found. Try other words.') :
          h('ul', { class: 'hits', 'aria-label': 'Search results' }, hits.map(hit => h('li', {},
            h('div', { class: 'row' }, h('span', { class: 'badge accent' }, KINDS[hit.kind] || hit.kind), h('strong', {}, hit.title)),
            hit.snippet ? h('p', { class: 'small muted' }, hit.snippet) : null,
            h('div', { class: 'row' }, h('button', { class: 'button small', type: 'button', 'aria-label': 'Open ' + hit.title, onclick: () => open(hit.open) }, 'Open'), h('code', { class: 'tiny', text: hit.open }))))),
        cli(data.cli));
    };
    const open = async target => {
      const data = await task(null, reader, 'Opening…', async () => need(await api('doc/open', { open: target })));
      if (!data) return;
      const text = typeof data.output === 'string' ? data.output : JSON.stringify(data.output, null, 2);
      add(reader, h('section', { class: 'card', 'aria-label': target }, h('div', { class: 'card-head' }, h('h2', {}, target.replace(/^oml /, '')), copyButton(text, 'Copy text', 'Text')), markdown(text), cli(data.cli)));
      reader.scrollIntoView({ block: 'start', behavior: 'smooth' });
    };
    go.addEventListener('click', search);
    q.addEventListener('keydown', e => { if (e.key === 'Enter') search(); });
    add(main, h('section', { class: 'card', 'aria-label': 'Search' },
      h('div', { class: 'field' }, h('label', { for: 'docs-q' }, 'Search'), h('div', { class: 'row' }, h('div', { class: 'grow' }, q), go),
        h('p', { class: 'hint', id: 'docs-q-hint' }, 'Ctrl+K opens this search from any page. Write a question or OutSystems words; an id such as NoErrorHandlingInFlow finds its warning.')),
      scopes, results), reader);
    if (params.get('open')) await open(params.get('open'));
    if (q.value.trim()) await search();
    if (params.has('focus')) q.focus();
  });

  // 12. Rules and checklist
  page('rules', 'Learn', 'Rules and checklist', 'The production rules agents follow, the best practices, and the checklist for every screen and block.', async main => {
    const docs = { rules: ['doc/rules', 'oml doc rules'], best: ['doc/best-practices', 'oml doc best-practices'], checklist: ['doc/checklist', 'oml doc ui-checklist'] };
    let which = store.get('rules-tab', 'rules');
    if (!docs[which]) which = 'rules';
    const tabs = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Document' });
    const out = h('div', { class: 'card' });
    const load = async () => {
      const data = await task(null, out, 'Loading…', () => api(docs[which][0]));
      if (data) add(out, markdown(data.markdown), cli(docs[which][1]));
    };
    for (const [value, label] of [['rules', 'Production rules'], ['best', 'Best practices'], ['checklist', 'UI checklist']]) {
      const b = h('button', { type: 'button', 'aria-pressed': String(value === which) }, label);
      b.addEventListener('click', () => { which = value; store.set('rules-tab', value); tabs.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); load(); });
      add(tabs, b);
    }
    add(main, h('div', { class: 'field' }, tabs), out);
    await load();
  });

  // 13. Developer tools (tools.js)
  page('tools', 'Tools', 'Developer tools', 'Small tools that run only in this browser. Nothing you type here is sent anywhere.', async main => {
    const tools = window.omlTools || [];
    let current = store.get('tool', tools[0] && tools[0].id);
    if (!tools.some(t => t.id === current)) current = tools[0].id;
    const list = h('div', { class: 'panel tool-list', role: 'navigation', 'aria-label': 'Tools' });
    const body = h('section', { class: 'card', 'aria-live': 'off' });
    const draw = () => {
      list.querySelectorAll('button').forEach(b => b.setAttribute('aria-current', String(b.dataset.id === current)));
      const tool = tools.find(t => t.id === current);
      body.replaceChildren(h('h2', {}, tool.name), h('p', { class: 'muted small' }, tool.desc));
      tool.render(body);
    };
    for (const t of tools) add(list, h('button', { type: 'button', 'data-id': t.id, onclick: () => { current = t.id; store.set('tool', t.id); draw(); } }, t.name));
    add(main, h('div', { class: 'tools' }, list, body));
    draw();
    if (window.omlToolTests) setTimeout(window.omlToolTests); // a broken tool shows up as a script error
  });

  // ---------- shell ----------

  const titles = { Start: 'Start', Module: 'Your module', Agents: 'Work with agents', Assets: 'Assets', Team: 'Team and agents', Learn: 'Learn', Tools: 'Tools' };
  function buildNav() {
    const nav = document.getElementById('nav');
    const groups = [...new Set(pages.map(p => p.group))];
    for (const g of groups) {
      add(nav, h('div', { class: 'nav-group' }, h('p', { class: 'nav-group-title' }, titles[g] || g),
        pages.filter(p => p.group === g).map(p => h('a', { class: 'nav-link', href: '#/' + p.id, 'data-id': p.id }, icon(p.id), p.title))));
    }
  }

  let renderId = 0;
  async function route() {
    cleanups.forEach(fn => { try { fn(); } catch { /* nothing to undo */ } });
    cleanups = [];
    const id = (location.hash.match(/^#\/([\w-]+)/) || [])[1] || 'home';
    // #/explorer?module=C:\Work\Orders.oml opens that module: a link a person can keep.
    const linked = new URLSearchParams(location.hash.split('?')[1] || '').get('module');
    if (linked && linked !== state.module) setModule(linked);
    const p = pages.find(x => x.id === id) || pages[0];
    const mine = ++renderId;
    document.querySelectorAll('.nav-link').forEach(a => { if (a.dataset.id === p.id) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); });
    document.getElementById('page-group').textContent = titles[p.group] || p.group;
    document.getElementById('page-title').textContent = p.title;
    document.getElementById('page-desc').textContent = p.desc;
    document.title = p.title + ' · oml dashboard';
    const main = document.getElementById('page');
    main.replaceChildren();
    root.dataset.page = p.id;
    delete root.dataset.ready;
    document.getElementById('nav').classList.remove('open');
    document.getElementById('menu-toggle').setAttribute('aria-expanded', 'false');
    if (route.moved) document.getElementById('page-title').focus({ preventScroll: true });
    route.moved = true;
    window.scrollTo(0, 0);
    try { await p.render(main); }
    catch (e) { add(main, errorBox(e)); }
    if (mine === renderId) root.dataset.ready = p.id;
  }

  // A file dropped outside a drop zone would make the browser open it and leave the dashboard. Every drag of files over
  // the page shows an overlay that says where to drop; a drop outside a zone is stopped and explained. A page that takes
  // files anywhere sets onPageDrop(files => ...) (cleared when the page is left).
  let pageDrop = null;
  const onPageDrop = fn => { pageDrop = fn; onLeave(() => { pageDrop = null; }); };
  function dropGuard() {
    const text = h('p', {});
    const overlay = h('div', { class: 'drop-overlay', hidden: true, 'aria-hidden': 'true' }, text);
    document.body.append(overlay);
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

  async function start() {
    buildNav();
    const toggle = document.getElementById('theme-toggle');
    const label = () => toggle.setAttribute('aria-label', `Switch to ${root.dataset.theme === 'dark' ? 'light' : 'dark'} theme`);
    label();
    toggle.addEventListener('click', () => {
      root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem('oml-theme', root.dataset.theme); } catch { /* not remembered */ }
      label();
      // Report frames follow: only the #theme= part changes, so the report does not load again.
      document.querySelectorAll('iframe[data-themed]').forEach(f => { f.src = viewSrc({ view: f.dataset.themed, themed: true }); });
    });
    dropGuard();
    // Ctrl+K (or Cmd+K): the docs search, from any page.
    // On the Docs page it only focuses the box, so the typed words and the results stay.
    const openSearch = () => {
      const box = root.dataset.page === 'docs' && document.getElementById('docs-q');
      if (box) { box.focus(); box.select(); return; }
      location.hash = '#/docs?focus=' + Date.now();
    };
    document.getElementById('search-open').addEventListener('click', openSearch);
    document.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'k') { e.preventDefault(); openSearch(); } });
    // "Show timings": adds --timings to the commands that load a module, and shows the phases under the CLI line.
    const timings = document.getElementById('timings-toggle');
    const showTimings = () => timings.setAttribute('aria-pressed', String(!!store.get('timings', false)));
    showTimings();
    timings.addEventListener('click', () => { store.set('timings', !store.get('timings', false)); showTimings(); toast(store.get('timings', false) ? 'Timings on: commands add --timings' : 'Timings off'); });
    const menu = document.getElementById('menu-toggle');
    menu.addEventListener('click', () => {
      const nav = document.getElementById('nav');
      nav.classList.toggle('open');
      menu.setAttribute('aria-expanded', String(nav.classList.contains('open')));
    });
    document.getElementById('stop').addEventListener('click', async () => {
      if (!confirm('Stop the oml dashboard? You can start it again with oml dashboard.')) return;
      try { await api('shutdown', {}); } catch { /* it may already be gone */ }
      document.getElementById('page').replaceChildren(h('div', { class: 'empty' }, 'The dashboard has stopped. You can close this tab.'));
    });
    if (!KEY) {
      document.getElementById('page').append(h('div', { class: 'message error', role: 'alert' }, 'This page needs its key. Open the full address that oml dashboard printed in its window.'));
      return;
    }
    try {
      state.info = await api('info');
      document.getElementById('version').textContent = state.info.version ? 'oml ' + state.info.version : '';
    } catch (e) { document.getElementById('page').append(errorBox(e)); }
    window.addEventListener('hashchange', route);
    await route();
  }

  // For tools.js: the same building blocks.
  window.omlUi = { h, add, icon, copy, copyButton, cli, download, downloadUrl, downloadLink, api, need, task, errorBox, colorCode, toast, store, ApiError, viewer, onPageDrop, workset };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
