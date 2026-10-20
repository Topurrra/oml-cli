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
      area.value = text; document.add(body, area); area.select();
      try { document.execCommand('copy'); toast((what || 'Text') + ' copied'); } catch { toast('Copy did not work. Select the text and press Ctrl+C.'); }
      area.remove();
    }
  }
  function copyButton(getText, label, what) {
    return h('button', { class: 'button small', type: 'button', onclick: () => copy(typeof getText === 'function' ? getText() : getText, what) }, icon('copy'), label || 'Copy');
  }

  // The CLI command for an action, with a copy button: people learn the CLI from here.
  function cli(command) {
    return h('div', { class: 'cli' }, h('span', { class: 'cli-label', 'aria-hidden': 'true' }, 'CLI'),
      h('code', { text: command }), copyButton(command, 'Copy', 'Command'));
  }

  function download(name, data, type, base64) {
    let blob;
    if (base64) {
      const bin = atob(data); const bytes = new Uint8Array(bin.length);
      for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
      blob = new Blob([bytes], { type: type || 'application/octet-stream' });
    } else blob = new Blob([data], { type: type || 'text/plain' });
    const url = URL.createObjectURL(blob);
    const a = h('a', { href: url, download: name });
    document.add(body, a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
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

  async function api(path, body) {
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
    if (!res.ok) throw new ApiError(data.error || ('HTTP ' + res.status), data);
    return data;
  }

  // A command result with a non-zero exit code becomes an error with the CLI's own text attached.
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

  function working(label) {
    const seconds = h('span', { class: 'tiny' });
    const el = h('div', { class: 'working', role: 'status' }, h('span', { class: 'spinner', 'aria-hidden': 'true' }), h('span', {}, label), seconds);
    const start = Date.now();
    const timer = setInterval(() => {
      if (!el.isConnected) { clearInterval(timer); return; }
      const s = Math.round((Date.now() - start) / 1000);
      seconds.textContent = s >= 2 ? `${s} s` : '';
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

  const CONNECT_NOTE = 'Service Studio will ask you to allow the connection once.';
  const state = { module: store.get('module', ''), inspect: {}, info: null };
  function setModule(path) {
    state.module = path;
    store.set('module', path);
    const recent = [path, ...store.get('recent', []).filter(p => p.toLowerCase() !== path.toLowerCase())].slice(0, 8);
    store.set('recent', recent);
  }

  async function inspect(path, fresh) {
    if (!fresh && state.inspect[path]) return state.inspect[path];
    const data = need(await api('inspect', { path }));
    state.inspect[path] = data;
    return data;
  }

  let pickerCount = 0;
  // A module picker: type or paste a path, pick a recent one, or pull the module open in Service Studio.
  function modulePicker(onOpen, opts) {
    const id = 'module-' + (++pickerCount);
    const listId = id + '-recent';
    const input = h('input', { type: 'text', id, class: 'code', list: listId, value: state.module, placeholder: 'C:\\Work\\Orders.oml', autocomplete: 'off', spellcheck: 'false' });
    const recent = h('datalist', { id: listId }, store.get('recent', []).map(p => h('option', { value: p })));
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
    const pull = h('button', { class: 'button', type: 'button' }, 'Pull the module open in Service Studio');
    pull.addEventListener('click', () => pullFlow(pull, status, path => { input.value = path; go(); }));
    return {
      el: h('section', { class: 'card', 'aria-label': 'Choose a module' },
        h('div', { class: 'field' }, h('label', { for: id }, 'Module file (.oml)'), h('div', { class: 'row' }, h('div', { class: 'grow' }, input), open, pull),
          h('p', { class: 'hint' }, 'Paste a full path. In File Explorer: Shift + right-click the file, then "Copy as path". Recent files appear as you type. ',
            'Pull: ', CONNECT_NOTE)),
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
      const r = await task(btn, out, `Saving ${name} from Service Studio… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: name })));
      if (r) { toast('Saved ' + fileName(r.path)); out.replaceChildren(h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path })), cli(r.cli)); done(r.path); }
    };
    if (open.length === 1) { await pullOne(open[0], button); return; }
    out.replaceChildren(h('div', { class: 'message' }, h('p', {}, 'Several modules are open. Which one?'),
      h('div', { class: 'row' }, open.map(name => { const b = h('button', { class: 'button small', type: 'button' }, name); b.addEventListener('click', () => pullOne(name, b)); return b; }))));
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

  function frame(viewId, title, width, height) {
    const box = h('div', { class: 'frame-box' });
    const iframe = h('iframe', { src: '/view/' + viewId, title, sandbox: 'allow-scripts', loading: 'lazy' });
    add(box, iframe);
    const fit = () => {
      const scale = Math.min(1, (box.clientWidth || width) / width);
      iframe.style.width = width + 'px';
      iframe.style.height = height + 'px';
      iframe.style.transform = `scale(${scale})`;
      box.style.height = Math.round(height * scale) + 'px';
    };
    requestAnimationFrame(fit);
    if (window.ResizeObserver) new ResizeObserver(fit).observe(box);
    return box;
  }

  // ---------- pages ----------

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
        h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'studio-title' }, 'Service Studio'), h('p', { class: 'muted small' }, 'Modules open in Service Studio. Pull one to a file to explore it here.'))),
        studioStatus,
        h('div', { class: 'field' }), studioAgain, h('p', { class: 'hint' }, CONNECT_NOTE),
        studioOut),
      h('section', { class: 'card', 'aria-labelledby': 'links-title' },
        h('h2', { id: 'links-title' }, 'Go to'),
        h('div', { class: 'links' }, pages.filter(p => p.id !== 'home').map(p =>
          h('a', { class: 'link-card', href: '#/' + p.id }, h('strong', {}, icon(p.id), p.title), h('span', {}, p.desc)))),
        state.info ? h('p', { class: 'muted small' }, 'Files the dashboard makes (pulled modules, OpenAPI, architecture pages) are saved in ', h('code', { text: state.info.workFolder }), '. Nothing is overwritten.') : null));

    const runDoctor = async () => {
      const data = await task(again, doctorOut, 'Checking your setup…', async () => need(await api('doctor'), [0, 1]));
      if (!data || !data.output || !data.output.checks) return;
      const label = { ok: 'OK', warn: 'Check', fail: 'Fix needed', skip: 'Skipped' };
      const mcp = data.output.checks.find(c => c.name === 'studioMcp');
      if (mcp) studioStatus.replaceChildren(h('span', { class: 'badge ' + (mcp.status === 'ok' ? 'ok' : 'warn') }, h('span', { class: 'dot' }), mcp.status === 'ok' ? 'MCP server answers' : 'MCP server not reachable'),
        h('span', { class: 'muted' }, mcp.status === 'ok' ? mcp.detail : 'Start it in Service Studio: Edit > MCP Server > Start MCP server.'));
      add(doctorOut, 
        h('div', { class: 'row' }, data.output.ok ? h('span', { class: 'badge ok' }, h('span', { class: 'dot' }), 'Ready to use') : h('span', { class: 'badge fail' }, h('span', { class: 'dot' }), 'Something must be fixed')),
        h('ul', { class: 'checks', 'aria-label': 'Setup checks' }, data.output.checks.map(c => h('li', {},
          h('div', {}, h('span', { class: 'badge ' + c.status }, h('span', { class: 'dot' }), label[c.status] || c.status)),
          h('div', {}, h('div', { class: 'what' }, window.omlCheckNames[c.name] || c.name), h('div', { class: 'detail', text: c.detail }),
            c.fix ? h('div', { class: 'fix' + (c.status === 'fail' ? ' fail' : '') }, h('strong', {}, 'Fix: '), c.fix) : null)))),
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
            const b = h('button', { class: 'button small', type: 'button' }, 'Pull to a file');
            b.addEventListener('click', async () => {
              const r = await task(b, out, `Saving ${a.name}… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: a.name })));
              if (r) { setModule(r.path); add(out, h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path }), ' ', h('a', { href: '#/explorer' }, 'Open it in the Module explorer')), cli(r.cli)); }
            });
            return h('tr', {}, h('td', {}, a.name), h('td', { class: 'num' }, b));
          })))), out, cli(data.cli));
    };
    again.addEventListener('click', runDoctor);
    studioAgain.addEventListener('click', runStudio);
    await runDoctor();
  });

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
            h('td', { class: 'num' }, copyButton(m.qualified || m.name, 'Copy name', 'Name'))))))),
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
        findButton, findOut));

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
      for (const [key, title] of groups) {
        let list = out[key] || [];
        if (key === 'restServices' || key === 'restClients') list = list.flatMap(api => [{ name: api.name }, ...(api.methods || []).map(m => ({ name: m.name, sub: `${m.method} ${m.url}` }))]);
        if (!list.length) continue;
        const ul = h('ul');
        for (const item of list) {
          const b = h('button', { type: 'button', title: item.sub || '' }, item.name, item.sub ? h('span', { class: 'tiny muted' }, '  ' + item.sub) : null);
          b.addEventListener('click', () => { if (current) current.removeAttribute('aria-current'); current = b; b.setAttribute('aria-current', 'true'); describe(item.name); });
          const li = h('li', {}, b);
          items.push([item.name.toLowerCase(), li]);
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
      body.replaceChildren(
        h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, out.name || fileName(path)), h('p', { class: 'muted small' }, out.kind ? `${out.kind} module · ` : '', fileName(path))), reload),
        h('div', { class: 'explorer' }, h('div', { class: 'panel tree' }, h('div', { class: 'field' }, filter), items.length ? tree : h('p', { class: 'muted' }, 'This module has no own elements yet.')), detail),
        cli(data.cli));

      async function describe(name) {
        const result = await task(null, detail, `Reading ${name}…`, async () => need(await api('describe', { path, name, in: (store.get('producers', '') || '').split(';').map(s => s.trim()).filter(Boolean) })));
        if (!result) return;
        const d = Array.isArray(result.output) ? result.output[0] : result.output || {};
        const sig = d.signature || {};
        add(detail, 
          h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, d.element || name), h('div', { class: 'row small' },
            d.kind ? h('span', { class: 'badge accent' }, d.kind) : null, d.public === true ? h('span', { class: 'badge' }, 'Public') : null)),
            copyButton(d.element || name, 'Copy name', 'Name')),
          (sig.inputs || sig.outputs) ? h('div', { class: 'signature' },
            (sig.inputs || []).map(p => h('span', { class: 'chip' }, h('span', { class: 'muted' }, 'in'), `${p.name}: ${p.type}`)),
            (sig.outputs || []).map(p => h('span', { class: 'chip' }, h('span', { class: 'muted' }, 'out'), `${p.name}: ${p.type}`))) : null,
          d.text ? colorCode(d.text, 'describe') : colorCode(JSON.stringify(d, null, 2), 'json'),
          cli(result.cli));
      }
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

    function segmented(label, options, current, set) {
      const group = h('div', { class: 'segmented', role: 'group', 'aria-label': label });
      for (const [value, text] of options) {
        const b = h('button', { type: 'button', 'aria-pressed': String(value === current) }, text);
        b.addEventListener('click', () => { group.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b))); set(value); });
        add(group, b);
      }
      return group;
    }

    function show(path, data) {
      const out = data.output || {};
      const elements = [...(out.screens || []).map(s => ({ name: s.name, kind: 'Screen' })), ...(out.blocks || []).map(b => ({ name: b.name, kind: 'Block' }))];
      if (!elements.length) { body.replaceChildren(h('div', { class: 'empty' }, 'This module has no screens or blocks.')); return; }
      const cards = [];
      const renderAll = h('button', { class: 'button primary', type: 'button' }, 'Render all');
      const gallery = h('div', { class: 'gallery' });
      const browserNote = state.info && !state.info.browser ? h('p', { class: 'hint' }, 'Screenshots need Edge or Chrome. The live previews work without them.') : null;
      for (const el of elements) {
        const out = h('div');
        const render = h('button', { class: 'button small', type: 'button' }, 'Render');
        const shot = h('button', { class: 'button small', type: 'button', disabled: state.info && !state.info.browser }, 'Screenshot');
        const go = async screenshot => {
          const w = width, t = theme;
          const r = await task(screenshot ? shot : render, out, screenshot ? 'Taking a screenshot… about 5 seconds.' : 'Rendering…', async () =>
            need(await api('preview', { path, element: el.name, theme: t, width: w, screenshot })));
          if (!r) return;
          const report = r.output || {};
          const frameHeight = w <= 500 ? 760 : 640;
          let view;
          if (r.png) {
            const url = blobUrl(r.png, 'image/png');
            onLeave(() => URL.revokeObjectURL(url));
            view = h('div', { class: 'frame-box' }, h('img', { src: url, alt: `Screenshot of ${el.name}, ${t} theme, ${w} px wide` }));
          } else view = frame(r.view, `Preview of ${el.name}, ${t} theme, ${w} px wide`, w, frameHeight);
          const failed = (report.checklist || []).filter(c => !c.ok);
          add(out, view,
            r.png ? h('button', { class: 'button small', type: 'button', onclick: () => download(`${el.name}-${t}-${w}.png`, r.png, 'image/png', true) }, icon('download'), 'Download PNG') : null,
            h('div', { class: 'row small' }, failed.length ? h('span', { class: 'badge warn' }, `${failed.length} of ${report.checklist.length} checks need work`) : h('span', { class: 'badge ok' }, 'All UI checks pass'),
              report.javaScriptErrors && report.javaScriptErrors.count ? h('span', { class: 'badge fail' }, `${report.javaScriptErrors.count} JavaScript error(s)`) : null,
              report.horizontalOverflow ? h('span', { class: 'badge fail' }, 'Wider than the frame') : null,
              (report.unsupported || []).length ? h('span', { class: 'badge' }, 'Not drawn: ' + report.unsupported.map(u => `${u.kind} ×${u.count}`).join(', ')) : null),
            h('ul', { class: 'checklist', 'aria-label': 'UI checks' }, (report.checklist || []).map(c => h('li', {},
              h('span', { class: 'badge ' + (c.ok ? 'ok' : 'warn') }, c.ok ? 'OK' : 'Check'), h('span', {}, c.name, c.detail ? h('span', { class: 'detail' }, ' — ' + c.detail) : null)))),
            cli(r.cli));
        };
        render.addEventListener('click', () => go(false));
        shot.addEventListener('click', () => go(true));
        cards.push(() => go(false));
        add(gallery, h('section', { class: 'card', 'aria-label': el.name }, h('div', { class: 'card-head' },
          h('div', {}, h('h3', {}, el.name), h('span', { class: 'badge' }, el.kind)), h('div', { class: 'row' }, render, shot)), out));
      }
      // The first ones render at once and again when the theme or width changes (a preview takes well under a
      // second); the rest wait for a click. A newer round stops an older one.
      let round = 0;
      const auto = async () => { const mine = ++round; for (const go of cards.slice(0, 12)) { if (!gallery.isConnected || mine !== round) return; await go(); } };
      renderAll.addEventListener('click', async () => {
        renderAll.disabled = true;
        try { for (const go of cards) await go(); } finally { renderAll.disabled = false; }
      });
      body.replaceChildren(
        h('div', { class: 'card' }, h('div', { class: 'row' },
          segmented('Theme', [['light', 'Light'], ['dark', 'Dark']], theme, v => { theme = v; store.set('preview-theme', v); auto(); }),
          segmented('Width', [[1200, 'Desktop 1200 px'], [390, 'Phone 390 px']], width, v => { width = v; store.set('preview-width', v); auto(); }),
          h('span', { class: 'grow' }), renderAll),
          h('p', { class: 'hint' }, 'The preview does not load OutSystems UI, so its classes look plain. Judge your own CSS, texts and layout.'), browserNote),
        gallery);
      auto();
    }
    if (state.module) picker.go();
  });

  // 4. Prompt builder
  page('prompt', 'Agents', 'Prompt builder', 'Write a clear task for an AI agent: the module, what to build, the exact elements to use and what it may not do.', async main => {
    const saved = store.get('prompt', { task: '', elements: [], noInstall: true, noPublish: true, preview: false, validate: true });
    const moduleInput = h('input', { type: 'text', id: 'p-module', class: 'code', value: state.module, placeholder: 'C:\\Work\\Orders.oml', spellcheck: 'false' });
    const taskInput = h('textarea', { id: 'p-task', placeholder: 'Add an entity Invoice (Number Text 20 mandatory, Amount Decimal, IssuedOn Date) with Invoice_CreateOrUpdate and Invoice_Delete server actions, and a Reactive list screen InvoiceList.' });
    taskInput.value = saved.task;
    const elements = [...saved.elements];
    const chips = h('div', { class: 'signature', 'aria-live': 'polite' });
    const findName = h('input', { type: 'search', id: 'p-find', class: 'code', placeholder: 'Customer_Validate or Customer*', spellcheck: 'false' });
    const findIn = h('input', { type: 'text', id: 'p-in', class: 'code', placeholder: 'C:\\Work\\Producers', value: store.get('producers', ''), spellcheck: 'false' });
    const findButton = h('button', { class: 'button', type: 'button' }, 'Find');
    const findOut = h('div');
    const checks = {
      validate: ['Stage the result and validate it', saved.validate],
      noInstall: ['Do not install', saved.noInstall],
      noPublish: ['Do not publish', saved.noPublish],
      preview: ['Use preview for UI: light, dark and phone width', saved.preview],
    };
    const boxes = Object.fromEntries(Object.entries(checks).map(([k, [, v]]) => [k, h('input', { type: 'checkbox', id: 'p-' + k, checked: v })]));
    const output = h('textarea', { id: 'p-out', class: 'code', readonly: true, rows: 10 });

    function build() {
      const module = moduleInput.value.trim();
      const parts = [];
      parts.push(module ? `Module: ${module}.` : 'Module: (type the module file).');
      const taskText = taskInput.value.trim();
      if (taskText) parts.push(/[.!?]$/.test(taskText) ? taskText : taskText + '.');
      if (elements.length) {
        parts.push(`Use these elements from other modules: ${elements.map(e => e.name).join(', ')}.`);
        const files = [...new Set(elements.map(e => e.file).filter(f => f && f.toLowerCase() !== module.toLowerCase()))];
        if (files.length) parts.push(`The producer module file${files.length > 1 ? 's are' : ' is'} ${files.join(', ')}.`);
      }
      if (boxes.preview.checked) parts.push('Preview the UI in light and dark and at phone width, follow the UI checklist, and send me the screenshots.');
      if (boxes.validate.checked) parts.push('Stage the result and validate it.');
      const not = [boxes.noInstall.checked && 'install', boxes.noPublish.checked && 'publish'].filter(Boolean);
      if (not.length) parts.push(`Do not ${not.join(' or ')}.`);
      output.value = parts.join(' ');
      store.set('prompt', { task: taskInput.value, elements, ...Object.fromEntries(Object.entries(boxes).map(([k, b]) => [k, b.checked])) });
      chips.replaceChildren(...elements.map((e, i) => h('span', { class: 'chip' }, e.name,
        h('button', { type: 'button', 'aria-label': 'Remove ' + e.name, onclick: () => { elements.splice(i, 1); build(); } }, '×'))));
      if (!elements.length) add(chips, h('span', { class: 'muted small' }, 'No elements yet. Find them below and add them, so the names are exact.'));
    }
    [moduleInput, taskInput].forEach(i => i.addEventListener('input', build));
    Object.values(boxes).forEach(b => b.addEventListener('change', build));

    const runFind = async () => {
      const name = findName.value.trim();
      if (!name) { findName.focus(); return; }
      store.set('producers', findIn.value.trim());
      const folders = findIn.value.split(';').map(s => s.trim()).filter(Boolean);
      const module = moduleInput.value.trim().replace(/^"(.*)"$/, '$1');
      const data = await task(findButton, findOut, 'Searching…', async () => need(await api('find', { name, path: module || undefined, in: folders })));
      if (!data) return;
      const matches = ((data.output && data.output.matches) || []).filter(m => m.source !== 'this module');
      add(findOut, matches.length === 0 ? h('p', { class: 'muted' }, 'No element with that name in referenced or producer modules.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('tbody', {}, matches.map(m => h('tr', {},
          h('td', {}, h('strong', {}, m.qualified), h('div', { class: 'tiny muted' }, `${m.kind} · ${m.source}`)),
          h('td', { class: 'num' }, h('button', { class: 'button small', type: 'button', onclick: () => {
            if (!elements.some(e => e.name === m.qualified)) elements.push({ name: m.qualified, file: m.source === 'producer file' ? m.file : null });
            build(); toast('Added ' + m.qualified);
          } }, 'Add'))))))), cli(data.cli));
    };
    findButton.addEventListener('click', runFind);
    findName.addEventListener('keydown', e => { if (e.key === 'Enter') runFind(); });

    add(main, h('div', { class: 'grid-2' },
      h('section', { class: 'card', 'aria-label': 'Task details' },
        h('div', { class: 'field' }, h('label', { for: 'p-module' }, 'Module file'), moduleInput),
        h('div', { class: 'field' }, h('label', { for: 'p-task' }, 'What should the agent build or change?'), taskInput,
          h('p', { class: 'hint' }, 'One task per message works best. Split big features: data model first, then logic, then screens.')),
        h('div', { class: 'field' }, h('span', { class: 'label', id: 'p-el-label' }, h('strong', { class: 'small' }, 'Elements from other modules')), chips),
        h('div', { class: 'grid-2' },
          h('div', { class: 'field' }, h('label', { for: 'p-find' }, 'Find an element'), findName),
          h('div', { class: 'field' }, h('label', { for: 'p-in' }, 'Producer folders (optional)'), findIn)),
        findButton, findOut,
        h('fieldset', { class: 'field' }, h('legend', { class: 'sr-only' }, 'Limits for the agent'),
          h('p', {}, h('strong', { class: 'small' }, 'Limits')),
          Object.entries(checks).map(([k, [text]]) => h('label', { class: 'check', for: 'p-' + k }, boxes[k], text)))),
      h('section', { class: 'card', 'aria-label': 'Task for the agent' },
        h('div', { class: 'card-head' }, h('h2', {}, 'Task for the agent'), copyButton(() => output.value, 'Copy task', 'Task')),
        h('label', { for: 'p-out', class: 'sr-only' }, 'Task text'), output,
        h('p', { class: 'hint' }, 'Paste it into your agent. Agents ask you before anything that changes a real module or an environment.'))));
    build();
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
      const toggle = h('button', { class: 'button small', type: 'button', 'aria-expanded': 'false' }, 'Show JSON');
      toggle.addEventListener('click', () => { code.hidden = !code.hidden; toggle.textContent = code.hidden ? 'Show JSON' : 'Hide JSON'; toggle.setAttribute('aria-expanded', String(!code.hidden)); });
      const card = h('section', { class: 'card', 'aria-label': title },
        h('h2', {}, title), h('p', { class: 'tiny mono muted' }, r.file), h('p', {}, r.description),
        h('div', { class: 'row' }, copyButton(r.text, 'Copy spec', 'Spec'), copyButton(prompt, 'Copy prompt', 'Prompt'), toggle), code,
        cli(`oml apply ${state.module || 'C:\\Work\\Orders.oml'} skill\\recipes\\${r.file} ${(state.module || 'C:\\Work\\Orders.oml').replace(/\.oml$/i, '')}.staged.oml`));
      return [(r.file + ' ' + r.description).toLowerCase(), card];
    });
    cards.forEach(([, c]) => add(list, c));
    filter.addEventListener('input', () => { const q = filter.value.toLowerCase(); cards.forEach(([t, c]) => { c.hidden = q && !t.includes(q); }); });
    add(out, h('div', { class: 'field' }, filter), list);
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
      } else {
        const to = h('select', { id: 'img-to' }, [['', 'Same as input'], ['webp', 'WebP'], ['png', 'PNG'], ['jpeg', 'JPEG'], ['svg', 'SVG (from SVG only)']].map(([v, t]) => h('option', { value: v }, t)));
        to.value = store.get('img-to', 'webp');
        const maxWidth = h('input', { type: 'number', id: 'img-width', min: '1', max: '100000', placeholder: 'No limit', value: store.get('img-width', '') });
        const quality = h('input', { type: 'range', id: 'img-quality', min: '1', max: '100', value: store.get('img-quality', 85) });
        const qualityText = h('span', { class: 'mono small' }, quality.value);
        quality.addEventListener('input', () => { qualityText.textContent = quality.value; });
        const options = () => {
          store.set('img-to', to.value); store.set('img-width', maxWidth.value); store.set('img-quality', Number(quality.value));
          return { to: to.value || undefined, maxWidth: maxWidth.value ? Number(maxWidth.value) : undefined, quality: Number(quality.value) };
        };
        body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Images'),
          h('p', { class: 'muted small' }, 'PNG, JPEG, WebP, GIF, BMP, ICO and SVG. Images only get smaller, never larger. Metadata is removed.'),
          h('div', { class: 'grid-3' },
            h('div', { class: 'field' }, h('label', { for: 'img-to' }, 'Format'), to),
            h('div', { class: 'field' }, h('label', { for: 'img-width' }, 'Max width (px)'), maxWidth),
            h('div', { class: 'field' }, h('label', { for: 'img-quality' }, 'Quality (JPEG, WebP) ', qualityText), quality)),
          dropZone('.png,.jpg,.jpeg,.gif,.bmp,.webp,.ico,.svg', 'Drop images', files => convert('convert/images', files, options(), out)), out));
      }
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
      add(out, 
        done.length > 1 ? h('div', { class: 'row' }, h('span', { class: 'badge ok' }, `${done.length} files: ${bytes(totalIn)} → ${bytes(totalOut)}, ${saved(totalIn, totalOut)}`),
          h('button', { class: 'button small', type: 'button', onclick: () => download(fonts ? 'fonts-woff2.zip' : 'images-optimized.zip', data.zip, 'application/zip', true) }, icon('download'), 'Download all (zip)')) : null,
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, [fonts ? '' : 'Result', 'File', 'Before', 'After', 'Saved', ''].map((t, i) => h('th', { class: i >= 2 && i <= 4 ? 'num' : null }, t)))),
          h('tbody', {}, rows.map(r => {
            let thumb = null;
            if (!fonts && r.data) {
              const type = { webp: 'image/webp', png: 'image/png', jpeg: 'image/jpeg', svg: 'image/svg+xml' }[r.outputFormat] || 'application/octet-stream';
              const url = blobUrl(r.data, type); onLeave(() => URL.revokeObjectURL(url));
              thumb = h('img', { class: 'thumb', src: url, alt: '' });
            }
            return h('tr', {},
              h('td', {}, thumb),
              h('td', {}, h('strong', {}, r.output || r.name), r.output ? h('div', { class: 'tiny muted' }, 'from ' + r.name, r.after ? ` · ${r.after.width}×${r.after.height}` : '') : null,
                r.error ? h('div', { class: 'small', role: 'alert' }, h('span', { class: 'saved-bad' }, 'Not converted: '), r.error) : null,
                r.note ? h('div', { class: 'small muted' }, r.note === 'already woff2' ? 'Already WOFF2: nothing to do.' : r.note) : null),
              h('td', { class: 'num' }, bytes(r.inputBytes)), h('td', { class: 'num' }, bytes(r.outputBytes)),
              h('td', { class: 'num' }, r.data ? h('span', { class: r.outputBytes <= r.inputBytes ? 'saved-good' : 'saved-bad' }, saved(r.inputBytes, r.outputBytes)) : ''),
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
    add(main, picker.el, body);

    function show(path, data) {
      const apis = (data.output && data.output.restServices) || [];
      if (!apis.length) { body.replaceChildren(h('div', { class: 'empty' }, 'This module exposes no REST API.')); return; }
      const out = h('div', { class: 'stack' });
      body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Exposed REST APIs'),
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['API', 'Methods', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, apis.map(a => {
            const b = h('button', { class: 'button small primary', type: 'button' }, 'Generate');
            b.addEventListener('click', () => generate(path, a.name, b, out));
            return h('tr', {}, h('td', {}, h('strong', {}, a.name), h('div', { class: 'tiny muted' }, a.baseUrl || '')),
              h('td', {}, (a.methods || []).map(m => h('div', { class: 'small mono' }, `${m.method} ${m.url}`))), h('td', { class: 'num' }, b));
          }))))), out);
    }

    async function generate(path, apiName, button, out) {
      const data = await task(button, out, `Generating OpenAPI for ${apiName}…`, async () => need(await api('openapi', { path, api: apiName })));
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
  page('architecture', 'Module', 'Architecture', 'Make one page with the applications, modules, layers, dependencies and findings of a folder of .oap or .oml files.', async main => {
    const folders = h('textarea', { id: 'arch-in', class: 'code', rows: 3, placeholder: 'C:\\Exports\\Payments', spellcheck: 'false' });
    folders.value = store.get('arch-in', '');
    const run = h('button', { class: 'button primary', type: 'button' }, 'Make the page');
    const out = h('div', { class: 'stack' });
    const show = data => {
      if (!data) return;
      const report = data.output || {};
      add(out, 
        h('div', { class: 'row' },
          report.modules !== undefined ? h('span', { class: 'badge' }, `${report.modules} modules`) : null,
          report.applications !== undefined ? h('span', { class: 'badge' }, `${report.applications} applications`) : null,
          report.findings ? h('span', { class: 'badge ' + (Object.values(report.findings).some(v => v > 0) ? 'warn' : 'ok') }, 'Findings: ' + Object.entries(report.findings).map(([k, v]) => `${v} ${k}`).join(', ')) : null,
          h('span', { class: 'grow' }),
          data.html ? h('button', { class: 'button small', type: 'button', onclick: () => download('architecture.html', data.html, 'text/html') }, icon('download'), 'Download HTML') : null,
          data.view ? h('a', { class: 'button small', href: '/view/' + data.view, target: '_blank', rel: 'noopener' }, 'Open in a new tab') : null),
        data.path ? h('p', { class: 'hint' }, 'Saved in ', h('code', { text: data.path })) : null,
        data.view ? frame(data.view, 'Architecture page', 1400, 1150) : null,
        cli(data.cli));
    };
    run.addEventListener('click', async () => {
      const list = folders.value.split(/\r?\n|;/).map(s => s.trim()).filter(Boolean);
      if (!list.length) { folders.focus(); return; }
      store.set('arch-in', folders.value);
      show(await task(run, out, 'Reading the modules… This can take a minute for many modules.', async () => need(await api('architecture', { in: list }))));
    });
    add(main, h('div', { class: 'grid-2' },
      h('section', { class: 'card' }, h('h2', {}, 'From a folder'),
        h('div', { class: 'field' }, h('label', { for: 'arch-in' }, 'Folders or files, one per line'), folders, h('p', { class: 'hint' }, 'Folders are searched with their subfolders for .oap and .oml files.')), run),
      h('section', { class: 'card' }, h('h2', {}, 'From files'),
        dropZone('.oap,.oml', 'Drop .oap or .oml files', async files => show(await task(null, out, 'Uploading and reading the modules…', async () => need(await api('architecture', { files: await readFiles(files) }))))))),
      out);
  });

  // 9. LifeTime
  page('lifetime', 'Team', 'LifeTime', 'Environments, applications and deployments, read only. Tagging and deploying stay in the CLI, where you confirm each step.', async main => {
    const lt = state.info && state.info.lifeTime;
    if (!lt || !lt.configured) {
      add(main, h('section', { class: 'card' }, h('h2', {}, 'Connect LifeTime'),
        h('p', {}, 'The dashboard reads LifeTime with a service account token. For your safety, the token is never typed into this page.'),
        h('ol', { class: 'doc' },
          h('li', {}, 'In LifeTime, open User Management > Service Accounts and create an account with read permissions. Copy its token.'),
          h('li', {}, 'Close this dashboard (Ctrl+C in its window).'),
          h('li', {}, 'In PowerShell, set the two variables and start the dashboard again:')),
        cli('$env:OML_LIFETIME_HOST = "lifetime.example.com"'),
        cli('$env:OML_LIFETIME_TOKEN = Read-Host -MaskInput "LifeTime token"'),
        cli('oml dashboard'),
        h('p', { class: 'hint' }, 'The host is the LifeTime hostname without https://. The token stays in that PowerShell window only.')));
      return;
    }
    const envOut = h('div'), appOut = h('div'), statusOut = h('div');
    const envSelect = h('select', { id: 'lt-env' }, h('option', { value: '' }, 'All environments'));
    const keyInput = h('input', { type: 'text', id: 'lt-key', class: 'code', placeholder: '6f1c2a9e-3b7d-4c55-9e0a-1d2b3c4d5e6f', spellcheck: 'false' });
    const lookup = h('button', { class: 'button', type: 'button' }, 'Look up');
    add(main, 
      h('p', { class: 'muted small' }, 'Connected to ', h('code', { text: lt.host })),
      h('section', { class: 'card' }, h('h2', {}, 'Environments'), envOut),
      h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', {}, 'Applications'), h('div', { class: 'row' }, h('label', { for: 'lt-env', class: 'sr-only' }, 'Environment'), envSelect)), appOut),
      h('section', { class: 'card' }, h('h2', {}, 'Deployment status'),
        h('div', { class: 'field' }, h('label', { for: 'lt-key' }, 'Deployment key'), h('div', { class: 'row' }, h('div', { class: 'grow' }, keyInput), lookup)), statusOut));

    const envs = await task(null, envOut, 'Reading environments…', async () => need(await api('lt/environments')));
    if (envs) {
      const list = Array.isArray(envs.output) ? envs.output : [];
      add(envOut, h('div', { class: 'links' }, list.map(e => h('div', { class: 'link-card' }, h('strong', {}, e.Name), h('span', {}, [e.EnvironmentType, e.Key].filter(Boolean).join(' · '))))), cli(envs.cli));
      list.forEach(e => add(envSelect, h('option', { value: e.Name }, e.Name)));
    }
    const loadApps = async () => {
      const env = envSelect.value;
      const data = await task(envSelect, appOut, 'Reading applications… LifeTime can take some seconds.', async () => need(await api('lt/apps' + (env ? '?env=' + encodeURIComponent(env) : ''))));
      if (!data) return;
      const apps = Array.isArray(data.output) ? data.output : [];
      const rows = apps.flatMap(a => (a.environments || []).length ? a.environments.map((e, i) => [i === 0 ? a.name : '', e.environment, e.version, e.isModified]) : [[a.name, '', '', null]]);
      add(appOut, apps.length === 0 ? h('p', { class: 'muted' }, 'No applications.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Application', 'Environment', 'Version (tag)', 'Changed since tag'].map(t => h('th', {}, t)))),
          h('tbody', {}, rows.map(([name, env, version, modified]) => h('tr', {}, h('td', {}, h('strong', {}, name)), h('td', {}, env || ''), h('td', { class: 'mono small' }, version || '—'),
            h('td', {}, modified === true ? h('span', { class: 'badge warn' }, 'Yes, not tagged') : modified === false ? h('span', { class: 'badge ok' }, 'No') : '')))))),
        cli(data.cli));
    };
    envSelect.addEventListener('change', loadApps);
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

  // 12. Rules and checklist
  page('rules', 'Learn', 'Rules and checklist', 'The production rules agents follow, and the checklist for every screen and block.', async main => {
    let which = store.get('rules-tab', 'rules');
    const tabs = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Document' });
    const out = h('div', { class: 'card' });
    const load = async () => {
      const data = await task(null, out, 'Loading…', () => api(which === 'rules' ? 'doc/rules' : 'doc/checklist'));
      if (data) add(out, markdown(data.markdown));
    };
    for (const [value, label] of [['rules', 'Production rules'], ['checklist', 'UI checklist']]) {
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

  async function start() {
    buildNav();
    const toggle = document.getElementById('theme-toggle');
    const label = () => toggle.setAttribute('aria-label', `Switch to ${root.dataset.theme === 'dark' ? 'light' : 'dark'} theme`);
    label();
    toggle.addEventListener('click', () => {
      root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
      try { localStorage.setItem('oml-theme', root.dataset.theme); } catch { /* not remembered */ }
      label();
    });
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
  window.omlUi = { h, add, icon, copy, copyButton, cli, download, api, need, task, errorBox, colorCode, toast, store, ApiError };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
