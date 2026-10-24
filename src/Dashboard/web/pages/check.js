import { h, add, icon } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, copy, toast, severityBadge, selectFilter, pathField, staged, downloadLink } from '../core/ui.js';
import { viewer } from '../core/viewer.js';
import { modulePicker } from '../core/workset.js';
import { state } from '../core/state.js';

// A prompt that asks an agent to fix one finding with oml, in the order the skill asks for.
// source: { file } for one module, or { folders, apps } (module name -> application) for folders; rules: the rules file.
function fixPrompt(f, source) {
  const file = source.file || `${f.module}.oml`;
  const where = f.element ? `${f.module}/${f.element}` : `the module ${f.module}`;
  const read = f.element && !/^(upward|side|cycle|core-ui|foundation|large|unused-public)/.test(f.rule)
    ? `oml describe "${file}" "${f.element}"` : `oml inspect "${file}" --own`;
  const app = source.apps && source.apps[f.module];
  const folders = (source.folders || []).join(', ');
  const origin = source.file ? `Fix one oml check finding in ${file}.`
    : app && app !== '(no application)'
      ? `Fix one oml check finding in the module ${f.module} of the application ${app}, from an .oap package in ${folders}. ` +
        `Extract it first: oml app list "<file.oap>", then oml app extract "<file.oap>" "<folder>". Work on the extracted ${f.module}.oml.`
      : `Fix one oml check finding in the module file ${f.module}.oml, in ${folders} or a subfolder.`;
  const rules = source.rules ? ` --rules "${source.rules}"` : '';
  return [
    origin,
    `Finding: ${f.title}. Rule ${f.rule}, severity ${f.severity}, ${f.category}. Where: ${where}.`,
    `Detail: ${f.detail}`,
    `How to fix: ${f.fix}`,
    `Read it first: ${read}`,
    `Write a spec with only this change. Run oml spec check on it, then oml apply "${file}" spec.json "${staged(file)}".`,
    `Then run oml check "${staged(file)}"${rules} and confirm that ${f.rule} at ${where} is gone.`,
    `Then run oml review "${file}" "${staged(file)}" --md review.md and show me the .md.`,
    'Do not install. Do not publish.',
  ].join('\n');
}

export default {
  id: 'check', group: 'Module', title: 'Check', icon: 'check',
  description: 'Find AI Mentor-style code patterns in a module or a folder: security, performance, architecture and maintainability.',
  async render(main) {
    const out = h('div', { class: 'stack' });
    const folders = h('textarea', { id: 'check-in', class: 'code', rows: 2, placeholder: 'C:\\Exports\\Payments', spellcheck: 'false' });
    folders.value = store.get('check-in', '');
    const rules = pathField('Rules file (optional)', store.get('check-rules', ''), 'C:\\Work\\oml-rules.json', 'The team rules: which rules run, their severity and ignored findings.');
    const baseline = pathField('Baseline (optional)', store.get('check-baseline', ''), 'C:\\Work\\check.json', 'An earlier check report (.json). Findings then show as new or existing.');
    const runFolders = h('button', { class: 'button', type: 'button' }, 'Check folders');
    const makeRules = h('button', { class: 'button', type: 'button' }, 'Make a rules file');
    const run = async (input, button, status) => {
      store.set('check-rules', rules.value()); store.set('check-baseline', baseline.value());
      const data = await task(button, status || out, 'Checking… Service Studio starts once, then each module takes about a second.', async () =>
        need(await api('check', { ...input, rules: rules.value() || undefined, baseline: baseline.value() || undefined })));
      if (data) show(data, { ...input, rules: rules.value() || undefined });
    };
    const picker = modulePicker((path, button, status) => run({ path }, button, status), { button: 'Check module' });
    runFolders.addEventListener('click', () => {
      const list = folders.value.split(/\r?\n|;/).map(s => s.trim()).filter(Boolean);
      if (!list.length) { folders.focus(); return; }
      store.set('check-in', folders.value);
      run({ in: list }, runFolders);
    });
    makeRules.addEventListener('click', async () => {
      const data = await task(makeRules, out, 'Writing the rules file…', async () => need(await api('check/init-rules', {})));
      if (!data) return;
      rules.input.value = data.path;
      add(out, h('div', { class: 'message' }, 'Saved ', h('code', { text: data.path }), '. Edit it to match the AI Mentor rules your team uses. It is now the rules file of this page.'), cli(data.cli));
    });
    add(main, picker.el,
      h('section', { class: 'card', 'aria-label': 'Check options' }, h('div', { class: 'grid-2' },
        h('div', {}, h('div', { class: 'field' }, h('label', { for: 'check-in' }, 'Or folders with .oml and .oap files, one per line'), folders), runFolders),
        h('div', {}, rules.el, baseline.el, makeRules))),
      out);
    if (state.module) await picker.go();

    function show(data, input) {
      out.replaceChildren();
      const report = data.output || {};
      const s = report.summary || {};
      const findings = report.findings || [];
      const source = { file: input.path, folders: input.in || [], rules: input.rules,
        apps: Object.fromEntries((report.modules || []).map(m => [m.name, m.application])) };
      const severity = selectFilter('Severity', ['high', 'medium', 'low']);
      const category = selectFilter('Category', [...new Set(findings.map(f => f.category))].sort());
      const module = selectFilter('Module', [...new Set(findings.map(f => f.module))].sort());
      const count = h('p', { class: 'small muted', 'aria-live': 'polite' });
      const rows = findings.map(f => [f, h('tr', {},
        h('td', { class: 'nowrap' }, severityBadge(f.severity), f.status ? h('div', { class: 'tiny muted' }, f.status) : null),
        h('td', {}, h('strong', {}, f.title), h('div', { class: 'tiny muted mono' }, f.rule),
          h('div', { class: 'small' }, h('strong', {}, 'Where: '), f.module, f.element ? [' ', h('code', { class: 'tiny', text: f.element })] : ' (the module)'),
          h('div', { class: 'small' }, f.detail), h('div', { class: 'small' }, h('strong', {}, 'Fix: '), f.fix),
          h('button', { class: 'button small', type: 'button', 'aria-label': `Copy a fix prompt for ${f.rule} at ${f.element || f.module}`, onclick: () => copy(fixPrompt(f, source), 'Fix prompt') }, icon('copy'), 'Copy a fix prompt')))]);
      const filter = () => {
        let shown = 0;
        for (const [f, tr] of rows) {
          tr.hidden = (severity.select.value && f.severity !== severity.select.value) || (category.select.value && f.category !== category.select.value) || (module.select.value && f.module !== module.select.value);
          if (!tr.hidden) shown++;
        }
        count.textContent = `${shown} of ${findings.length} findings shown.`;
      };
      [severity, category, module].forEach(x => x.select.addEventListener('change', filter));
      filter();
      add(out,
        h('section', { class: 'card', 'aria-label': 'Check results' },
          h('div', { class: 'card-head' }, h('h2', {}, findings.length === 1 ? '1 finding' : findings.length ? `${findings.length} findings` : 'No findings'),
            h('div', { class: 'row' }, h('span', { class: 'badge' + (s.high ? ' fail' : '') }, `${s.high || 0} high`), h('span', { class: 'badge' + (s.medium ? ' warn' : '') }, `${s.medium || 0} medium`), h('span', { class: 'badge' }, `${s.low || 0} low`),
              s.new !== undefined ? h('span', { class: 'badge accent' }, `${s.new} new`) : null, s.ignored ? h('span', { class: 'badge' }, `${s.ignored} ignored`) : null)),
          (s.skipped || []).length ? h('div', { class: 'message info' }, 'Not checked: ', s.skipped.map(x => `${x.input || x.module}${x.rule ? ' / ' + x.rule : ''} (${x.reason})`).join('; ')) : null,
          (report.modules || []).length ? h('p', { class: 'small muted' }, 'Modules: ', report.modules.map(m => m.name).join(', ')) : null,
          findings.length ? h('div', {},
            h('div', { class: 'grid-3' }, severity.el, category.el, module.el), count,
            h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Severity', 'Finding'].map(t => h('th', { class: 'nowrap' }, t)))), h('tbody', {}, rows.map(r => r[1]))))) : null,
          h('p', { class: 'hint' }, 'oml check covers part of the AI Mentor patterns. 0 findings does not mean the code is correct.'),
          data.json ? h('div', { class: 'row' }, h('span', { class: 'small muted' }, 'Report data: ', h('code', { text: data.json })),
            h('button', { class: 'button small', type: 'button', onclick: () => { baseline.input.value = data.json; store.set('check-baseline', data.json); toast('Baseline set'); } }, 'Use as the baseline next time')) : null,
          cli(data.cli)),
        data.view ? h('section', { class: 'card', 'aria-label': 'Check report page' }, h('div', { class: 'card-head' }, h('h2', {}, 'Report page'),
          h('div', { class: 'row' }, data.download ? downloadLink(data.download, 'Download HTML', 'check.html') : null,
            data.jsonDownload ? downloadLink(data.jsonDownload, 'Download JSON', 'check.json') : null)),
          h('p', { class: 'hint' }, 'Saved in ', h('code', { text: data.path })), viewer({ view: data.view, title: 'Check report', width: 1200, height: 900, themed: true })) : null);
    }
  },
};
