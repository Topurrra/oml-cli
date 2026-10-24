import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, toast, errorBox, bytes, fileName, downloadLink, ltDownload, pullWarning, dropZone, readFiles, CONNECT_NOTE } from '../core/ui.js';
import { onLeave } from '../core/lifecycle.js';
import { state } from '../core/state.js';
import { workset } from '../core/workset.js';

export default {
  id: 'modules', group: 'Start', title: 'Get modules', icon: 'modules',
  description: 'Choose the modules you work on: copy them from Service Studio, list a folder, open a package, drop files or get them from LifeTime.',
  async render(main) {
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
  },
};
