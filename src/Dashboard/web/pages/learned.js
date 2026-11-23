import { h, add } from '../core/dom.js';
import { api } from '../core/api.js';
import { task, cli, errorBox } from '../core/ui.js';
import { store } from '../core/store.js';
import { dataTable, section, iconButton } from '../core/table.js';

// What agents learned: the oml learn report and the open proposals of a project folder, with their evidence and diff.
// The dashboard only reads: Accept and Reject show the command, and the developer runs it.
const pct = n => Math.round((n || 0) * 100) + '%';
const quote = s => /[\s"]/.test(s) ? '"' + String(s).replace(/"/g, '\\"') + '"' : s;
const evidenceText = e => e.memory !== undefined ? `memory record #${e.memory}` : e.check ? `${e.check}: ${e.rule} (${e.count})`
  : `usage line ${e.usage}, ${e.ts}, ${e.command}${e.error ? ': ' + e.error : ''}${e.query ? ': ' + e.query : ''}`;

export default {
  id: 'learned', group: 'Agents', title: 'What agents learned', icon: 'learned',
  description: 'Proposals oml learn made from the local usage log and memory: evidence, diff, and the command to accept or reject each one.',
  async render(main) {
    const dirInput = h('input', { type: 'text', id: 'evolve-dir', class: 'code', placeholder: 'Project folder (empty: where the dashboard started)', spellcheck: 'false', value: store.get('evolve-dir', '') });
    const show = iconButton('refresh', 'Read again', () => load(), { primary: true });
    const body = h('div', { class: 'stack' });
    add(main, h('div', { class: 'toolbar' }, h('label', { for: 'evolve-dir', class: 'sr-only' }, 'Project folder'), h('div', { class: 'grow' }, dirInput), show),
      h('p', { class: 'hint' }, 'Nothing here changes a file. Open a proposal for its command; run it in a terminal after you read the evidence.'), body);
    dirInput.addEventListener('keydown', e => { if (e.key === 'Enter') load(); });
    const load = async () => {
      store.set('evolve-dir', dirInput.value.trim());
      body.replaceChildren();
      const q = dirInput.value.trim() ? '?dir=' + encodeURIComponent(dirInput.value.trim()) : '';
      const shown = await task(show, body, 'Reading proposals…', () => api('evolve' + q));
      if (!shown) return;
      const dir = shown.dir || dirInput.value;
      if (shown.dir && !dirInput.value) dirInput.placeholder = shown.dir;
      const report = await task(null, body, 'Reading the usage log…', () => api('evolve/report' + q));
      add(body, proposalsSection(shown, dir), report ? reportSection(report, dir) : null);
    };
    await load();
  },
};

// The four lists of the report: each its own table with search and paging, inside one section.
function reportSection(r, dir) {
  const o = r.output || {};
  const s = section('Where agents lose turns', { key: 'learned-report' });
  // Exit 1 is "no evidence yet" (no usage log): a hint, not an error.
  if (r.code === 1) { add(s.body, h('p', { class: 'muted' }, o.error || 'No usage log yet.'), o.hint ? h('p', { class: 'hint' }, o.hint) : null, cli(r.cli)); return s.el; }
  if (r.code !== 0) { add(s.body, errorBox(o.error || r.stderr || 'The report failed.'), o.hint ? h('p', { class: 'hint' }, o.hint) : null, cli(r.cli)); return s.el; }
  add(s.body, h('p', { class: 'muted small' }, `${o.lines} commands in ${o.sessions} sessions${o.from ? `, ${o.from} to ${o.to}` : ''}.`));
  const part = (title, opts, rows) => rows.length === 0 ? null : h('div', { class: 'report-part' }, h('h3', {}, title), dataTable({ ...opts, rows }).el);
  const code = text => h('code', { text });
  add(s.body,
    part('The same failure again and again', {
      caption: 'Repeated failures', sort: { key: 'count', desc: true },
      columns: [{ key: 'command', label: 'Command', width: '24%', render: f => code(f.command) }, { key: 'message', label: 'Message', width: '52%' },
        { key: 'count', label: 'Times', width: '12%', num: true }, { key: 'sessions', label: 'Sessions', width: '12%', num: true }],
    }, o.repeatedFailures || []),
    part('Retried until it passed', {
      caption: 'Retries', sort: { key: 'count', desc: true },
      columns: [{ key: 'command', label: 'Command', width: '24%', render: f => code(f.command) }, { key: 'failed', label: 'Failed with', width: '32%' },
        { key: 'fix', label: 'What changed', width: '32%' }, { key: 'count', label: 'Times', width: '12%', num: true }],
    }, o.retries || []),
    part('Searches that found nothing good', {
      caption: 'Searches without a good answer', sort: { key: 'count', desc: true },
      columns: [{ key: 'query', label: 'Words', width: '88%' }, { key: 'count', label: 'Times', width: '12%', num: true }],
    }, o.docMisses || []),
    part('Did the accepted proposals help?', {
      caption: 'Accepted proposals',
      columns: [{ key: 'name', label: 'Proposal', width: '40%' },
        { key: 'before', label: 'Failure rate before', width: '20%', num: true, render: a => pct(a.before.failureRate), value: a => a.before.failureRate },
        { key: 'after', label: 'After', width: '20%', num: true, render: a => pct(a.after.failureRate), value: a => a.after.failureRate },
        { key: 'verdict', label: 'Verdict', width: '20%', render: a => h('span', { class: 'badge ' + (a.verdict === 'helped' ? 'ok' : a.verdict === 'worse' ? 'fail' : 'warn') }, a.verdict) }],
      filters: [{ key: 'verdict', label: 'Verdicts' }],
    }, (o.accepted || []).map(a => ({ ...a, name: `${a.id} ${a.title || ''}` }))),
    cli(`oml learn propose --dir ${quote(dir)}`));
  return s.el;
}

// The open proposals: one row each; open a row for its lines, evidence, diff and the accept or reject command.
function proposalsSection(r, dir) {
  const o = r.output || {};
  const s = section('Proposals', { key: 'learned-proposals' });
  if (r.code !== 0) { add(s.body, errorBox(o.error || 'oml learn show failed.'), cli(r.cli)); return s.el; }
  const list = (o.proposals || []).map(p => ({ ...p, lineCount: (p.lines || []).length, searchText: (p.lines || []).map(l => l.text).join(' ') }));
  s.count(list.length);
  const detail = p => {
    const action = h('div');
    const reveal = (verb, extra) => action.replaceChildren(
      h('p', { class: 'hint' }, `Run this yourself after you read the lines and the diff. An agent never ${verb}s on your behalf.`),
      cli(`oml learn ${verb} ${p.id}${extra} --dir ${quote(dir)}`));
    return h('div', { class: 'stack' },
      h('ul', { class: 'evolve-lines' }, (p.lines || []).map(l => h('li', {}, h('span', { text: l.text }),
        h('details', {}, h('summary', { class: 'small muted' }, `Evidence (${(l.evidence || []).length})`),
          h('ul', { class: 'small' }, (l.evidence || []).map(e => h('li', { text: evidenceText(e) }))))))),
      p.diff ? h('details', {}, h('summary', {}, 'Diff'), h('pre', { class: 'out', text: p.diff })) : null,
      h('div', { class: 'toolbar' },
        h('button', { class: 'button small', type: 'button', onclick: () => reveal('accept', '') }, 'Accept…'),
        h('button', { class: 'button small', type: 'button', onclick: () => reveal('reject', ' --why "<reason>"') }, 'Reject…')),
      action);
  };
  add(s.body, dataTable({
    caption: 'Open proposals', rows: list, empty: o.hint || 'No open proposals.', searchPlaceholder: 'Search proposals',
    columns: [{ key: 'id', label: 'Id', width: '14%', render: p => h('code', { text: p.id }) }, { key: 'title', label: 'Proposal', width: '52%' },
      { key: 'kind', label: 'Kind', width: '20%', render: p => h('span', { class: 'badge' }, p.kind) }, { key: 'lineCount', label: 'Lines', width: '14%', num: true }],
    filters: [{ key: 'kind', label: 'Kinds' }],
    detail, rowKey: p => p.id,
  }).el,
  list.length ? h('p', { class: 'hint' }, 'Accepted changes are listed by ', h('code', { text: 'oml learn history' }), ' and can be undone with ', h('code', { text: 'oml learn undo <id>' }), '.')
    : cli(`oml learn propose --dir ${quote(dir)}`));
  return s.el;
}
