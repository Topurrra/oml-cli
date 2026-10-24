import { h, add } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli } from '../core/ui.js';
import { onLeave } from '../core/lifecycle.js';

export default {
  id: 'activity', group: 'Team', title: 'Agent activity', icon: 'activity',
  description: 'What agents ran with oml on this computer: commands, results and time. From the local usage log.',
  async render(main) {
    const out = h('div', { class: 'stack' });
    add(main, out);
    const first = await task(null, out, 'Reading the usage log…', () => api('log'));
    if (!first) return;
    if (!first.enabled) {
      add(out, h('section', { class: 'card' }, h('h2', {}, 'Turn on the usage log'),
        h('p', {}, 'oml can write one line per command to a file on this computer. It never sends anything, and never writes arguments, specs, tokens or folders.'),
        h('p', {}, 'For the current PowerShell window (start your agent and this dashboard from it):'), cli('$env:OML_LOG = "1"'),
        h('p', {}, 'For every new window:'), cli('setx OML_LOG 1'),
        h('p', { class: 'hint' }, 'Then start the dashboard again. The log is ', h('code', { text: first.file }), '.')));
      return;
    }
    const live = h('input', { type: 'checkbox', id: 'live', checked: true });
    const table = h('div'), summary = h('div');
    add(out, h('section', { class: 'card' },
      h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, 'Latest commands'), h('p', { class: 'muted small' }, 'From ', h('code', { text: first.file }))),
        h('label', { class: 'check', for: 'live' }, live, 'Refresh every 5 seconds')), table),
      h('section', { class: 'card' }, h('h2', {}, 'Summary'), summary));
    const draw = data => {
      const lines = data.lines || [];
      table.replaceChildren(lines.length === 0 ? h('p', { class: 'muted' }, 'No commands yet.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Time', 'Command', 'Module', 'Result', 'Time taken', 'Problem'].map((t, i) => h('th', { class: i === 4 ? 'num' : null }, t)))),
          h('tbody', {}, lines.slice(0, 100).map(l => h('tr', {},
            h('td', { class: 'small' }, l.ts ? new Date(l.ts).toLocaleString() : ''),
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
    await loadSummary();
    const timer = setInterval(async () => {
      if (!live.checked) return;
      try { draw(await api('log')); } catch { /* the next tick tries again */ }
    }, 5000);
    onLeave(() => clearInterval(timer));
  },
};
