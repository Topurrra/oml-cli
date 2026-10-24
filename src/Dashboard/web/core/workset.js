import { h, add, icon } from './dom.js';
import { store } from './store.js';
import { state, remember, setModule } from './state.js';
import { api, need } from './api.js';
import { task, cli, toast, fileName, pullWarning, CONNECT_NOTE } from './ui.js';

// The working set: the modules a person works with now, from any source ("Get modules" page). Kept in the browser
// (store 'workset'). Pages read it through this singleton:
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
export const workset = (() => {
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

// The working set as buttons that fill a module field, or a link to Get modules when it is empty.
export function worksetQuick(onPick) {
  const set = workset.list();
  return set.length ? h('div', { class: 'row small', role: 'group', 'aria-label': 'Modules in the working set' }, h('span', { class: 'muted' }, 'Working set:'),
    set.map(m => h('button', { class: 'button small', type: 'button', 'aria-label': 'Open ' + m.name + ' from the working set', onclick: () => onPick(m.path) }, m.name)),
    h('a', { href: '#/modules', class: 'small' }, 'Get modules')) : h('p', { class: 'hint' }, h('a', { href: '#/modules' }, 'Get modules'), ' from Service Studio, a folder, a package or LifeTime.');
}

let pickerCount = 0;

// A module picker: type or paste a path, pick a recent one, or pull the module open in Service Studio.
export function modulePicker(onOpen, opts) {
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
export async function pullFlow(button, out, done) {
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
