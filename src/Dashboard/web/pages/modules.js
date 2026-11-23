import { h, add, icon } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, toast, errorBox, bytes, fileName, ltDownload, pullWarning, dropZone, readFiles, CONNECT_NOTE } from '../core/ui.js';
import { onLeave } from '../core/lifecycle.js';
import { state } from '../core/state.js';
import { workset } from '../core/workset.js';
import { dataTable, section, tabs, actionButton, iconButton } from '../core/table.js';
import { serverModules } from '../core/server-modules.js';

const roleLabel = { change: 'Change', read: 'Read only' };

export default {
  id: 'modules', group: 'Start', title: 'Get modules', icon: 'modules',
  description: 'Choose the modules you work on: from Service Studio, the platform server, a folder, a package, dropped files or LifeTime.',
  async render(main) {
    // The working set: every page offers these modules.
    const setSec = section('Working set', { key: 'modules-set' });
    const setTable = dataTable({
      caption: 'Working set', empty: 'No modules yet. Get them below. Every page offers the modules you add here.', searchPlaceholder: 'Search the working set',
      columns: [
        { key: 'name', label: 'Module', width: '38%', render: m => [h('strong', {}, m.name), h('div', { class: 'tiny muted mono', text: m.path })] },
        { key: 'role', label: 'Role', width: '18%', render: m => {
          const id = 'role-' + Math.random().toString(36).slice(2);
          const role = h('select', { id, onchange: e => workset.setRole(m.path, e.target.value) }, Object.entries(roleLabel).map(([v, t]) => h('option', { value: v }, t)));
          role.value = m.role;
          return [h('label', { for: id, class: 'sr-only' }, 'Role of ' + m.name), role];
        } },
        { key: 'source', label: 'Where it came from', width: '30%', render: m => h('span', { class: 'small' }, workset.sourceLine(m)), value: m => workset.sourceLine(m) },
        { key: 'actions', label: '', width: '14%', sortable: false, render: m => h('div', { class: 'row-actions' },
          h('a', { class: 'icon-button small', href: '#/explorer?module=' + encodeURIComponent(m.path), 'aria-label': 'Explore ' + m.name, title: 'Explore ' + m.name }, icon('explorer')),
          iconButton('trash', 'Remove ' + m.name + ' from the working set', () => workset.remove(m.path), { small: true })) },
      ],
    });
    const clear = iconButton('trash', 'Clear the working set', () => { if (confirm('Remove every module from the working set? The files stay.')) workset.clear(); }, { small: true });
    const prompt = h('a', { class: 'icon-button small', href: '#/agents', 'aria-label': 'Write a task for an agent', title: 'Write a task for an agent' }, icon('prompt'));
    add(setSec.head, prompt, clear);
    add(setSec.body, setTable.el, h('p', { class: 'hint' }, 'Change: the agent may change this module. Read only: the agent reads it as a producer, with --in. Removing a module here keeps its file.'));
    const drawSet = () => { const list = workset.list(); setTable.update(list); setSec.count(list.length); clear.disabled = !list.length; };
    onLeave(workset.onChange(drawSet));
    drawSet();

    const getSec = section('Get modules', { key: 'modules-get' });
    const sources = [
      { id: 'studio', label: 'Service Studio', icon: 'modules', render: studioTab },
      { id: 'server', label: 'Platform server', icon: 'server', render: serverTab },
      { id: 'folder', label: 'Folder', icon: 'folder', render: folderTab },
      { id: 'drop', label: 'Drop files', icon: 'upload', render: dropTab },
      { id: 'recent', label: 'Recent', icon: 'activity', render: recentTab },
      { id: 'lifetime', label: 'LifeTime', icon: 'lifetime', render: lifetimeTab },
    ];
    add(getSec.body, tabs(sources, { key: 'modules-source', label: 'Where the modules come from' }).el);
    add(main, setSec.el, getSec.el);
  },
};

// A list of modules with a checkbox each, and one button that adds the checked ones.
function pickList(items, source, label) {
  const boxes = items.map(item => {
    const id = 'pick-' + Math.random().toString(36).slice(2);
    const usable = item.path && /\.oml$/i.test(item.path);
    return { item, box: h('input', { type: 'checkbox', id, checked: usable, disabled: !usable }), id, usable };
  });
  const addButton = actionButton('plus', label || 'Add to the working set', () => {
    const chosen = boxes.filter(b => b.box.checked);
    if (!chosen.length) { toast('Check at least one module.'); return; }
    chosen.forEach(b => workset.add(b.item.path, typeof source === 'function' ? source(b.item) : source));
    toast(`Added ${chosen.length} module${chosen.length > 1 ? 's' : ''}`);
  }, { primary: true, small: true });
  return h('div', { class: 'stack' },
    h('ul', { class: 'checklist', 'aria-label': 'Modules' }, boxes.map(b => h('li', {},
      h('label', { class: 'check', for: b.id }, b.box, h('span', {}, h('strong', {}, b.item.name), ' ', h('span', { class: 'tiny muted' }, b.item.note || ''),
        !b.usable ? h('span', { class: 'tiny muted' }, ' (not a module file you can add)') : null))))),
    addButton);
}

// An .oap: `app list` on a click, then `app extract` into a new work folder on another click.
function packageView(file, out) {
  const list = actionButton('eye', 'Show modules', async () => {
    const data = await task(list, out, `Reading ${fileName(file)}…`, async () => need(await api('modules/app', { path: file })));
    if (!data) return;
    const modules = (data.output && data.output.modules) || [];
    const apps = (data.output && data.output.applications) || [];
    const extracted = h('div');
    const extract = actionButton('download', 'Extract the modules', async () => {
      const r = await task(extract, extracted, 'Extracting…', async () => need(await api('modules/extract', { path: file })));
      if (!r) return;
      const files = ((r.output && r.output.extracted) || []).map(f => ({ name: f.name, path: f.path, note: bytes(f.size) }));
      add(extracted, h('p', { class: 'small' }, 'Saved in ', h('code', { text: r.folder })), pickList(files, { kind: 'package', file }), cli(r.cli));
    }, { primary: true, small: true });
    add(out, h('p', {}, apps.map(a => a.name).join(', ') || fileName(file), ': ', `${modules.length} module${modules.length === 1 ? '' : 's'}.`),
      h('ul', { class: 'small' }, modules.map(m => h('li', {}, h('strong', {}, m.name), ` · ${m.kind} · ${bytes(m.size)}`))),
      extract, extracted, cli(data.cli));
  }, { small: true, aria: 'Show the modules of ' + fileName(file) });
  return list;
}

function studioTab(body) {
  const out = h('div', { class: 'stack', 'aria-live': 'polite' });
  const show = actionButton('search', 'Show open modules', async () => {
    const data = await task(show, out, 'Asking Service Studio for its open modules…', async () => need(await api('studio')));
    if (!data) { out.prepend(h('div', { class: 'message info' }, 'Start Service Studio\'s MCP server first: in Service Studio, open Edit > MCP Server > Start MCP server.')); return; }
    const apps = (data.output && data.output.apps) || [];
    if (!apps.some(a => a.isOpen)) { add(out, h('p', { class: 'muted' }, 'Service Studio answers, but no module is open.'), cli(data.cli)); return; }
    const rows = apps.map(a => ({ a, box: h('input', { type: 'checkbox', id: 'ss-' + Math.random().toString(36).slice(2), checked: a.isOpen && !a.isReference, disabled: !a.isOpen }) }));
    const result = h('div', { class: 'stack' });
    const copyButton = actionButton('download', 'Copy the checked modules', async () => {
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
    }, { primary: true });
    add(out, h('ul', { class: 'checklist', 'aria-label': 'Modules in Service Studio' }, rows.map(({ a, box }) => h('li', {},
      h('label', { class: 'check', for: box.id }, box, h('strong', {}, a.name), ' ',
        a.isOpen ? h('span', { class: 'badge ok' }, 'Open') : h('span', { class: 'badge' }, 'Not open'), a.isReference ? h('span', { class: 'badge' }, 'Reference') : null)))),
      h('p', { class: 'hint' }, 'Only open modules can be copied. Each copy goes to a new folder; the module in Service Studio stays as it is.'),
      copyButton, result, cli(data.cli));
  }, { primary: true });
  add(body, h('p', { class: 'muted small' }, 'Copies the modules open in Service Studio to new files. The open modules do not change. Nothing is copied until you click.'),
    h('div', { class: 'toolbar' }, show), h('p', { class: 'hint' }, CONNECT_NOTE), out);
}

// The platform server (REST): every module and version, Service Studio closed. Save adds the file to the working set.
async function serverTab(body) {
  const info = await api('server/history').catch(() => null);
  const server = (info && info.server) || {};
  add(body, h('p', { class: 'muted small' }, 'Lists every module on the platform server with Service Studio closed. Open a module, then save the version you want to a new file: it joins the working set.'),
    serverModules(server, { secondary: false }).el);
}

function folderTab(body) {
  const out = h('div', { class: 'stack', 'aria-live': 'polite' });
  const folder = h('input', { type: 'text', id: 'm-folder', class: 'code', value: store.get('modules-folder', ''), placeholder: 'C:\\Work\\Modules', spellcheck: 'false' });
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
  const list = iconButton('search', 'List the .oml and .oap files', run, { primary: true });
  folder.addEventListener('keydown', e => { if (e.key === 'Enter') run(); });
  add(body, h('div', { class: 'field' }, h('label', { for: 'm-folder' }, 'Folder with .oml or .oap files'), h('div', { class: 'toolbar' }, h('div', { class: 'grow' }, folder), list),
    h('p', { class: 'hint' }, 'Subfolders are listed too. A package (.oap) shows its modules; extracting them makes new files and never changes the package.')), out);
}

function dropTab(body) {
  const out = h('div', { class: 'stack', 'aria-live': 'polite' });
  let busy = false;
  add(body, h('p', { class: 'muted small' }, 'Dropped files are copied to a new folder in the work folder and stay there.'),
    dropZone('.oml,.oap', 'Drop .oml or .oap files', async files => {
      if (busy) { toast('Wait for the files before to finish.'); return; }
      busy = true;
      try {
        const data = await task(null, out, 'Uploading…', async () => api('modules/upload', { files: await readFiles(files, ['.oml', '.oap']) }));
        if (!data) return;
        const omls = (data.files || []).filter(f => f.kind === 'oml');
        omls.forEach(f => workset.add(f.path, { kind: 'drop', file: f.name }));
        add(out, h('p', { class: 'small' }, 'Saved in ', h('code', { text: data.folder })),
          omls.length ? h('div', { class: 'message' }, `Added ${omls.length} module${omls.length > 1 ? 's' : ''} to the working set: ${omls.map(f => f.name).join(', ')}.`) : null,
          (data.files || []).filter(f => f.kind === 'oap').map(f => { const o = h('div', { class: 'stack' }); return h('div', { class: 'panel' }, h('div', { class: 'row' }, h('strong', {}, f.name), packageView(f.path, o)), o); }));
      } finally { busy = false; }
    }), out);
}

function recentTab(body) {
  const inSet = new Set(workset.list().map(m => m.path.toLowerCase()));
  const sourcesSeen = store.get('sources', {});
  const recent = store.get('recent', []).filter(p => !inSet.has(p.toLowerCase()));
  add(body, recent.length === 0 ? h('p', { class: 'muted' }, 'No other recent modules. Modules you open or add appear here.') :
    pickList(recent.map(p => ({ name: fileName(p), path: p, note: p })), item => sourcesSeen[item.path.toLowerCase()] || { kind: 'path' }));
}

function lifetimeTab(body) {
  const out = h('div', { class: 'stack', 'aria-live': 'polite' });
  const lt = state.info && state.info.lifeTime;
  if (!lt || !lt.configured) { add(body, h('p', {}, 'LifeTime is not connected. ', h('a', { href: '#/lifetime' }, 'See how to connect it'), '.')); return; }
  const env = h('select', { id: 'm-lt-env', 'aria-label': 'Environment' });
  const listApps = actionButton('search', 'List applications', async () => {
    const environment = env.value;
    if (!environment) { env.focus(); return; }
    const data = await task(listApps, out, 'Reading applications… LifeTime can take some seconds.', async () => need(await api('lt/apps?env=' + encodeURIComponent(environment))));
    if (!data) return;
    const apps = (Array.isArray(data.output) ? data.output : []).map(a => ({ ...a, version: ((a.environments || [])[0] || {}).version || '' }));
    add(out, dataTable({
      caption: 'Applications in ' + environment, rows: apps, empty: 'No applications run in this environment.', searchPlaceholder: 'Search applications',
      columns: [{ key: 'name', label: 'Application', width: '60%', render: a => h('strong', {}, a.name) }, { key: 'version', label: 'Version', width: '25%', mono: true },
        { key: 'get', label: '', width: '15%', sortable: false, render: a => {
          const result = h('div', { class: 'stack' });
          const get = iconButton('download', 'Download and extract ' + a.name, () => ltDownload(a.name, environment, true, get, result, files =>
            pickList(files.map(f => ({ name: f.name, path: f.path, note: bytes(f.size) })), { kind: 'lifetime', app: a.name, env: environment })), { small: true });
          return h('div', {}, get, result);
        } }],
    }).el, cli(data.cli));
  }, { primary: true });
  add(body, h('p', { class: 'muted small' }, 'Downloads the package of an application as it runs in one environment (read only), then extracts its modules into the work folder.'),
    h('div', { class: 'toolbar' }, env, listApps), out);
  api('lt/environments').then(r => (Array.isArray(r.output) ? r.output : []).forEach(e => add(env, h('option', { value: e.Name }, e.Name)))).catch(e => out.replaceChildren(errorBox(e)));
}
