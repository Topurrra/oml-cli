import { h, add } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli } from '../core/ui.js';
import { onLeave } from '../core/lifecycle.js';
import { store } from '../core/store.js';
import { when } from '../core/chart.js';

const STATUS = { completed: ['ok', 'Done'], paused: ['warn', 'Waits for you'], running: ['', 'Running'], error: ['fail', 'Failed'], unknown: ['', 'Unknown'] };

// The programs an agent ran with oml code (code mode), newest first. A program that calls a gated command (install,
// publish, references, pull, lifetime) pauses until a person approves it in a terminal; this card only shows them.
function codeCard() {
  const dir = h('input', { type: 'text', id: 'code-dir', class: 'code', placeholder: 'Project folder (holds .oml)', spellcheck: 'false', value: store.get('code-dir', '') });
  const show = h('button', { class: 'button', type: 'button' }, 'Show');
  const out = h('div', { class: 'stack' });
  const load = async () => {
    store.set('code-dir', dir.value.trim());
    const data = await task(show, out, 'Reading the code-mode logs…', () => api('code' + (dir.value.trim() ? '?dir=' + encodeURIComponent(dir.value.trim()) : '')));
    if (!data) return;
    if (!dir.value.trim()) dir.placeholder = data.folder;
    const programs = data.programs || [];
    if (!programs.length) {
      add(out, h('p', { class: 'muted' }, 'No program has run in ', h('code', { text: data.folder }), ' yet.'), cli('oml code run --json -f .oml/code/program.py'));
      return;
    }
    const waiting = programs.filter(p => p.status === 'paused');
    add(out, waiting.length ? h('div', { class: 'message warn', role: 'status' }, h('strong', {}, waiting.length === 1 ? 'One program waits for your approval. ' : `${waiting.length} programs wait for your approval. `),
      'Read what it will run below, then approve it in your own terminal. The dashboard never approves.') : null,
      h('div', { class: 'table-wrap' }, h('table', { class: 'fixed cols-code' }, h('thead', {}, h('tr', {}, ['Started', 'Status', 'Calls', 'Tools'].map((t, i) => h('th', { scope: 'col', class: i === 2 ? 'num' : null }, t)))),
        h('tbody', {}, programs.flatMap(p => {
          const [tone, word] = STATUS[p.status] || ['', p.status];
          const tools = Object.entries(p.tools || {}).map(([n, c]) => `${n} ${c}`).join(', ');
          const more = h('tr', { class: 'detail-row' }, h('td', { colspan: '4' }, h('details', { open: p.status === 'paused' }, h('summary', {}, p.status === 'paused' ? 'What waits, and the program' : 'The program'),
            (p.pending || []).length ? h('div', { class: 'stack' }, h('p', { class: 'small' }, h('strong', {}, 'Waits for approval:')), p.pending.map(c => h('div', { class: 'cli' }, h('span', { class: 'cli-label' }, c.tool), h('code', { text: c.command }))),
              cli('oml code approve ' + p.id)) : null,
            p.error ? h('div', { class: 'message error' }, String(typeof p.error === 'string' ? p.error : JSON.stringify(p.error))) : null,
            p.note ? h('p', { class: 'hint' }, p.note) : null,
            p.code ? h('pre', { class: 'out', text: p.code }) : null,
            cli('oml code get ' + p.id + ' --json'))));
          return [h('tr', {}, h('td', { class: 'small' }, when(p.started)), h('td', {}, h('span', { class: 'badge ' + tone }, word)),
            h('td', { class: 'num' }, p.calls ?? '', p.failed ? h('div', { class: 'tiny muted' }, `${p.failed} failed`) : null), h('td', { class: 'small' }, tools)), more];
        })))),
      h('p', { class: 'hint' }, 'From ', h('code', { text: data.executions }), '.'));
  };
  show.addEventListener('click', load);
  dir.addEventListener('keydown', e => { if (e.key === 'Enter') load(); });
  return { el: h('section', { class: 'card', 'aria-labelledby': 'code-title' },
    h('h2', { id: 'code-title' }, 'Code-mode programs'),
    h('p', { class: 'muted small' }, 'Programs an agent ran with oml code: one Python program for many oml calls. A program that calls install, publish or another gated command stops until you approve it in your own terminal.'),
    h('div', { class: 'field' }, h('label', { for: 'code-dir' }, 'Project folder'), h('div', { class: 'row' }, h('div', { class: 'grow' }, dir), show), h('p', { class: 'hint' }, 'Empty: the folder the dashboard started in.')),
    out), load };
}

export default {
  id: 'activity', group: 'Agents', title: 'Agent activity', icon: 'activity',
  description: 'What agents ran with oml on this computer: commands, results and time, and the code-mode programs that wait for you.',
  async render(main) {
    const out = h('div', { class: 'stack' });
    add(main, out);
    const first = await task(null, out, 'Reading the usage log…', () => api('log'));
    if (!first) return;
    const code = codeCard();
    if (!first.enabled) {
      add(out, h('section', { class: 'card' }, h('h2', {}, 'The usage log is off'),
        h('p', {}, 'OML_LOG is set to 0, so oml writes no log, and memory recall and oml learn have nothing to learn from. The log stays on this computer, is never sent, and never holds arguments, specs, tokens or folders.'),
        h('p', {}, 'To turn it on again for the current PowerShell window (start your agent and this dashboard from it):'), cli('Remove-Item Env:OML_LOG'),
        h('p', {}, 'For every new window:'), cli('reg delete HKCU\\Environment /v OML_LOG /f'),
        h('p', { class: 'hint' }, 'Then start the dashboard again. The log is ', h('code', { text: first.file }), '.')), code.el);
      await code.load();
      return;
    }
    const live = h('input', { type: 'checkbox', id: 'live', checked: true });
    const table = h('div'), summary = h('div');
    // The programs first: one that waits for an approval is the only thing on this page that needs the person.
    add(out, code.el, h('section', { class: 'card' },
      h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, 'Latest commands'), h('p', { class: 'muted small' }, 'From ', h('code', { text: first.file }))),
        h('label', { class: 'check', for: 'live' }, live, 'Refresh every 5 seconds')), table),
      h('section', { class: 'card' }, h('h2', {}, 'Summary'), summary));
    const draw = data => {
      const lines = data.lines || [];
      table.replaceChildren(lines.length === 0 ? h('p', { class: 'muted' }, 'No commands yet.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Time', 'Command', 'Module', 'Result', 'Time taken', 'Problem'].map((t, i) => h('th', { class: i === 4 ? 'num' : null }, t)))),
          h('tbody', {}, lines.slice(0, 100).map(l => h('tr', {},
            h('td', { class: 'small nowrap' }, l.ts ? when(l.ts) : ''),
            h('td', {}, h('code', { text: 'oml ' + l.cmd }), l.run ? h('span', { class: 'tiny muted' }, ' in run') : null),
            h('td', { class: 'small' }, l.module || ''),
            h('td', {}, h('span', { class: 'badge ' + (l.exit === 0 ? 'ok' : l.exit === 1 ? 'warn' : 'fail') }, l.exit === 0 ? 'OK' : 'Exit ' + l.exit)),
            h('td', { class: 'num small' }, l.ms !== undefined ? (l.ms >= 1000 ? (l.ms / 1000).toFixed(1) + ' s' : l.ms + ' ms') : ''),
            h('td', { class: 'small' }, l.error || '')))))));
    };
    const loadSummary = async () => {
      const data = await task(null, summary, 'Counting…', async () => need(await api('log/summary')));
      if (!data || !data.output) return;
      add(summary, h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Command', 'Count', 'Failures', 'Median time', 'Most common problems'].map((t, i) => h('th', { class: i >= 1 && i <= 3 ? 'num' : null }, t)))),
        h('tbody', {}, (data.output.commands || []).map(c => h('tr', {}, h('td', {}, h('code', { text: 'oml ' + c.command })), h('td', { class: 'num' }, c.count), h('td', { class: 'num' }, c.failures),
          h('td', { class: 'num' }, c.medianMs >= 1000 ? (c.medianMs / 1000).toFixed(1) + ' s' : c.medianMs + ' ms'),
          h('td', { class: 'small' }, (c.topFailures || []).map(f => h('div', {}, `${f.count}× ${f.message}`)))))))), cli(data.cli));
    };
    draw(first);
    await Promise.all([code.load(), loadSummary()]);
    add(out, h('section', { class: 'card' }, h('h2', {}, 'What agents learned'),
      h('p', {}, 'oml learn reads this log and the project memory. It shows where agents lose turns and proposes trap records, company skills and doc edits, each with its evidence. A person accepts or rejects each one; every accepted change can be undone.'),
      h('p', {}, h('a', { href: '#/learned' }, 'Open What agents learned')), cli('oml learn report')));
    const timer = setInterval(async () => {
      if (!live.checked) return;
      try { draw(await api('log')); } catch { /* the next tick tries again */ }
    }, 5000);
    onLeave(() => clearInterval(timer));
  },
};
