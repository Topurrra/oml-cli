import { h, add } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, errorBox, ltDownload, reportView } from '../core/ui.js';
import { state } from '../core/state.js';

export default {
  id: 'lifetime', group: 'Team', title: 'LifeTime', icon: 'lifetime',
  description: 'Environments, applications and deployments, read only. Tagging and deploying stay in the CLI, where you confirm each step.',
  async render(main) {
    const lt = state.info && state.info.lifeTime;
    if (!lt || !lt.configured) {
      add(main, h('section', { class: 'card' }, h('h2', {}, 'Connect LifeTime'),
        h('p', {}, 'The dashboard reads LifeTime with a service account token. For your safety, the token is never typed into this page.'),
        h('ol', { class: 'doc' },
          h('li', {}, 'In LifeTime, open User Management > Service Accounts and create an account with read permissions. Copy its token.'),
          h('li', {}, 'Stop this dashboard: use Stop dashboard at the bottom of the page list, or press Ctrl+C in its window.'),
          h('li', {}, 'In PowerShell, set the two variables and start the dashboard again:')),
        cli('$env:OML_LIFETIME_HOST = "lifetime.example.com"'),
        cli('$env:OML_LIFETIME_TOKEN = Read-Host -MaskInput "LifeTime token"'),
        cli('oml dashboard'),
        h('p', { class: 'hint' }, 'The host is the LifeTime hostname without https://. The token stays in that PowerShell window only.')));
      return;
    }
    const envOut = h('div'), appOut = h('div'), statusOut = h('div'), teamOut = h('div', { class: 'stack' });
    const envSelect = h('select', { id: 'lt-env' }, h('option', { value: '' }, 'All environments'));
    const teamSelect = h('select', { id: 'lt-team' }, h('option', { value: '' }, 'All teams'));
    const reportTeam = h('select', { id: 'lt-report-team' });
    const keyInput = h('input', { type: 'text', id: 'lt-key', class: 'code', placeholder: '6f1c2a9e-3b7d-4c55-9e0a-1d2b3c4d5e6f', spellcheck: 'false' });
    const lookup = h('button', { class: 'button', type: 'button' }, 'Look up');
    const archButton = h('button', { class: 'button primary', type: 'button' }, 'Architecture page');
    const checkButton = h('button', { class: 'button', type: 'button' }, 'Check');
    add(main,
      h('p', { class: 'muted small' }, 'Connected to ', h('code', { text: lt.host }), '. Read only: this page never tags, deploys, aborts or continues.'),
      h('section', { class: 'card' }, h('h2', {}, 'Environments'), envOut),
      h('section', { class: 'card' }, h('div', { class: 'card-head' }, h('h2', {}, 'Applications'),
        h('div', { class: 'row' }, h('label', { for: 'lt-team', class: 'sr-only' }, 'Team'), teamSelect, h('label', { for: 'lt-env', class: 'sr-only' }, 'Environment'), envSelect)), appOut),
      h('section', { class: 'card', 'aria-labelledby': 'lt-team-title' }, h('h2', { id: 'lt-team-title' }, 'Team reports'),
        h('p', { class: 'muted small' }, "Downloads the team's applications (read only) to a temporary folder, makes the page, then deletes the packages."),
        h('div', { class: 'field' }, h('label', { for: 'lt-report-team' }, 'Team'), h('div', { class: 'row' }, reportTeam, archButton, checkButton)), teamOut),
      h('section', { class: 'card' }, h('h2', {}, 'Deployment status'),
        h('div', { class: 'field' }, h('label', { for: 'lt-key' }, 'Deployment key'), h('div', { class: 'row' }, h('div', { class: 'grow' }, keyInput), lookup)), statusOut));

    const envs = await task(null, envOut, 'Reading environments…', async () => need(await api('lt/environments')));
    if (envs) {
      const list = Array.isArray(envs.output) ? envs.output : [];
      add(envOut, h('div', { class: 'links' }, list.map(e => h('div', { class: 'link-card' }, h('strong', {}, e.Name), h('span', {}, [e.EnvironmentType, e.Key].filter(Boolean).join(' · '))))), cli(envs.cli));
      list.forEach(e => add(envSelect, h('option', { value: e.Name }, e.Name)));
    }
    let teamsCli = '';
    try {
      const teams = need(await api('lt/teams'));
      teamsCli = teams.cli;
      (Array.isArray(teams.output) ? teams.output : []).forEach(t => { const name = t.name || t.Name; add(teamSelect, h('option', { value: name }, name)); add(reportTeam, h('option', { value: name }, name)); });
    } catch (e) { add(teamOut, errorBox(e)); }

    const loadApps = async () => {
      const env = envSelect.value, team = teamSelect.value;
      const data = await task(envSelect, appOut, 'Reading applications… LifeTime can take some seconds.', async () => need(await api('lt/apps' + (env ? '?env=' + encodeURIComponent(env) : ''))));
      if (!data) return;
      let apps = Array.isArray(data.output) ? data.output : [];
      let teamData = null;
      if (team) {
        try { teamData = need(await api('lt/team-apps?team=' + encodeURIComponent(team))); }
        catch (e) { add(appOut, errorBox(e)); return; }
        const keys = new Set((Array.isArray(teamData.output) ? teamData.output : []).map(a => a.Key || a.key));
        apps = apps.filter(a => keys.has(a.key));
      }
      const rows = apps.flatMap(a => (a.environments || []).length ? a.environments.map((e, i) => [i === 0 ? a.name : '', a.name, e.environment, e.version, e.isModified]) : [[a.name, a.name, '', '', null]]);
      add(appOut, apps.length === 0 ? h('p', { class: 'muted' }, team ? 'No applications of this team.' : 'No applications.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Application', 'Environment', 'Version (tag)', 'Changed since tag'].map(t => h('th', {}, t)))),
          h('tbody', {}, rows.map(([shown, name, env, version, modified]) => {
            const result = h('div');
            const get = env ? h('button', { class: 'button small', type: 'button', 'aria-label': `Download ${name} from ${env}` }, 'Download .oap') : null;
            if (get) get.addEventListener('click', () => ltDownload(name, env, false, get, result));
            return h('tr', {}, h('td', {}, h('strong', {}, shown)), h('td', {}, env || '', get ? h('div', {}, get) : null, result), h('td', { class: 'mono small' }, version || '—'),
              h('td', {}, modified === true ? h('span', { class: 'badge warn' }, 'Yes, not tagged') : modified === false ? h('span', { class: 'badge ok' }, 'No') : ''));
          })))),
        h('p', { class: 'hint' }, 'Download .oap saves the package as it runs in that environment. To work on its modules, use ', h('a', { href: '#/modules' }, 'Get modules'), ' > LifeTime.'),
        cli(data.cli), teamData ? cli(teamData.cli) : null);
    };
    envSelect.addEventListener('change', loadApps);
    teamSelect.addEventListener('change', loadApps);
    const teamReport = async (kind, button) => {
      const team = reportTeam.value;
      if (!team) { reportTeam.focus(); return; }
      const data = await task(button, teamOut, `Downloading the applications of ${team} and making the ${kind === 'check' ? 'check report' : 'architecture page'}… This can take minutes.`,
        async () => need(await api('lt/report', { team, kind })));
      if (data) add(teamOut, reportView(data, (kind === 'check' ? 'Check report of team ' : 'Architecture page of team ') + team, kind));
    };
    archButton.addEventListener('click', () => teamReport('architecture', archButton));
    checkButton.addEventListener('click', () => teamReport('check', checkButton));
    if (teamsCli) add(teamOut, cli(teamsCli));
    lookup.addEventListener('click', async () => {
      const key = keyInput.value.trim();
      if (!key) { keyInput.focus(); return; }
      const data = await task(lookup, statusOut, 'Reading the deployment…', async () => need(await api('lt/status?key=' + encodeURIComponent(key)), [0, 1]));
      if (!data) return;
      const s = (data.output && data.output.status) || {};
      const status = String(s.DeploymentStatus || '').toLowerCase();
      add(statusOut, h('div', { class: 'row' }, h('span', { class: 'badge ' + (status === 'finished_successful' ? 'ok' : /error|abort/.test(status) ? 'fail' : 'warn') }, status || 'unknown')),
        (s.Messages || []).length ? h('ul', { class: 'checklist' }, s.Messages.map(m => h('li', {}, typeof m === 'string' ? m : JSON.stringify(m)))) : null,
        cli(data.cli));
    });
    keyInput.addEventListener('keydown', e => { if (e.key === 'Enter') lookup.click(); });
    await loadApps();
  },
};
