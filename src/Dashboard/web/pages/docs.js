import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, copyButton } from '../core/ui.js';
import { markdown } from '../core/markdown.js';
import { hashParams } from '../core/state.js';

const KINDS = { doc: 'Doc', spec: 'Spec', key: 'Spec key', warning: 'Warning', recipe: 'Recipe', command: 'Command', file: 'File' };

export default {
  id: 'docs', group: 'Learn', title: 'Docs and search', icon: 'docs',
  description: 'Search the OutSystems rules, the spec reference, the validation warnings, the recipes and the commands. Open a result to read it here.',
  async render(main) {
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
  },
};
