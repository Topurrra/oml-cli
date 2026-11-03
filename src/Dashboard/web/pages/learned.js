import { h, add } from '../core/dom.js';
import { api } from '../core/api.js';
import { task, cli, errorBox } from '../core/ui.js';

// What agents learned: the oml evolve report and the open proposals of a project folder, with their evidence and diff.
// The dashboard only reads: Accept and Reject show the command, and the developer runs it.
const pct = n => Math.round((n || 0) * 100) + '%';
const quote = s => /[\s"]/.test(s) ? '"' + String(s).replace(/"/g, '\\"') + '"' : s;

export default {
  id: 'learned', group: 'Team', title: 'What agents learned', icon: 'learned',
  description: 'Proposals oml evolve made from the local usage log and memory: evidence, diff, and the command to accept or reject each one.',
  async render(main) {
    const out = h('div', { class: 'stack' });
    const dirInput = h('input', { type: 'text', id: 'evolve-dir', class: 'grow', placeholder: 'Project folder (holds .oml)', spellcheck: false });
    const show = h('button', { class: 'primary', type: 'submit' }, 'Show');
    const body = h('div', { class: 'stack' });
    add(main, h('form', { class: 'card', onsubmit: e => { e.preventDefault(); load(); } },
      h('label', { for: 'evolve-dir' }, 'Project folder'), h('div', { class: 'row' }, dirInput, show),
      h('p', { class: 'hint' }, 'Nothing here changes a file. Each button shows the command; run it in a terminal after you read the evidence.')), out);
    add(out, body);
    const load = async () => {
      body.replaceChildren();
      const q = dirInput.value.trim() ? '?dir=' + encodeURIComponent(dirInput.value.trim()) : '';
      const shown = await task(show, body, 'Reading proposals…', () => api('evolve' + q));
      if (!shown) return;
      if (shown.dir && !dirInput.value) dirInput.value = shown.dir;
      const dir = shown.dir || dirInput.value;
      const report = await task(null, body, 'Reading the usage log…', () => api('evolve/report' + q));
      if (report) add(body, reportCard(report, dir));
      add(body, proposalsCard(shown, dir));
    };
    await load();
  },
};

function reportCard(r, dir) {
  const o = r.output || {};
  const card = h('section', { class: 'card' }, h('h2', {}, 'Where agents lose turns'));
  // Exit 1 is "no evidence yet" (no usage log): a hint, not an error.
  if (r.code === 1) { add(card, h('p', { class: 'muted' }, o.error || 'No usage log yet.'), o.hint ? h('p', { class: 'hint' }, o.hint) : null, cli(r.cli)); return card; }
  if (r.code !== 0) { add(card, errorBox(o.error || r.stderr || 'The report failed.'), o.hint ? h('p', { class: 'hint' }, o.hint) : null, cli(r.cli)); return card; }
  add(card, h('p', { class: 'muted small' }, `${o.lines} commands in ${o.sessions} sessions${o.from ? `, ${o.from} to ${o.to}` : ''}.`));
  const table = (title, heads, rows) => rows.length === 0 ? null : h('div', {}, h('h3', {}, title),
    h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, heads.map(t => h('th', {}, t)))), h('tbody', {}, rows.map(cells => h('tr', {}, cells.map(c => h('td', { class: 'small' }, c))))))));
  add(card,
    table('The same failure again and again', ['Command', 'Message', 'Times', 'Sessions'], (o.repeatedFailures || []).map(f => [h('code', { text: f.command }), f.message, f.count, f.sessions])),
    table('Retried until it passed', ['Command', 'Failed with', 'What changed', 'Times'], (o.retries || []).map(f => [h('code', { text: f.command }), f.failed, f.fix, f.count])),
    table('Searches that found nothing good', ['Words', 'Times'], (o.docMisses || []).map(d => [d.query, d.count])),
    table('Did the accepted proposals help?', ['Proposal', 'Failure rate before', 'After', 'Verdict'], (o.accepted || []).map(a => [
      `${a.id} ${a.title || ''}`, pct(a.before.failureRate), pct(a.after.failureRate),
      h('span', { class: 'badge ' + (a.verdict === 'helped' ? 'ok' : a.verdict === 'worse' ? 'fail' : 'warn') }, a.verdict)])));
  add(card, cli(`oml evolve propose --dir ${quote(dir)}`));
  return card;
}

function proposalsCard(r, dir) {
  const o = r.output || {};
  const card = h('section', { class: 'card' }, h('h2', {}, 'Proposals'));
  if (r.code !== 0) { add(card, errorBox(o.error || 'oml evolve show failed.'), cli(r.cli)); return card; }
  const list = o.proposals || [];
  if (list.length === 0) { add(card, h('p', { class: 'muted' }, o.hint || 'No open proposals.'), cli(`oml evolve propose --dir ${quote(dir)}`)); return card; }
  for (const p of list) {
    const action = h('div');
    const reveal = (verb, extra) => () => action.replaceChildren(
      h('p', { class: 'hint' }, `Run this yourself after you read the lines and the diff. An agent never ${verb}s on your behalf.`),
      cli(`oml evolve ${verb} ${p.id}${extra} --dir ${quote(dir)}`));
    add(card, h('article', { class: 'evolve-proposal' },
      h('div', { class: 'card-head' }, h('h3', {}, h('code', { text: p.id }), ' ', p.title), h('span', { class: 'badge' }, p.kind)),
      h('ul', {}, (p.lines || []).map(l => h('li', {}, h('span', { text: l.text }),
        h('details', {}, h('summary', { class: 'small muted' }, `Evidence (${(l.evidence || []).length})`),
          h('ul', { class: 'small' }, (l.evidence || []).map(e => h('li', { text: e.memory !== undefined ? `memory record #${e.memory}` : e.check ? `${e.check}: ${e.rule} (${e.count})` : `usage line ${e.usage}, ${e.ts}, ${e.command}${e.error ? ': ' + e.error : ''}${e.query ? ': ' + e.query : ''}` }))))))),
      p.diff ? h('details', {}, h('summary', {}, 'Diff'), h('pre', { class: 'code', text: p.diff })) : null,
      h('div', { class: 'row' }, h('button', { type: 'button', onclick: reveal('accept', '') }, 'Accept…'), h('button', { type: 'button', onclick: reveal('reject', ' --why "<reason>"') }, 'Reject…')),
      action));
  }
  add(card, h('p', { class: 'hint' }, 'Accepted changes are listed by ', h('code', { text: 'oml evolve history' }), ' and can be undone with ', h('code', { text: 'oml evolve undo <id>' }), '.'));
  return card;
}
