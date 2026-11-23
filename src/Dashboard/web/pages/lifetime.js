import { h, add } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, errorBox, ltDownload, reportView } from '../core/ui.js';
import { state } from '../core/state.js';
import { dataTable, section, actionButton, iconButton } from '../core/table.js';

// LifeTime, read only: environments, the applications as a grid (one row per application, one column per environment,
// each cell the version there and whether it changed since its tag), a team's architecture page, a deployment status.
export default {
  id: 'lifetime', group: 'Environment', title: 'LifeTime', icon: 'lifetime',
  description: 'Environments, applications and deployments, read only. Tagging and deploying stay in the CLI, where you confirm each step.',
  async render(main) {
    const lt = state.info && state.info.lifeTime;
    if (!lt || !lt.configured) { add(main, connectCard(lt || {})); return; }
    add(main, h('p', { class: 'muted small' }, 'Connected to ', h('code', { text: lt.host }), '. Read only: this page never tags, deploys, aborts or continues.'));

    const envSec = section('Environments', { key: 'lt-envs' });
    const appSec = section('Applications', { key: 'lt-apps' });
    const teamSec = section('Team architecture page', { key: 'lt-team', open: false });
    const statusSec = section('Deployment status', { key: 'lt-status', open: false });
    add(main, envSec.el, appSec.el, teamSec.el, statusSec.el);

    const envs = await task(null, envSec.body, 'Reading environments…', async () => need(await api('lt/environments')));
    const envList = envs && Array.isArray(envs.output) ? envs.output : [];
    if (envs) {
      envSec.count(envList.length);
      add(envSec.body, h('div', { class: 'links' }, envList.map(e => h('div', { class: 'link-card' }, h('strong', {}, e.Name), h('span', {}, [e.EnvironmentType, e.Key].filter(Boolean).join(' · '))))), cli(envs.cli));
    }

    // Teams: a filter for the grid and the team of the architecture page.
    const teamSelect = h('select', { id: 'lt-team', 'aria-label': 'Team' }, h('option', { value: '' }, 'All teams'));
    const reportTeam = h('select', { id: 'lt-report-team', 'aria-label': 'Team' });
    const teamOut = h('div', { class: 'stack' });
    try {
      const teams = need(await api('lt/teams'));
      (Array.isArray(teams.output) ? teams.output : []).forEach(t => { const name = t.name || t.Name; add(teamSelect, h('option', { value: name }, name)); add(reportTeam, h('option', { value: name }, name)); });
      add(teamOut, cli(teams.cli));
    } catch (e) { add(teamOut, errorBox(e)); }

    // The grid: environments in LifeTime's order become the columns.
    const envNames = envList.map(e => e.Name);
    const appOut = h('div', { class: 'stack' });
    const cell = (a, env) => {
      const e = (a.environments || []).find(x => x.environment === env);
      if (!e) return h('span', { class: 'muted' }, '—');
      const result = h('div');
      const get = iconButton('download', `Download ${a.name} from ${env} as .oap`, () => ltDownload(a.name, env, false, get, result), { small: true });
      return h('div', { class: 'lt-cell' }, h('div', { class: 'lt-version' }, h('span', { class: 'mono' }, e.version || '—'),
        e.isModified === true ? h('span', { class: 'badge warn', title: 'Changed since this tag' }, 'Changed') : null, get), result);
    };
    const grid = dataTable({
      caption: 'Applications per environment', empty: 'No applications.', searchPlaceholder: 'Search applications',
      columns: [
        { key: 'name', label: 'Application', width: envNames.length ? '22%' : '100%', render: a => h('strong', {}, a.name) },
        ...envNames.map(env => ({ key: 'env:' + env, label: env, width: (78 / envNames.length).toFixed(2) + '%', sortable: false, render: a => cell(a, env) })),
      ],
      filters: [{ key: 'changedText', label: 'Changes' }],
    });
    const loadApps = async () => {
      const team = teamSelect.value;
      const data = await task(teamSelect, appOut, 'Reading applications… LifeTime can take some seconds.', async () => need(await api('lt/apps')));
      if (!data) return;
      let apps = Array.isArray(data.output) ? data.output : [];
      let teamData = null;
      if (team) {
        try { teamData = need(await api('lt/team-apps?team=' + encodeURIComponent(team))); }
        catch (e) { add(appOut, errorBox(e)); return; }
        const keys = new Set((Array.isArray(teamData.output) ? teamData.output : []).map(a => a.Key || a.key));
        apps = apps.filter(a => keys.has(a.key));
      }
      const rows = apps.map(a => ({ ...a, changedText: (a.environments || []).some(e => e.isModified === true) ? 'Changed since a tag' : 'All tagged',
        searchText: (a.environments || []).map(e => `${e.environment} ${e.version}`).join(' ') }));
      grid.update(rows);
      appSec.count(rows.length);
      add(appOut, h('p', { class: 'hint' }, '"Changed" means the version there has changes that are not tagged yet. The download icon saves the package as it runs in that environment; to work on its modules, use ',
        h('a', { href: '#/modules' }, 'Get modules'), ' > LifeTime.'), cli(data.cli), teamData ? cli(teamData.cli) : null);
    };
    teamSelect.addEventListener('change', loadApps);
    add(appSec.head, teamSelect);
    add(appSec.body, grid.el, appOut);

    // A team's architecture page: the packages go to a temporary folder that the command deletes.
    const archButton = actionButton('architecture', 'Make the page', async () => {
      const team = reportTeam.value;
      if (!team) { reportTeam.focus(); return; }
      const data = await task(archButton, teamOut, `Downloading the applications of ${team} and making the architecture page… This can take minutes.`,
        async () => need(await api('lt/report', { team, kind: 'architecture' })));
      if (data) add(teamOut, reportView(data, 'Architecture page of team ' + team, 'architecture'));
    }, { primary: true });
    add(teamSec.body, h('p', { class: 'muted small' }, "Downloads the team's applications (read only) to a temporary folder, makes the page, then deletes the packages."),
      h('div', { class: 'toolbar' }, reportTeam, archButton), teamOut);

    const keyInput = h('input', { type: 'text', id: 'lt-key', class: 'code', placeholder: 'Deployment key, such as 6f1c2a9e-3b7d-4c55-9e0a-1d2b3c4d5e6f', spellcheck: 'false' });
    const statusOut = h('div');
    const lookup = iconButton('search', 'Look up the deployment', async () => {
      const key = keyInput.value.trim();
      if (!key) { keyInput.focus(); return; }
      const data = await task(lookup, statusOut, 'Reading the deployment…', async () => need(await api('lt/status?key=' + encodeURIComponent(key)), [0, 1]));
      if (!data) return;
      const s = (data.output && data.output.status) || {};
      const st = String(s.DeploymentStatus || '').toLowerCase();
      add(statusOut, h('div', { class: 'row' }, h('span', { class: 'badge ' + (st === 'finished_successful' ? 'ok' : /error|abort/.test(st) ? 'fail' : 'warn') }, st || 'unknown')),
        (s.Messages || []).length ? h('ul', { class: 'checklist' }, s.Messages.map(m => h('li', {}, typeof m === 'string' ? m : JSON.stringify(m)))) : null,
        cli(data.cli));
    }, { primary: true });
    keyInput.addEventListener('keydown', e => { if (e.key === 'Enter') lookup.click(); });
    add(statusSec.body, h('div', { class: 'toolbar' }, h('label', { for: 'lt-key', class: 'sr-only' }, 'Deployment key'), h('div', { class: 'grow' }, keyInput), lookup), statusOut);

    await loadApps();
  },
};

function connectCard(lt) {
  return h('section', { class: 'card' }, h('h2', {}, 'Connect LifeTime'),
    h('p', {}, 'The dashboard reads LifeTime with a service account token. For your safety, the token is never typed into this page.'),
    h('ol', { class: 'doc' },
      h('li', {}, 'In LifeTime, open User Management > Service Accounts and create an account with read permissions. Copy its token.'),
      lt.host ? h('li', {}, 'The host is set: ', h('code', { text: lt.host }), '.') : h('li', {}, 'Set the LifeTime host in the project settings (the hostname without https://):', cli('oml config set outsystems.lifetimeHost lifetime.example.com')),
      lt.hasToken ? h('li', {}, 'The token is set.') : [
        h('li', {}, 'Stop this dashboard: use Stop dashboard at the bottom of the page list, or press Ctrl+C in its window.'),
        h('li', {}, 'In PowerShell, set the token and start the dashboard again:', cli('$env:OML_LIFETIME_TOKEN = Read-Host -MaskInput "LifeTime token"'), cli('oml dashboard'))]),
    h('p', { class: 'hint' }, 'The token stays in that PowerShell window only, never in a file.'));
}
