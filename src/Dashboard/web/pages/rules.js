import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api } from '../core/api.js';
import { task, cli } from '../core/ui.js';
import { markdown } from '../core/markdown.js';

export default {
  id: 'rules', group: 'Learn', title: 'Rules and checklist', icon: 'rules',
  description: 'The production rules agents follow, the best practices, and the checklist for every screen and block.',
  async render(main) {
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
  },
};
