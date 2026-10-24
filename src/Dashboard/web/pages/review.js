import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, copyButton, downloadLink, severityBadge, pathField, staged } from '../core/ui.js';
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
      const r = data.output || {};
      const s = r.summary || {};
      const table = (heads, rows) => h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, heads.map(t => h('th', {}, t)))), h('tbody', {}, rows)));
      add(out,
        h('section', { class: 'card', 'aria-label': 'Review results' },
          h('div', { class: 'card-head' }, h('h2', {}, r.module || 'Review'),
            h('div', { class: 'row' }, h('span', { class: 'badge ' + (s.risks ? 'fail' : 'ok') }, `${s.risks || 0} risks`), s.info ? h('span', { class: 'badge' }, `${s.info} to look at`) : null,
              h('span', { class: 'badge ' + (s.newFindings ? 'warn' : 'ok') }, `${s.newFindings || 0} new findings`),
              h('span', { class: 'badge' }, `${s.added || 0} added, ${s.changed || 0} changed, ${s.removed || 0} removed, ${s.renamed || 0} renamed, ${s.recreated || 0} recreated`))),
          h('div', { class: 'row' }, data.markdown ? copyButton(data.markdown, 'Copy Markdown', 'Markdown') : null, data.notes ? copyButton(data.notes, 'Copy release notes', 'Release notes') : null,
            data.markdownDownload ? downloadLink(data.markdownDownload, 'Download Markdown', 'review.md') : null,
            data.download ? downloadLink(data.download, 'Download HTML', 'review.html') : null),
          h('h3', {}, `Risks (${(r.risks || []).length})`),
          (r.risks || []).length ? table(['Rule', 'Element', 'What'], r.risks.map(x => h('tr', {},
            h('td', {}, h('span', { class: 'badge ' + (x.info ? '' : 'fail') }, x.info ? 'Look' : 'Rule ' + x.rule), h('div', { class: 'tiny muted' }, x.ruleName)),
            h('td', { class: 'mono small' }, x.element), h('td', {}, x.text)))) : h('p', { class: 'muted' }, 'No change breaks a production rule.'),
          h('h3', {}, `Changes (${(r.changes || []).length})`),
          (r.changes || []).length ? table(['Area', 'Element', 'Change'], r.changes.map(c => h('tr', {},
            h('td', {}, h('span', { class: 'badge' }, c.area)), h('td', {}, h('strong', {}, c.name), h('div', { class: 'tiny muted' }, c.kind)),
            h('td', {}, h('span', { class: 'badge ' + ({ added: 'ok', removed: 'fail', recreated: 'fail' }[c.change] || 'warn') }, c.change), (c.details || []).length ? h('ul', { class: 'checklist' }, c.details.map(d => h('li', {}, d)), c.moreDetails ? h('li', { class: 'muted' }, `${c.moreDetails} more in the Markdown`) : null) : null)))) : h('p', { class: 'muted' }, 'No change found.'),
          h('h3', {}, `New findings (${(r.newFindings || []).length})`),
          (r.newFindings || []).length ? table(['Severity', 'Finding', 'Element'], r.newFindings.map(f => h('tr', {}, h('td', {}, severityBadge(f.severity)),
            h('td', {}, h('strong', {}, f.title), h('div', { class: 'small' }, f.fix)), h('td', { class: 'mono small' }, f.element || '(module)')))) : h('p', { class: 'muted' }, 'No new check finding.'),
          r.truncated ? h('p', { class: 'hint' }, 'The lists here are cut. The Markdown and the report page have everything.') : null,
          h('p', { class: 'hint' }, r.note || 'Logic bugs are not covered; 0 findings does not mean the code is correct.'),
          data.folder ? h('p', { class: 'hint' }, 'Markdown, release notes and the page are saved in ', h('code', { text: data.folder })) : null,
          cli(data.cli)),
        data.view ? h('section', { class: 'card', 'aria-label': 'Review report page' }, h('div', { class: 'card-head' }, h('h2', {}, 'Report page')),
          viewer({ view: data.view, title: 'Review report', width: 1200, height: 900, themed: true })) : null);
    }
  },
};
