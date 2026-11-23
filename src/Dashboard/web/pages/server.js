// The platform server: the end-of-day score over time (from the cache that oml server check keeps, no login), the
// check itself, and the modules and versions on the server (list, save one version to a new file, compare two).
// Nothing here writes to the server: oml server only reads it.
import { h, add } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, reportView } from '../core/ui.js';
import { scoreChart, sparkline, when, signed } from '../core/chart.js';
import { dataTable, section, actionButton, iconButton } from '../core/table.js';
import { serverModules } from '../core/server-modules.js';

const appsLabel = apps => apps && apps.length ? apps.join(', ') : 'All applications';
const appsKey = apps => (apps || []).map(a => a.toLowerCase()).join('|');
const STATUS = { worse: ['fail', '▼ Worse'], better: ['ok', '▲ Better'], same: ['', 'Same'], new: ['accent', 'New'] };
const statusBadge = st => h('span', { class: 'badge ' + STATUS[st][0] }, STATUS[st][1]);
const quote = a => /[\s"&|<>]/.test(a) ? `"${a}"` : a;

export default {
  id: 'server', group: 'Environment', title: 'Platform server', icon: 'server',
  description: 'How the check score of your published modules changes day by day, and every module and version on the server. Read only.',
  async render(main) {
    const top = h('div');
    const score = section('Environment score', { key: 'server-score' });
    const check = section('Run the end-of-day check', { key: 'server-check' });
    const modules = section('Modules and versions', { key: 'server-modules' });
    add(main, top, score.el, check.el, modules.el);
    let history = await task(null, score.body, 'Reading the check history…', () => api('server/history'));
    if (!history) return;
    const server = history.server || {};
    add(top, server.configured
      ? h('p', { class: 'muted small' }, 'Connected to ', h('code', { text: server.host }), '. Read only: oml never publishes or changes anything on the server.')
      : connectCard(server));

    const drawScore = () => {
      score.body.replaceChildren(); score.head.replaceChildren();
      const runs = history.runs || [];
      if (!runs.length) {
        add(score.body, h('div', { class: 'empty' }, h('p', {}, h('strong', {}, 'No check has run on this computer yet.')),
          h('p', { class: 'small' }, server.configured ? 'Run the first one below. After a few days, this page shows how each module gets better or worse.' : 'Connect the server, then run the first check below.')));
        return;
      }
      scoreView(score, history, async host => { history = await api('server/history?host=' + encodeURIComponent(host)); drawScore(); });
    };
    drawScore();
    checkView(check, history, server, async () => { history = await api('server/history' + (history.host ? '?host=' + encodeURIComponent(history.host) : '')); drawScore(); });
    const list = serverModules(server, { compare: true, onCount: n => modules.count(n) });
    add(modules.body, h('p', { class: 'muted small' }, 'Every module on the server, with Service Studio closed. Open a module for its versions: save one to a new file, or compare two.'), list.el);
  },
};

function connectCard(server) {
  const missing = server.missing || [];
  return h('section', { class: 'card', 'aria-labelledby': 'srv-connect' }, h('h2', { id: 'srv-connect' }, 'Connect the platform server'),
    h('p', {}, 'oml reads the server with the login you use in Service Studio. For your safety, the password is never typed into this page.'),
    missing.includes('OML_SERVER_HOST') ? [h('p', {}, 'Set the server host in the project settings:'), cli('oml config set outsystems.serverHost dev.example.com')] : null,
    missing.some(m => m !== 'OML_SERVER_HOST') ? h('p', {}, 'Set your login as Windows user variables in PowerShell. The dashboard reads them on the next action; you do not need to restart it.') : null,
    missing.includes('OML_SERVER_USER') ? cli("[Environment]::SetEnvironmentVariable('OML_SERVER_USER', 'your.name', 'User')") : null,
    missing.includes('OML_SERVER_PASSWORD') ? cli("[Environment]::SetEnvironmentVariable('OML_SERVER_PASSWORD', (Read-Host -MaskInput 'Password'), 'User')") : null,
    h('p', { class: 'hint' }, 'The host is the server name without https://. The check history below needs no login.'));
}

// ---- the score ----

function scoreView(sec, history, switchHost) {
  const runs = history.runs;
  const sets = [...new Map(runs.map(r => [appsKey(r.apps), r.apps])).entries()];
  let set = appsKey(runs[runs.length - 1].apps);
  if ((history.hosts || []).length > 1) {
    const hostSelect = h('select', { id: 'srv-host', 'aria-label': 'Server' }, history.hosts.map(x => h('option', { value: x, selected: x === history.host }, x)));
    hostSelect.addEventListener('change', () => switchHost(hostSelect.value));
    add(sec.head, hostSelect);
  }
  if (sets.length > 1) {
    const setSelect = h('select', { id: 'srv-set', 'aria-label': 'Applications' }, sets.map(([k, apps]) => h('option', { value: k, selected: k === set }, appsLabel(apps))));
    setSelect.addEventListener('change', () => { set = setSelect.value; draw(); });
    add(sec.head, setSelect);
  }
  const body = h('div', { class: 'stack' });
  add(sec.body, h('p', { class: 'muted small' }, 'The average module score of each run of oml server check. 100 means no findings; a high finding weighs 10, a medium 3, a low 1.'), body);

  const draw = () => {
    body.replaceChildren();
    const mine = runs.filter(r => appsKey(r.apps) === set);
    const points = mine.map((r, i) => ({ time: r.time, score: r.total.score, change: i ? r.total.score - mine[i - 1].total.score : null,
      high: r.total.high, medium: r.total.medium, low: r.total.low, modules: r.total.modules, apps: appsLabel(r.apps) }));
    const last = mine[mine.length - 1], before = mine[mine.length - 2];
    const change = before ? last.total.score - before.total.score : null;
    const reportOut = h('div');
    const open = iconButton('open', 'Open the latest report', async () => {
      const data = await task(open, reportOut, 'Opening the report…', () => api('server/report', { host: history.host, index: last.index }));
      if (data) add(reportOut, reportView({ ...data, cli: 'oml server check' + last.apps.map(a => ' --app ' + quote(a)).join('') }, 'Check report of ' + when(last.time), 'check'));
    }, { disabled: !last.report });
    add(body,
      h('div', { class: 'score-head' },
        h('div', { class: 'score-big' }, h('span', { class: 'score-number' }, last.total.score),
          change !== null ? h('span', { class: 'badge ' + (change > 0 ? 'ok' : change < 0 ? 'fail' : '') }, (change > 0 ? '▲ ' : change < 0 ? '▼ ' : '') + signed(change) + ' since the run before') : h('span', { class: 'badge' }, 'First run')),
        h('p', { class: 'muted small' }, `${when(last.time)} · ${last.total.modules} modules · ${last.total.high} high, ${last.total.medium} medium, ${last.total.low} low · ${appsLabel(last.apps)}`),
        h('span', { class: 'grow' }), open),
      reportOut,
      points.length > 1 ? scoreChart(points) : h('p', { class: 'hint' }, 'The chart starts after the second run.'),
      points.length > 1 ? h('details', { class: 'doctor-details' }, h('summary', {}, `Show the ${points.length} runs as a table`), runTable(points)) : null,
      moduleTable(mine),
      h('p', { class: 'hint' }, 'From ', h('code', { text: history.history }), '. Runs over different applications are kept apart, as oml server check compares them.'));
  };
  draw();
}

function runTable(points) {
  return dataTable({
    caption: 'Runs', rows: [...points].reverse(), sort: { key: 'time', desc: true },
    columns: [
      { key: 'time', label: 'Run', width: '28%', render: p => when(p.time) },
      { key: 'score', label: 'Score', width: '12%', num: true },
      { key: 'change', label: 'Change', width: '12%', num: true, render: p => signed(p.change), value: p => p.change ?? 0 },
      { key: 'high', label: 'High', width: '12%', num: true }, { key: 'medium', label: 'Medium', width: '12%', num: true },
      { key: 'low', label: 'Low', width: '12%', num: true }, { key: 'modules', label: 'Modules', width: '12%', num: true },
    ],
  }).el;
}

// The modules of the latest run: worse first (as oml server check lists them), with the score of each run as a line.
function moduleTable(runs) {
  const last = runs[runs.length - 1], before = runs[runs.length - 2];
  const prev = new Map((before ? before.modules : []).map(m => [m.key, m.score]));
  const rank = { worse: 0, new: 1, better: 2, same: 3 };
  const rows = last.modules.map(m => {
    const change = prev.has(m.key) ? m.score - prev.get(m.key) : null;
    const status = change === null ? (before ? 'new' : 'same') : change > 0 ? 'better' : change < 0 ? 'worse' : 'same';
    return { ...m, change, status, statusText: STATUS[status][1].replace(/^[▼▲] /, ''), rank: rank[status], trend: runs.map(r => (r.modules.find(x => x.key === m.key) || {}).score) };
  }).sort((a, b) => a.rank - b.rank || a.score - b.score);
  const counts = Object.fromEntries(Object.keys(rank).map(k => [k, rows.filter(r => r.status === k).length]));
  return h('div', {},
    h('h3', {}, 'Modules'),
    h('p', { class: 'muted small' }, before ? `${counts.worse} worse, ${counts.better} better, ${counts.same} the same, ${counts.new} new since the run before.` : 'The first run: the next one shows what changed.'),
    dataTable({
      caption: 'Modules of the latest run', rows, searchPlaceholder: 'Search modules and applications', className: 'wide',
      columns: [
        { key: 'name', label: 'Module', width: '20%', render: r => [h('strong', {}, r.name), r.version ? h('span', { class: 'tiny muted' }, ' v' + r.version) : null] },
        { key: 'application', label: 'Application', width: '16%' },
        { key: 'score', label: 'Score', width: '9%', num: true, render: r => h('strong', {}, String(r.score)) },
        { key: 'statusText', label: 'Since the run before', width: '17%', value: r => r.rank, render: r => [statusBadge(r.status), r.change ? h('span', { class: 'tiny muted' }, ' ' + signed(r.change)) : null] },
        { key: 'trend', label: 'Trend', width: '14%', sortable: false, render: r => sparkline(r.trend) },
        { key: 'high', label: 'High', width: '8%', num: true }, { key: 'medium', label: 'Medium', width: '8%', num: true }, { key: 'low', label: 'Low', width: '8%', num: true },
      ],
      filters: [{ key: 'application', label: 'Applications' }, { key: 'statusText', label: 'Changes' }],
    }).el);
}

// ---- the check ----

function checkView(sec, history, server, reload) {
  const runs = history.runs || [];
  const last = runs[runs.length - 1];
  const known = [...new Set(runs.flatMap(r => r.apps || []))].sort();
  const options = [...(last && last.apps.length ? [['last', `Same as the last run (${appsLabel(last.apps)})`]] : []), ['all', 'All applications'], ...known.map(a => ['app:' + a, a])];
  const select = h('select', { id: 'srv-check-apps' }, options.map(([v, t]) => h('option', { value: v }, t)));
  const out = h('div', { class: 'stack' });
  const apps = () => select.value === 'last' ? last.apps : select.value === 'all' ? [] : [select.value.slice(4)];
  const command = () => 'oml server check' + apps().map(a => ' --app ' + quote(a)).join('');
  const line = h('div', {}, cli(command()));
  select.addEventListener('change', () => line.replaceChildren(cli(command())));
  const run = actionButton('run', 'Run', async () => {
    const data = await task(run, out, 'Downloading and checking… Keep this page open.', async () => need(await api('server/check', { apps: apps() })));
    if (!data) return;
    const o = data.output || {}, s = o.summary || {};
    line.replaceChildren();
    add(out, h('div', { class: 'message' }, h('strong', {}, `Score ${s.score}`, s.change !== null && s.change !== undefined ? ` (${signed(s.change)})` : '', '. '),
      `${s.modules} modules: ${s.worse} worse, ${s.better} better, ${s.same} the same, ${s.new} new. ${s.downloaded} downloaded, ${s.cached} from the cache.`),
      (o.versionChanged || []).length ? h('p', { class: 'small' }, 'New versions since the last run: ', o.versionChanged.map(c => `${c.name} ${c.from} → ${c.to}${c.uploadedBy ? ' by ' + c.uploadedBy : ''}`).join(', ')) : null,
      [...(o.failed || []), ...(o.skipped || [])].length ? h('details', {}, h('summary', {}, 'Modules not checked'), h('ul', { class: 'checklist' }, [...(o.failed || []), ...(o.skipped || [])].map(x => h('li', {}, h('strong', {}, x.name), ': ', x.reason)))) : null,
      o.warning ? h('div', { class: 'message warn' }, o.warning) : null,
      cli(data.cli));
    await reload();
  }, { primary: true });
  run.disabled = !server.configured;
  add(sec.body, h('p', { class: 'muted small' }, 'Downloads the published modules that changed since the last run into the cache, checks them all, and adds a run to the history. It changes nothing on the server. A first run over many modules can take minutes.'),
    h('div', { class: 'toolbar' }, h('label', { for: 'srv-check-apps', class: 'sr-only' }, 'Applications'), select, run),
    server.configured ? null : h('p', { class: 'hint' }, 'Connect the server first (see the top of this page).'),
    line, out);
}
