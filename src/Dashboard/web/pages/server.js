// The platform server: the end-of-day score over time (from the cache that oml server check keeps, no login), the
// check itself, and the modules and versions on the server (list, save one version to a new file, compare two).
// Nothing here writes to the server: oml server only reads it.
import { h, add, icon } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, errorBox, reportView } from '../core/ui.js';
import { scoreChart, sparkline, when, signed } from '../core/chart.js';
import { reviewResult } from '../core/review-view.js';
import { viewer } from '../core/viewer.js';
import { setModule } from '../core/state.js';
import { workset } from '../core/workset.js';

const appsLabel = apps => apps && apps.length ? apps.join(', ') : 'All applications';
const appsKey = apps => (apps || []).map(a => a.toLowerCase()).join('|');
const STATUS = { worse: ['fail', '▼ Worse'], better: ['ok', '▲ Better'], same: ['', 'Same'], new: ['accent', 'New'] };
const statusBadge = st => h('span', { class: 'badge ' + STATUS[st][0] }, STATUS[st][1]);
const quote = a => /[\s"&|<>]/.test(a) ? `"${a}"` : a;

export default {
  id: 'server', group: 'Environment', title: 'Platform server', icon: 'server',
  description: 'How the check score of your published modules changes day by day, and every module and version on the server. Read only.',
  async render(main) {
    const top = h('div'), scoreOut = h('div', { class: 'stack' }), checkCard = h('section', { class: 'card', 'aria-labelledby': 'srv-check-title' });
    const modulesCard = h('section', { class: 'card', 'aria-labelledby': 'srv-mod-title' });
    add(main, top, scoreOut, checkCard, modulesCard);
    let history = await task(null, scoreOut, 'Reading the check history…', () => api('server/history'));
    if (!history) return;
    const server = history.server || {};
    add(top, server.configured
      ? h('p', { class: 'muted small' }, 'Connected to ', h('code', { text: server.host }), '. Read only: oml never publishes or changes anything on the server.')
      : connectCard(server));

    const drawScore = () => {
      scoreOut.replaceChildren();
      const runs = history.runs || [];
      if (!runs.length) {
        add(scoreOut, h('section', { class: 'card' }, h('h2', {}, 'Environment score'),
          h('div', { class: 'empty' }, h('p', {}, h('strong', {}, 'No check has run on this computer yet.')),
            h('p', { class: 'small' }, server.configured ? 'Run the first one below. After a few days, this page shows how each module gets better or worse.' : 'Connect the server, then run the first check below.'))));
        return;
      }
      add(scoreOut, scoreCard(history, async host => { history = await api('server/history?host=' + encodeURIComponent(host)); drawScore(); }));
    };
    drawScore();
    checkSection(checkCard, history, server, async () => { history = await api('server/history' + (history.host ? '?host=' + encodeURIComponent(history.host) : '')); drawScore(); });
    modulesSection(modulesCard, server);
  },
};

function connectCard(server) {
  const missing = server.missing || [];
  return h('section', { class: 'card', 'aria-labelledby': 'srv-connect' }, h('h2', { id: 'srv-connect' }, 'Connect the platform server'),
    h('p', {}, 'oml reads the server with the login you use in Service Studio. For your safety, the password is never typed into this page.'),
    h('p', {}, 'Set the missing ', missing.length === 1 ? 'value' : 'values', ' as Windows user variables in PowerShell. The dashboard reads them on the next action; you do not need to restart it.'),
    missing.includes('OML_SERVER_HOST') ? cli("[Environment]::SetEnvironmentVariable('OML_SERVER_HOST', 'dev.example.com', 'User')") : null,
    missing.includes('OML_SERVER_USER') ? cli("[Environment]::SetEnvironmentVariable('OML_SERVER_USER', 'your.name', 'User')") : null,
    missing.includes('OML_SERVER_PASSWORD') ? cli("[Environment]::SetEnvironmentVariable('OML_SERVER_PASSWORD', (Read-Host -MaskInput 'Password'), 'User')") : null,
    h('p', { class: 'hint' }, 'The host is the server name without https://. The check history below needs no login.'));
}

// ---- the score ----

function scoreCard(history, switchHost) {
  const runs = history.runs;
  const sets = [...new Map(runs.map(r => [appsKey(r.apps), r.apps])).entries()];
  let set = appsKey(runs[runs.length - 1].apps);
  const body = h('div', { class: 'stack' });
  const head = h('div', { class: 'row' });
  if ((history.hosts || []).length > 1) {
    const hostSelect = h('select', { id: 'srv-host', 'aria-label': 'Server' }, history.hosts.map(x => h('option', { value: x, selected: x === history.host }, x)));
    hostSelect.addEventListener('change', () => switchHost(hostSelect.value));
    add(head, hostSelect);
  }
  if (sets.length > 1) {
    const setSelect = h('select', { id: 'srv-set', 'aria-label': 'Applications' }, sets.map(([k, apps]) => h('option', { value: k, selected: k === set }, appsLabel(apps))));
    setSelect.addEventListener('change', () => { set = setSelect.value; draw(); });
    add(head, setSelect);
  }
  const card = h('section', { class: 'card', 'aria-labelledby': 'srv-score-title' },
    h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'srv-score-title' }, 'Environment score'),
      h('p', { class: 'muted small' }, 'The average module score of each run of oml server check. 100 means no findings; a high finding weighs 10, a medium 3, a low 1.')), head),
    body);

  const draw = () => {
    body.replaceChildren();
    const mine = runs.filter(r => appsKey(r.apps) === set);
    const points = mine.map((r, i) => ({ time: r.time, score: r.total.score, change: i ? r.total.score - mine[i - 1].total.score : null,
      high: r.total.high, medium: r.total.medium, low: r.total.low, modules: r.total.modules, apps: appsLabel(r.apps) }));
    const last = mine[mine.length - 1], before = mine[mine.length - 2];
    const change = before ? last.total.score - before.total.score : null;
    const reportOut = h('div');
    const open = h('button', { class: 'button', type: 'button', disabled: !last.report }, 'Open the latest report');
    open.addEventListener('click', async () => {
      const data = await task(open, reportOut, 'Opening the report…', () => api('server/report', { host: history.host, index: last.index }));
      if (data) add(reportOut, reportView({ ...data, cli: 'oml server check' + last.apps.map(a => ' --app ' + quote(a)).join('') }, 'Check report of ' + when(last.time), 'check'));
    });
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
  return card;
}

function runTable(points) {
  return h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Run', 'Score', 'Change', 'High', 'Medium', 'Low', 'Modules'].map((t, i) => h('th', { class: i ? 'num' : null, scope: 'col' }, t)))),
    h('tbody', {}, [...points].reverse().map(p => h('tr', {}, h('td', {}, when(p.time)), h('td', { class: 'num' }, p.score), h('td', { class: 'num' }, signed(p.change)),
      h('td', { class: 'num' }, p.high), h('td', { class: 'num' }, p.medium), h('td', { class: 'num' }, p.low), h('td', { class: 'num' }, p.modules))))));
}

// The modules of the latest run: worse first (as oml server check lists them), with the score of each run as a line.
function moduleTable(runs) {
  const last = runs[runs.length - 1], before = runs[runs.length - 2];
  const prev = new Map((before ? before.modules : []).map(m => [m.key, m.score]));
  const rank = { worse: 0, new: 1, better: 2, same: 3 };
  const rows = last.modules.map(m => {
    const change = prev.has(m.key) ? m.score - prev.get(m.key) : null;
    const status = change === null ? (before ? 'new' : 'same') : change > 0 ? 'better' : change < 0 ? 'worse' : 'same';
    return { ...m, change, status, trend: runs.map(r => (r.modules.find(x => x.key === m.key) || {}).score) };
  }).sort((a, b) => rank[a.status] - rank[b.status] || a.score - b.score);
  const apps = [...new Set(rows.map(r => r.application))].sort();
  const tbody = h('tbody');
  let filter = '';
  const fill = () => tbody.replaceChildren(...rows.filter(r => !filter || r.application === filter).map(r => h('tr', {},
    h('th', { scope: 'row' }, r.name, r.version ? h('span', { class: 'tiny muted' }, ' v' + r.version) : null), h('td', { class: 'small' }, r.application),
    h('td', { class: 'num' }, h('strong', {}, r.score)), h('td', {}, statusBadge(r.status), r.change ? h('span', { class: 'tiny muted' }, ' ' + signed(r.change)) : null),
    h('td', { class: 'spark-cell' }, sparkline(r.trend)),
    h('td', { class: 'num' }, r.high), h('td', { class: 'num' }, r.medium), h('td', { class: 'num' }, r.low))));
  fill();
  const counts = Object.fromEntries(Object.keys(rank).map(k => [k, rows.filter(r => r.status === k).length]));
  return h('div', { class: 'stack' },
    h('div', { class: 'card-head' }, h('div', {}, h('h3', {}, 'Modules'),
      h('p', { class: 'muted small' }, before ? `${counts.worse} worse, ${counts.better} better, ${counts.same} the same, ${counts.new} new since the run before.` : 'The first run: the next one shows what changed.')),
      apps.length > 1 ? appFilter(apps, v => { filter = v; fill(); }) : null),
    h('div', { class: 'table-wrap' }, h('table', { class: 'wide' }, h('thead', {}, h('tr', {}, ['Module', 'Application', 'Score', 'Since the run before', 'Trend', 'High', 'Medium', 'Low']
      .map((t, i) => h('th', { scope: 'col', class: [2, 5, 6, 7].includes(i) ? 'num' : null }, t)))), tbody)));
}

function appFilter(apps, set) {
  const select = h('select', { id: 'srv-mod-app' }, h('option', { value: '' }, 'All applications'), apps.map(a => h('option', { value: a }, a)));
  select.addEventListener('change', () => set(select.value));
  return h('div', {}, h('label', { for: 'srv-mod-app', class: 'sr-only' }, 'Application'), select);
}

// ---- the check ----

function checkSection(card, history, server, reload) {
  const runs = history.runs || [];
  const last = runs[runs.length - 1];
  const known = [...new Set(runs.flatMap(r => r.apps || []))].sort();
  const options = [...(last && last.apps.length ? [['last', `Same as the last run (${appsLabel(last.apps)})`]] : []), ['all', 'All applications'], ...known.map(a => ['app:' + a, a])];
  const select = h('select', { id: 'srv-check-apps' }, options.map(([v, t]) => h('option', { value: v }, t)));
  const run = h('button', { class: 'button primary', type: 'button', disabled: !server.configured }, 'Run the check');
  const out = h('div', { class: 'stack' });
  const apps = () => select.value === 'last' ? last.apps : select.value === 'all' ? [] : [select.value.slice(4)];
  const command = () => 'oml server check' + apps().map(a => ' --app ' + quote(a)).join('');
  const line = h('div', {}, cli(command()));
  select.addEventListener('change', () => line.replaceChildren(cli(command())));
  add(card, h('h2', { id: 'srv-check-title' }, 'Run the end-of-day check'),
    h('p', { class: 'muted small' }, 'Downloads the published modules that changed since the last run into the cache, checks them all, and adds a run to the history. It changes nothing on the server. A first run over many modules can take minutes.'),
    h('div', { class: 'field' }, h('label', { for: 'srv-check-apps' }, 'Applications'), h('div', { class: 'row' }, select, run)),
    server.configured ? null : h('p', { class: 'hint' }, 'Connect the server first (see the top of this page).'),
    line, out);
  run.addEventListener('click', async () => {
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
  });
}

// ---- modules and versions ----

function modulesSection(card, server) {
  const list = h('button', { class: 'button', type: 'button', disabled: !server.configured }, 'Show the modules');
  const search = h('input', { type: 'search', id: 'srv-mod-filter', placeholder: 'Module or application', autocomplete: 'off', hidden: true });
  const out = h('div', { class: 'stack' });
  add(card, h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'srv-mod-title' }, 'Modules and versions'),
    h('p', { class: 'muted small' }, 'Every module on the server, with Service Studio closed. Save any version to a new file, or compare two versions.')), list),
    h('label', { for: 'srv-mod-filter', class: 'sr-only' }, 'Filter modules'), search, out);
  list.addEventListener('click', async () => {
    const data = await task(list, out, 'Logging in and reading the module list…', async () => need(await api('server/modules')));
    if (!data) return;
    const modules = (data.output && data.output.modules) || [];
    search.hidden = false;
    const tbody = h('tbody');
    const fill = () => {
      const q = search.value.trim().toLowerCase();
      tbody.replaceChildren(...modules.filter(m => !q || (m.name + ' ' + m.application).toLowerCase().includes(q)).flatMap(m => moduleRow(m)));
    };
    search.addEventListener('input', fill);
    fill();
    add(out, h('div', { class: 'table-wrap' }, h('table', { class: 'fixed cols-modules' }, h('thead', {}, h('tr', {}, ['Module', 'Application', 'Latest version', ''].map(t => h('th', { scope: 'col' }, t)))), tbody)),
      h('p', { class: 'hint' }, `${modules.length} modules on ${data.output.host}.`), cli(data.cli));
  });
}

function moduleRow(m) {
  const more = h('button', { class: 'button small', type: 'button', 'aria-expanded': 'false', 'aria-label': `Versions of ${m.name}` }, 'Versions');
  const panel = h('div', { class: 'stack' });
  const row = h('tr', {}, h('th', { scope: 'row' }, m.name), h('td', { class: 'small' }, m.application),
    h('td', { class: 'small' }, m.lastVersion ? 'v' + m.lastVersion : '', m.uploadedBy ? h('span', { class: 'muted' }, ` by ${m.uploadedBy}`) : null,
      m.uploadedDate ? h('div', { class: 'tiny muted' }, when(m.uploadedDate)) : null,
      m.lastVersionId && m.publishedVersionId !== m.lastVersionId ? h('div', {}, h('span', { class: 'badge warn' }, 'Latest not published')) : null),
    h('td', { class: 'num' }, more));
  const detail = h('tr', { class: 'detail-row', hidden: true }, h('td', { colspan: '4' }, panel));
  let loaded = false;
  more.addEventListener('click', async () => {
    const open = detail.hidden;
    detail.hidden = !open;
    more.setAttribute('aria-expanded', String(open));
    if (!open || loaded) return;
    const data = await task(more, panel, `Reading the versions of ${m.name}…`, async () => need(await api('server/versions?module=' + encodeURIComponent(m.name))));
    if (!data) return;
    loaded = true;
    add(panel, versionsView(m.name, data.output.versions || []), cli(data.cli));
  });
  return [row, detail];
}

function versionsView(name, versions) {
  const out = h('div', { class: 'stack' });
  const table = h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Version', 'Uploaded', ''].map(t => h('th', { scope: 'col' }, t)))),
    h('tbody', {}, versions.map(v => {
      const save = h('button', { class: 'button small', type: 'button', 'aria-label': `Save ${name} version ${v.version} to a new file` }, icon('download'), 'Save to a file');
      save.addEventListener('click', async () => {
        const r = await task(save, out, `Saving ${name} version ${v.version}…`, async () => need(await api('server/pull', { module: name, version: v.version })));
        if (!r || !r.path) return;
        setModule(r.path); workset.add(r.path, { kind: 'server', module: name, version: v.version });
        add(out, h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path }), ' ', h('a', { href: '#/explorer' }, 'Open it in the Module explorer')), cli(r.cli));
      });
      return h('tr', {}, h('th', { scope: 'row' }, 'v' + v.version, v.published ? h('span', {}, ' ', h('span', { class: 'badge ok' }, 'Published')) : null),
        h('td', { class: 'small' }, when(v.uploadedDate), v.uploadedBy ? h('span', { class: 'muted' }, ' by ' + v.uploadedBy) : null), h('td', { class: 'num' }, save));
    }))));
  if (versions.length < 2) return h('div', {}, table, out);
  const id = 'cmp-' + name;
  const pick = (suffix, label, value) => h('div', { class: 'field' }, h('label', { for: id + suffix }, label),
    h('select', { id: id + suffix }, versions.map(v => h('option', { value: String(v.version), selected: v.version === value }, 'v' + v.version + (v.published ? ' (published)' : '')))));
  const published = versions.find(v => v.published);
  const to = versions[0].version, from = published && published.version !== to ? published.version : versions[1].version;
  const fromField = pick('-from', 'From', from), toField = pick('-to', 'To', to);
  const compare = h('button', { class: 'button primary', type: 'button' }, 'Compare');
  const result = h('div', { class: 'stack' });
  compare.addEventListener('click', async () => {
    const [a, b] = [fromField.querySelector('select').value, toField.querySelector('select').value];
    if (a === b) { result.replaceChildren(errorBox(new Error('Choose two different versions.'))); return; }
    const data = await task(compare, result, `Downloading v${a} and v${b} and comparing…`, async () => need(await api('server/review', { module: name, from: a, to: b })));
    if (!data) return;
    add(result, reviewResult(data, { title: `${name}: v${a} → v${b}`, heading: 'h3' }),
      data.view ? viewer({ view: data.view, title: 'Review report', width: 1200, height: 900, themed: true }) : null);
  });
  return h('div', { class: 'stack' }, table, out,
    h('div', { class: 'compare' }, h('h3', {}, 'Compare two versions'), h('div', { class: 'row compare-row' }, fromField, toField, compare),
      h('p', { class: 'hint' }, 'The same review as the Review page: production risks, changes and new check findings.')), result);
}
