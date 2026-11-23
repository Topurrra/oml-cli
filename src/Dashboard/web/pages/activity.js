import { h, add } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli } from '../core/ui.js';
import { onLeave } from '../core/lifecycle.js';
import { store } from '../core/store.js';
import { when } from '../core/chart.js';
import { dataTable, section, iconButton } from '../core/table.js';

const STATUS = { completed: ['ok', 'Done'], paused: ['warn', 'Waits for you'], running: ['', 'Running'], error: ['fail', 'Failed'], unknown: ['', 'Unknown'] };
const time = ms => ms === undefined || ms === null ? '' : ms >= 1000 ? (ms / 1000).toFixed(1) + ' s' : ms + ' ms';
const badge = (tone, word) => h('span', { class: 'badge ' + tone }, word);

// The programs an agent ran with oml code. A program that calls a gated command (install, publish, references, pull,
// lifetime) pauses until a person approves it in a terminal; this page only shows them.
function programDetail(p) {
  return h('div', { class: 'stack' },
    (p.pending || []).length ? h('div', {}, h('p', { class: 'small' }, h('strong', {}, 'Waits for approval:')),
      p.pending.map(c => h('div', { class: 'cli' }, h('span', { class: 'cli-label' }, c.tool), h('code', { text: c.command }))),
      cli('oml code approve ' + p.id)) : null,
    p.error ? h('div', { class: 'message error' }, String(typeof p.error === 'string' ? p.error : JSON.stringify(p.error))) : null,
    p.note ? h('p', { class: 'hint' }, p.note) : null,
    p.code ? h('pre', { class: 'out', text: p.code }) : null,
    cli('oml code get ' + p.id + ' --json'));
}

export default {
  id: 'activity', group: 'Agents', title: 'Agent activity', icon: 'activity',
  description: 'What agents ran with oml on this computer: commands, results, time, and the code-mode programs that wait for you.',
  async render(main) {
    const out = h('div', { class: 'stack' });
    add(main, out);
    const first = await task(null, out, 'Reading the usage log…', () => api('log'));
    if (!first) return;

    // Code-mode programs: of a project folder (empty: the folder the dashboard started in).
    const dir = h('input', { type: 'text', id: 'code-dir', class: 'code', placeholder: 'Project folder (empty: where the dashboard started)', spellcheck: 'false', value: store.get('code-dir', '') });
    const codeSection = section('Code-mode programs', { key: 'activity-code' });
    const waiting = h('div');
    const programs = dataTable({
      caption: 'Code-mode programs', empty: 'No program has run in this folder yet.', searchPlaceholder: 'Search programs',
      columns: [
        { key: 'started', label: 'Started', width: '25%', render: p => when(p.started), value: p => p.started || '' },
        { key: 'statusText', label: 'Status', width: '20%', render: p => badge(...(STATUS[p.status] || ['', p.status])) },
        { key: 'calls', label: 'Calls', width: '20%', render: p => [String(p.calls ?? ''), p.failed ? h('span', { class: 'tiny muted' }, ` · ${p.failed} failed`) : null], value: p => p.calls || 0 },
        { key: 'toolsText', label: 'Tools', width: '35%' },
      ],
      filters: [{ key: 'statusText', label: 'Status' }],
      sort: { key: 'started', desc: true },
      detail: programDetail, rowKey: p => p.id,
    });
    const codeOut = h('div');
    const loadCode = async button => {
      store.set('code-dir', dir.value.trim());
      const data = await task(button, codeOut, 'Reading the code-mode logs…', () => api('code' + (dir.value.trim() ? '?dir=' + encodeURIComponent(dir.value.trim()) : '')));
      if (!data) return;
      if (!dir.value.trim()) dir.placeholder = data.folder;
      const list = (data.programs || []).map(p => ({ ...p, statusText: (STATUS[p.status] || ['', p.status])[1], toolsText: Object.entries(p.tools || {}).map(([n, c]) => `${n} ${c}`).join(', ') }));
      programs.update(list);
      codeSection.count(list.length);
      const paused = list.filter(p => p.status === 'paused').length;
      waiting.replaceChildren(paused ? h('div', { class: 'message warn', role: 'status' }, h('strong', {}, paused === 1 ? 'One program waits for your approval. ' : `${paused} programs wait for your approval. `),
        'Open it to read what it will run, then approve it in your own terminal. The dashboard never approves.') : '');
      codeOut.replaceChildren(list.length ? h('p', { class: 'hint' }, 'From ', h('code', { text: data.executions }), '.') : cli('oml code run --json -f .oml/code/program.py'));
    };
    const showCode = iconButton('refresh', 'Read the programs again', () => loadCode(showCode), { small: true });
    dir.addEventListener('keydown', e => { if (e.key === 'Enter') loadCode(showCode); });
    add(codeSection.head, showCode);
    add(codeSection.body, h('div', { class: 'field' }, h('label', { for: 'code-dir', class: 'sr-only' }, 'Project folder'), dir), waiting, programs.el, codeOut);

    if (!first.enabled) {
      add(out, h('section', { class: 'card' }, h('h2', {}, 'The usage log is off'),
        h('p', {}, 'OML_LOG is set to 0, so oml writes no log, and memory recall and oml learn have nothing to learn from. The log stays on this computer, is never sent, and never holds arguments, specs, tokens or folders.'),
        h('p', {}, 'To turn it on again for the current PowerShell window (start your agent and this dashboard from it):'), cli('Remove-Item Env:OML_LOG'),
        h('p', {}, 'For every new window:'), cli('reg delete HKCU\\Environment /v OML_LOG /f'),
        h('p', { class: 'hint' }, 'Then start the dashboard again. The log is ', h('code', { text: first.file }), '.')), codeSection.el);
      await loadCode(showCode);
      return;
    }

    // Latest commands: refreshed every 5 seconds while the live button is pressed; search and page stay as they are.
    const commandsSection = section('Latest commands', { key: 'activity-commands' });
    let live = store.get('activity-live', true);
    const liveButton = iconButton('refresh', 'Refresh every 5 seconds', () => {
      live = !live; store.set('activity-live', live); liveButton.setAttribute('aria-pressed', String(live));
    }, { small: true });
    liveButton.setAttribute('aria-pressed', String(live));
    add(commandsSection.head, liveButton);
    const commands = dataTable({
      caption: 'Latest commands', empty: 'No commands yet.', searchPlaceholder: 'Search commands, modules, problems',
      columns: [
        { key: 'ts', label: 'Time', width: '18%', render: l => l.ts ? when(l.ts) : '' },
        { key: 'command', label: 'Command', width: '22%', render: l => [h('code', { text: l.command }), l.run ? h('span', { class: 'tiny muted' }, ' in run') : null] },
        { key: 'module', label: 'Module', width: '18%' },
        { key: 'result', label: 'Result', width: '12%', render: l => badge(l.exit === 0 ? 'ok' : l.exit === 1 ? 'warn' : 'fail', l.result) },
        { key: 'ms', label: 'Time taken', width: '12%', num: true, render: l => time(l.ms) },
        { key: 'error', label: 'Problem', width: '18%' },
      ],
      filters: [{ key: 'result', label: 'Results' }, { key: 'verb', label: 'Commands' }],
    });
    const rowsOf = data => (data.lines || []).map(l => ({ ...l, command: 'oml ' + l.cmd, verb: String(l.cmd || '').split(' ')[0], result: l.exit === 0 ? 'OK' : 'Exit ' + l.exit }));
    const draw = data => { const rows = rowsOf(data); commands.update(rows); commandsSection.count(rows.length); };
    add(commandsSection.body, h('p', { class: 'hint' }, 'From ', h('code', { text: first.file })), commands.el);

    const summarySection = section('Summary', { key: 'activity-summary' });
    const summaryOut = h('div');
    const summary = dataTable({
      caption: 'Summary per command', empty: 'Nothing to count yet.', searchPlaceholder: 'Search commands',
      columns: [
        { key: 'command', label: 'Command', width: '22%', render: c => h('code', { text: 'oml ' + c.command }) },
        { key: 'count', label: 'Count', width: '12%', num: true },
        { key: 'failures', label: 'Failures', width: '12%', num: true },
        { key: 'medianMs', label: 'Median time', width: '14%', num: true, render: c => time(c.medianMs) },
        { key: 'problems', label: 'Most common problems', width: '40%', render: c => (c.topFailures || []).map(f => h('div', {}, `${f.count}× ${f.message}`)) },
      ],
      sort: { key: 'count', desc: true },
    });
    add(summarySection.body, summary.el, summaryOut);
    const loadSummary = async () => {
      const data = await task(null, summaryOut, 'Counting…', async () => need(await api('log/summary')));
      if (!data || !data.output) return;
      const list = (data.output.commands || []).map(c => ({ ...c, problems: (c.topFailures || []).map(f => f.message).join(' ') }));
      summary.update(list); summarySection.count(list.length);
      add(summaryOut, cli(data.cli));
    };

    // The programs first: one that waits for an approval is the only thing on this page that needs the person.
    add(out, codeSection.el, commandsSection.el, summarySection.el);
    draw(first);
    await Promise.all([loadCode(showCode), loadSummary()]);
    const timer = setInterval(async () => {
      if (!live || !commandsSection.el.open) return;
      try { draw(await api('log')); } catch { /* the next tick tries again */ }
    }, 5000);
    onLeave(() => clearInterval(timer));
  },
};
