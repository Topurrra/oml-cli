import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, reportView, dropZone, readFiles, toast } from '../core/ui.js';
import { workset } from '../core/workset.js';
import { onLeave } from '../core/lifecycle.js';
import { section, tabs, actionButton } from '../core/table.js';

const lines = text => String(text || '').split(/\r?\n|;/).map(s => s.trim()).filter(Boolean);

// Architecture: options (folded, first: a drop makes the page at once), where the modules come from (the working set,
// folders, or dropped files), then the page.
// The result takes the full width, with its downloads and an Open in a new tab button in the viewer.
export default {
  id: 'architecture', group: 'Module', title: 'Architecture', icon: 'architecture',
  description: 'One page with the applications, modules, layers, dependencies and findings of the working set or a folder of .oap or .oml files.',
  render(main) {
    const out = h('div', { class: 'stack' });
    const apps = h('input', { type: 'text', id: 'arch-apps', placeholder: 'Only these applications: Payments; Orders (empty: all)', value: store.get('arch-apps', '') });
    const layers = h('input', { type: 'text', id: 'arch-layers', class: 'code', placeholder: 'Layers file (.json), such as { "Shop*": "Core" } (empty: oml guesses)', value: store.get('arch-layers', ''), spellcheck: 'false' });
    const json = h('input', { type: 'checkbox', id: 'arch-json', checked: store.get('arch-json', false) });
    const options = () => {
      store.set('arch-apps', apps.value); store.set('arch-layers', layers.value.trim()); store.set('arch-json', json.checked);
      return { apps: lines(apps.value), layers: layers.value.trim().replace(/^"(.*)"$/, '$1') || undefined, json: json.checked };
    };
    const show = data => { if (data) { add(out, reportView(data, 'Architecture page', 'architecture')); out.scrollIntoView({ block: 'start' }); } };
    let busy = false;
    const make = async (button, body, label) => {
      if (busy) { toast('A page is being made. Wait for it to finish.'); return; }
      busy = true;
      try { show(await task(button, out, label, async () => need(await api('architecture', { ...body, ...options() })))); }
      finally { busy = false; }
    };

    // The modules.
    const fromSet = box => {
      const list = h('ul', { class: 'checklist' });
      const run = actionButton('architecture', 'Make the page', () => {
        const paths = workset.list().map(m => m.path);
        if (!paths.length) { toast('The working set is empty. Add modules on Get modules.'); return; }
        make(run, { in: paths }, `Reading ${paths.length} modules… This can take a minute for many modules.`);
      }, { primary: true });
      const draw = () => {
        const set = workset.list();
        list.replaceChildren(...(set.length ? set.map(m => h('li', {}, h('strong', {}, m.name), ' ', h('span', { class: 'tiny muted mono', text: m.path }))) : [h('li', { class: 'muted' }, 'The working set is empty. ', h('a', { href: '#/modules' }, 'Get modules'), ' first.')]));
        run.disabled = !set.length;
      };
      onLeave(workset.onChange(draw));
      draw();
      add(box, list, h('div', { class: 'toolbar' }, run));
    };
    const fromFolder = box => {
      const folders = h('textarea', { id: 'arch-in', class: 'code', rows: 3, placeholder: 'C:\\Exports\\Payments (one folder or file per line; subfolders are searched too)', spellcheck: 'false' });
      folders.value = store.get('arch-in', '');
      const run = actionButton('architecture', 'Make the page', () => {
        const list = lines(folders.value);
        if (!list.length) { folders.focus(); return; }
        store.set('arch-in', folders.value);
        make(run, { in: list }, 'Reading the modules… This can take a minute for many modules.');
      }, { primary: true });
      add(box, h('label', { for: 'arch-in', class: 'sr-only' }, 'Folders or files, one per line'), folders, h('div', { class: 'toolbar' }, run));
    };
    const fromFiles = box => add(box, dropZone('.oap,.oml', 'Drop .oap or .oml files: the page is made at once', async files => {
      make(null, { files: await readFiles(files, ['.oap', '.oml']) }, 'Uploading and reading the modules…');
    }));

    const source = section('Modules', { key: 'arch-source' });
    add(source.body, tabs([
      { id: 'set', label: 'Working set', icon: 'modules', render: fromSet },
      { id: 'folder', label: 'Folders', icon: 'folder', render: fromFolder },
      { id: 'files', label: 'Drop files', icon: 'upload', render: fromFiles },
    ], { key: 'arch-source', label: 'Where the modules come from' }).el);

    // Options first, folded: a drop makes the page at once, with these options.
    const opts = section('Options: applications, layers, JSON', { key: 'arch-options', open: false });
    add(opts.body, h('div', { class: 'grid-2' },
      h('div', { class: 'field' }, h('label', { for: 'arch-apps' }, 'Applications (--app)'), apps),
      h('div', { class: 'field' }, h('label', { for: 'arch-layers' }, 'Layers file (--layers)'), layers)),
      h('label', { class: 'check', for: 'arch-json' }, json, 'Also save the data as JSON (--json)'));

    add(main, opts.el, source.el, out);
  },
};
