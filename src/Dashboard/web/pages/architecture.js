import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, reportView, dropZone, readFiles } from '../core/ui.js';
import { workset } from '../core/workset.js';

const lines = text => String(text || '').split(/\r?\n|;/).map(s => s.trim()).filter(Boolean);

export default {
  id: 'architecture', group: 'Module', title: 'Architecture', icon: 'architecture',
  description: 'Make one page with the applications, modules, layers, dependencies and findings of a folder of .oap or .oml files.',
  render(main) {
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
  },
};
