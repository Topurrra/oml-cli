import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need, inspect } from '../core/api.js';
import { task, cli, copyButton, bytes, fileName, segmented, pathField, downloadLink } from '../core/ui.js';
import { colorCode } from '../core/markdown.js';
import { modulePicker } from '../core/workset.js';
import { state, setModule, hashParams } from '../core/state.js';

export default {
  id: 'explorer', group: 'Module', title: 'Module explorer', icon: 'explorer',
  description: 'Browse the entities, actions, screens and blocks of a module. Select one to read what it does.',
  async render(main) {
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
  },
};
