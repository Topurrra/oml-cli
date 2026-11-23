import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need, inspect } from '../core/api.js';
import { task, cli, copy, copyButton, fileName } from '../core/ui.js';
import { colorCode } from '../core/markdown.js';
import { modulePicker } from '../core/workset.js';
import { state, hashParams } from '../core/state.js';
import { dataTable, section, tabs, iconButton } from '../core/table.js';

const GROUPS = [
  ['entities', 'Entities'], ['staticEntities', 'Static entities'], ['structures', 'Structures'],
  ['serverActions', 'Server actions'], ['serviceActions', 'Service actions'], ['clientActions', 'Client actions'],
  ['screens', 'Screens'], ['blocks', 'Blocks'], ['restServices', 'Exposed REST APIs'], ['restClients', 'Consumed REST APIs'],
  ['timers', 'Timers'], ['processes', 'Processes'], ['roles', 'Roles'], ['siteProperties', 'Site properties'], ['exceptions', 'Exceptions'],
];

// The module explorer: one row to choose the module (it reopens the last one), then two panes: the elements on the
// left with one search box (names as you type; Enter searches inside the module), and the chosen element on the right.
export default {
  id: 'explorer', group: 'Module', title: 'Module explorer', icon: 'explorer',
  description: 'Browse the entities, actions, screens and blocks of a module. Select one to read what it does.',
  async render(main) {
    const body = h('div', { class: 'stack' });
    const picker = modulePicker(async (path, button, status) => {
      const data = await task(button, status, 'Reading the module… The first time can take some seconds.', () => inspect(path, true));
      if (data) showModule(path, data);
    }, { compact: true });
    add(main, picker.el, body, moreTools());

    function showModule(path, data) {
      const out = data.output || {};
      const detail = h('div', { class: 'card explorer-detail' }, h('div', { class: 'empty' }, 'Select an element on the left to read what it does.'));
      const search = h('input', { type: 'search', id: 'x-search', placeholder: 'Filter names · Enter: search inside', autocomplete: 'off', spellcheck: 'false' });
      const tree = h('div');
      let current = null;
      const items = [];
      const byName = new Map();
      for (const [key, title] of GROUPS) {
        let list = out[key] || [];
        if (key === 'restServices' || key === 'restClients') list = list.flatMap(a => [{ name: a.name }, ...(a.methods || []).map(m => ({ name: m.name, sub: `${m.method} ${m.url}` }))]);
        if (!list.length) continue;
        const ul = h('ul');
        for (const item of list) {
          const changed = item.lastModifiedBy ? `${item.lastModifiedBy}, ${String(item.lastModifiedDate || '').slice(0, 10)}` : '';
          const b = h('button', { type: 'button', title: [item.sub, changed].filter(Boolean).join(' · ') }, item.name, item.sub ? h('span', { class: 'tiny muted' }, '  ' + item.sub) : null,
            changed ? h('span', { class: 'tiny muted' }, ' · ' + changed) : null);
          b.addEventListener('click', () => { select(b); open(item.name, key, item); });
          const li = h('li', {}, b);
          items.push([item.name.toLowerCase(), li]);
          byName.set(item.name, () => b.click());
          add(ul, li);
        }
        add(tree, h('details', { open: list.length <= 40 }, h('summary', {}, title, h('span', { class: 'count' }, String(list.length))), ul));
      }
      const select = b => { if (current) current.removeAttribute('aria-current'); current = b; if (b) b.setAttribute('aria-current', 'true'); };
      search.addEventListener('input', () => {
        const q = search.value.trim().toLowerCase();
        for (const [name, li] of items) li.hidden = q && !name.includes(q);
        tree.querySelectorAll('details').forEach(d => { const any = [...d.querySelectorAll('li')].some(li => !li.hidden); d.hidden = !!q && !any; if (q) d.open = true; });
      });
      search.addEventListener('keydown', e => { if (e.key === 'Enter' && search.value.trim()) textSearch(search.value.trim()); });
      const searchInside = iconButton('search', 'Search inside the module (names, descriptions, expressions, SQL, labels)', () => search.value.trim() ? textSearch(search.value.trim()) : search.focus(), { small: true });

      // Header icons: change history (reads the module again with it), validate, read again.
      let history = store.get('history', false);
      const reread = async () => { const fresh = await task(null, detail, 'Reading the module again…', () => inspect(path, true)); if (fresh) showModule(path, fresh); };
      const historyButton = iconButton('activity', 'Show who changed each element and when', async () => { history = !history; store.set('history', history); await reread(); }, { small: true });
      historyButton.setAttribute('aria-pressed', String(history));
      const validateOut = h('div');
      const validate = iconButton('check', 'Validate the module (errors and warnings)', () => validateModule(path, validate, validateOut), { small: true });
      const reload = iconButton('refresh', 'Read the module again', reread, { small: true });

      body.replaceChildren(
        h('div', { class: 'explorer-head' }, h('div', {}, h('h2', {}, out.name || fileName(path)), h('p', { class: 'muted small' }, out.kind ? `${out.kind} module · ` : '', fileName(path), ` · ${items.length} elements`)),
          h('div', { class: 'toolbar' }, historyButton, validate, reload)),
        validateOut,
        h('div', { class: 'explorer' },
          h('div', { class: 'panel tree' }, h('div', { class: 'tree-search' }, h('label', { for: 'x-search', class: 'sr-only' }, 'Filter or search the elements'), search, searchInside),
            items.length ? tree : h('p', { class: 'muted' }, 'This module has no own elements yet.')),
          detail),
        cli(data.cli));
      // #/explorer?module=<path>&element=<Name>&tab=paths opens that element and tab: a link a person can keep.
      const linked = hashParams();
      if (linked.get('tab')) store.set('explorer-tab', linked.get('tab'));
      if (byName.has(linked.get('element'))) byName.get(linked.get('element'))();

      // One element: Describe, its object Paths (tree), its Style sheet (describe --css).
      function open(name, key, item) {
        const kind = (item && item.kind) || '';
        const list = [{ id: 'describe', label: 'Describe', render: pane => describe(name, pane) }, { id: 'paths', label: 'Paths', render: pane => pathsTab(name, pane) }];
        if (key === 'screens' || key === 'blocks' || /^(Screen|Block|WebBlock|WebScreen|Email|Theme)$/.test(kind)) list.push({ id: 'css', label: 'Style sheet', render: pane => cssTab(name, pane) });
        detail.replaceChildren(h('div', { class: 'card-head' }, h('h2', {}, name), copyButton(name, 'Copy name', 'Name')), tabs(list, { key: 'explorer-tab', label: 'Show' }).el);
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
        const depth = h('select', { id: 'x-depth', 'aria-label': 'Levels' }, [1, 2, 3, 4, 6].map(n => h('option', { value: n }, n + (n === 1 ? ' level' : ' levels'))));
        depth.value = String(store.get('tree-depth', 2));
        const out = h('div');
        const load = async () => {
          store.set('tree-depth', Number(depth.value));
          const data = await task(depth, out, `Reading the parts of ${name}…`, async () => need(await api('tree', { path, name, depth: Number(depth.value) })));
          if (data) add(out, h('ul', { class: 'ptree', 'aria-label': 'Parts of ' + name }, treeNode(data.output)),
            data.output.truncated ? h('p', { class: 'hint' }, String(data.output.truncated)) : null, cli(data.cli));
        };
        depth.addEventListener('change', load);
        add(pane, h('div', { class: 'toolbar' }, h('p', { class: 'hint grow' }, 'Exact paths for describe, tree and edits. Copy one into a spec.'), depth), out);
        await load();
      }
      function treeNode(node) {
        const children = h('ul');
        const more = node.more ? iconButton('plus', `Show the ${node.more} parts under ${node.path}`, async () => {
          const data = await task(more, children, 'Reading…', async () => need(await api('tree', { path, name: node.path, depth: 2 })));
          if (data) { more.remove(); add(children, (data.output.children || []).map(treeNode)); }
        }, { small: true }) : null;
        add(children, (node.children || []).map(treeNode));
        return h('li', {}, h('div', { class: 'row' }, h('span', { class: 'badge' }, node.kind || node.type), node.name ? h('strong', { class: 'small' }, node.name) : null,
          node.text ? h('code', { class: 'tiny', text: node.text.length > 60 ? node.text.slice(0, 60) + '…' : node.text }) : null,
          h('code', { class: 'tiny muted', text: node.path }), iconButton('copy', 'Copy the path ' + node.path, () => copy(node.path, 'Path'), { small: true }), more), children);
      }

      async function cssTab(name, pane) {
        const data = await task(null, pane, `Reading the style sheet of ${name}…`, async () => need(await api('describe', { path, name, css: true })));
        if (!data) return;
        const d = Array.isArray(data.output) ? data.output[0] : data.output || {};
        const css = d.styleSheet || '';
        add(pane, h('div', { class: 'toolbar' }, h('p', { class: 'hint grow' }, 'The whole style sheet. A styleSheet in a spec replaces all of it, so start from this text.'), css ? copyButton(css, 'Copy CSS', 'CSS') : null),
          css ? h('pre', { class: 'code-view' }, h('code', { text: css })) : h('p', { class: 'muted' }, 'This element has no style sheet.'), cli(data.cli));
      }

      // oml find <module> --text: the module's own elements ranked by name, description and the text inside them.
      async function textSearch(words) {
        select(null);
        const pane = h('div');
        detail.replaceChildren(h('div', { class: 'card-head' }, h('h2', {}, 'Search: ' + words)), pane);
        const data = await task(searchInside, pane, 'Searching the module…', async () => need(await api('find/text', { path, text: words, top: 30 })));
        if (!data) return;
        const hits = (data.output && data.output.hits) || [];
        add(pane, dataTable({
          caption: 'Search results', rows: hits, empty: 'Nothing in this module matches.', searchPlaceholder: 'Filter the results',
          columns: [
            { key: 'name', label: 'Element', width: '40%', render: x => [h('button', { type: 'button', class: 'link-button', onclick: () => open(x.path, null, { kind: x.kind }) }, x.name || x.kind), h('div', { class: 'tiny muted' }, x.kind)] },
            { key: 'why', label: 'Why', width: '46%', render: x => h('span', { class: 'small' }, x.why) },
            { key: 'score', label: 'Score', width: '14%', num: true },
          ],
          sort: { key: 'score', desc: true },
        }).el, data.output && data.output.searched ? h('p', { class: 'hint' }, `${data.output.searched} elements searched.`) : null, cli(data.cli));
      }
    }

    // oml validate --warnings: errors and warnings with their fix.
    async function validateModule(path, button, out) {
      const data = await task(button, out, 'Validating…', async () => need(await api('validate', { path }), [0, 1]));
      if (!data) return;
      const r = data.output || {};
      const list = [...(r.errors || []).map(x => ({ ...x, error: true })), ...(r.warnings || [])].map(x => {
        const sample = (x.samples || [])[0] || x;
        return { ...x, sample, level: x.error ? 'Error' : 'Warning', message: (sample.message || '') + (sample.detail ? ': ' + sample.detail : ''), owner: sample.owner || '' };
      });
      const s = section('Validation', { key: 'explorer-validation' });
      add(s.head, h('span', { class: 'badge ' + (r.errorCount ? 'fail' : 'ok') }, `${r.errorCount || 0} errors`), h('span', { class: 'badge ' + (r.warningCount ? 'warn' : 'ok') }, `${r.warningCount || 0} warnings`),
        iconButton('close', 'Close the validation', () => out.replaceChildren(), { small: true }));
      add(s.body, dataTable({
        caption: 'Errors and warnings', rows: list, empty: 'No errors and no warnings.', searchPlaceholder: 'Search messages',
        columns: [
          { key: 'id', label: 'Message', width: '44%', render: x => [h('span', { class: 'badge ' + (x.error ? 'fail' : 'warn') }, x.id || x.level), x.count > 1 ? h('span', { class: 'tiny muted' }, ` ×${x.count}`) : null, h('div', { class: 'small' }, x.message)] },
          { key: 'owner', label: 'Where', width: '22%', mono: true },
          { key: 'hint', label: 'Fix', width: '34%', render: x => [h('span', { class: 'small' }, x.hint || ''), x.id && x.hint ? h('div', { class: 'mono tiny muted' }, 'oml doc warning ' + x.id) : null] },
        ],
        filters: [{ key: 'level', label: 'Levels' }],
      }).el, cli(data.cli));
      add(out, s.el);
    }

    if (state.module && state.inspect[state.module]) showModule(state.module, state.inspect[state.module]);
    else if (state.module) picker.go();
  },
};

// Tools that do not need the open module: find a name in producer modules, and inspect an extension. Folded by default.
function moreTools() {
  const s = section('More: find in other modules, inspect an extension', { key: 'explorer-more', open: false });

  const findName = h('input', { type: 'search', id: 'find-name', class: 'code', placeholder: 'Name or pattern: Customer_Validate or Order*', spellcheck: 'false' });
  const findIn = h('input', { type: 'text', id: 'find-in', class: 'code', placeholder: 'Producer folders (optional), separated by ;', value: store.get('producers', ''), spellcheck: 'false' });
  const findOut = h('div');
  const runFind = async () => {
    const name = findName.value.trim();
    if (!name) { findName.focus(); return; }
    store.set('producers', findIn.value.trim());
    const folders = findIn.value.split(';').map(x => x.trim()).filter(Boolean);
    const data = await task(findButton, findOut, 'Searching…', async () => need(await api('find', { name, path: state.module || undefined, in: folders })));
    if (!data) return;
    const matches = (data.output && data.output.matches) || [];
    add(findOut, dataTable({
      caption: 'Matches', rows: matches, empty: 'Nothing found with that name.',
      columns: [
        { key: 'name', label: 'Name', width: '40%', render: m => [h('strong', {}, m.qualified || m.name), m.inputs ? h('div', { class: 'tiny muted' }, 'Inputs: ' + m.inputs.map(i => `${i.name} (${i.type})`).join(', ')) : null] },
        { key: 'kind', label: 'Kind', width: '18%' },
        { key: 'source', label: 'Where', width: '30%', render: m => [m.source, m.file && m.source !== 'this module' ? h('div', { class: 'tiny muted', text: fileName(m.file) }) : null] },
        { key: 'copy', label: '', width: '12%', sortable: false, render: m => iconButton('copy', 'Copy the name ' + (m.qualified || m.name), () => copy(m.qualified || m.name, 'Name'), { small: true }) },
      ],
    }).el, data.output && data.output.hint ? h('p', { class: 'hint' }, data.output.hint) : null, cli(data.cli));
  };
  const findButton = iconButton('search', 'Find the name', runFind, { primary: true });
  findName.addEventListener('keydown', e => { if (e.key === 'Enter') runFind(); });

  // `oml extension inspect`: the actions, entities and structures of an extension (.xif).
  const xif = h('input', { type: 'text', id: 'xif-path', class: 'code', value: store.get('xif-path', ''), placeholder: 'Extension file (.xif): C:\\Work\\PdfTools.xif', spellcheck: 'false' });
  const xifOut = h('div');
  const goXif = async () => {
    const file = xif.value.trim().replace(/^"(.*)"$/, '$1');
    if (!file) { xif.focus(); return; }
    store.set('xif-path', file);
    const data = await task(xifButton, xifOut, 'Reading the extension…', async () => need(await api('extension/inspect', { path: file })));
    if (!data) return;
    const x = data.output || {};
    const list = (title, entries, line) => (entries || []).length ? h('div', {}, h('h3', {}, `${title} (${entries.length})`), h('ul', { class: 'small' }, entries.map(i => h('li', {}, line(i))))) : null;
    const params = ps => (ps || []).map(p => `${p.name}: ${p.type}${p.direction ? ' (' + p.direction + ')' : ''}`).join(', ');
    add(xifOut, h('p', {}, h('strong', {}, x.name || fileName(file)), x.key ? h('span', { class: 'tiny muted' }, ' · ' + x.key) : null),
      list('Actions', x.actions, a => [h('strong', {}, a.name), ' ', h('span', { class: 'muted' }, params(a.parameters || a.inputs))]),
      list('Entities', x.entities, e => e.name),
      list('Structures', x.structures, st => st.name),
      h('details', {}, h('summary', {}, 'Show the JSON'), colorCode(JSON.stringify(x, null, 2), 'json')), cli(data.cli));
  };
  const xifButton = iconButton('eye', 'Inspect the extension', goXif, { primary: true });
  xif.addEventListener('keydown', e => { if (e.key === 'Enter') goXif(); });

  add(s.body,
    h('h3', {}, 'Find an element'), h('p', { class: 'muted small' }, 'Which module owns a name: the open module, its references, or producer modules in folders.'),
    h('div', { class: 'toolbar' }, h('label', { for: 'find-name', class: 'sr-only' }, 'Name or pattern'), h('div', { class: 'grow' }, findName),
      h('label', { for: 'find-in', class: 'sr-only' }, 'Producer folders'), h('div', { class: 'grow' }, findIn), findButton), findOut,
    h('h3', {}, 'Inspect an extension'), h('p', { class: 'muted small' }, 'The actions with their parameters, entities and structures of an Integration Studio extension (.xif).'),
    h('div', { class: 'toolbar' }, h('label', { for: 'xif-path', class: 'sr-only' }, 'Extension file (.xif)'), h('div', { class: 'grow' }, xif), xifButton), xifOut);
  return s.el;
}
