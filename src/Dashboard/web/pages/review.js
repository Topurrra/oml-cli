import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, pathField, staged } from '../core/ui.js';
import { reviewResult } from '../core/review-view.js';
import { viewer } from '../core/viewer.js';
import { worksetQuick } from '../core/workset.js';
import { state, hashParams } from '../core/state.js';

export default {
  id: 'review', group: 'Module', title: 'Review', icon: 'review',
  description: 'Compare a module with its changed copy: production risks first, then what changed and the new check findings.',
  async render(main) {
    const before = pathField('Before (the module as it is now)', store.get('review-before', state.module), 'C:\\Work\\Orders.oml', null, true);
    const after = pathField('After (the changed copy)', store.get('review-after', state.module ? staged(state.module) : ''), 'C:\\Work\\Orders.staged.oml', 'Usually the staged file of an apply, next to the module.', true);
    const quick = worksetQuick(path => { before.input.value = path; after.input.value = staged(path); });
    const rules = pathField('Rules file (optional)', store.get('check-rules', ''), 'C:\\Work\\oml-rules.json');
    const run = h('button', { class: 'button primary', type: 'button' }, 'Review');
    const out = h('div', { class: 'stack' });
    before.input.addEventListener('change', () => { if (!after.value() && before.value()) after.input.value = staged(before.value()); });
    const go = async () => {
      if (!before.value()) { before.input.focus(); return; }
      if (!after.value()) after.input.value = staged(before.value());
      store.set('review-before', before.value()); store.set('review-after', after.value());
      const data = await task(run, out, 'Comparing… about 3 seconds for a big module.', async () =>
        need(await api('review', { before: before.value(), after: after.value(), rules: rules.value() || undefined })));
      if (data) show(data);
    };
    run.addEventListener('click', go);
    add(main, h('section', { class: 'card', 'aria-label': 'Modules to compare' }, h('div', { class: 'grid-2' }, before.el, after.el), h('div', { class: 'field' }, quick), rules.el, run), out);
    const linked = hashParams();
    if (linked.get('before') && linked.get('after')) { before.input.value = linked.get('before'); after.input.value = linked.get('after'); await go(); }

    function show(data) {
      out.replaceChildren();
      add(out, reviewResult(data),
        data.view ? h('section', { class: 'card', 'aria-label': 'Review report page' }, h('div', { class: 'card-head' }, h('h2', {}, 'Report page')),
          viewer({ view: data.view, title: 'Review report', width: 1200, height: 900, themed: true })) : null);
    }
  },
};
