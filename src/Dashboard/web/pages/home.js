import { iconButton } from '../core/table.js';
import { h, add, icon } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, pullWarning, CONNECT_NOTE } from '../core/ui.js';
import { checkNames } from '../data/cookbook.js';
import { state, setModule } from '../core/state.js';
import { workset } from '../core/workset.js';
import { when, signed } from '../core/chart.js';
import { NAV } from '../nav.js';

// One tile of "Today": a label, one number or word, one line under it. A link when it has a page to open.
function tile({ id, label, iconName, value, note, tone, href, onclick }) {
  const body = [h('span', { class: 'tile-label' }, icon(iconName), label), h('strong', { class: 'tile-value' + (tone ? ' ' + tone : '') }, value), h('span', { class: 'tile-note' }, note)];
  return href ? h('a', { class: 'tile', href, id }, body) : h('button', { class: 'tile', type: 'button', id, onclick }, body);
}

export default {
  id: 'home', group: 'Start', title: 'Home', icon: 'home',
  description: 'What oml did today, what waits for you, and your setup. Open the module you work on from here.',
  async render(main, ctx) {
    const tiles = h('div', { class: 'tiles', role: 'list', 'aria-label': 'Today' });
    const doctorOut = h('div');
    const again = iconButton('refresh', 'Check the setup again', null);
    // The page itself never calls `studio` or `pull`: they ask Service Studio for a session, which shows a dialog
    // there. The server check below comes from `doctor` (an MCP initialize only); the rest waits for a click.
    const studioStatus = h('div', { class: 'row small' }, h('span', { class: 'muted' }, 'Checking whether the MCP server answers…'));
    const studioOut = h('div');
    const studioAgain = h('button', { class: 'button', type: 'button' }, 'Show open modules in Service Studio');
    const setupTitle = h('h2', { id: 'setup-title', tabindex: '-1' }, 'Setup check');
    const setupTile = { id: 'tile-setup', label: 'Setup', iconName: 'check', value: '…', note: 'Checking…', onclick: () => { setupTitle.scrollIntoView({ block: 'start' }); setupTitle.focus({ preventScroll: true }); } };
    const tileSlots = [h('div', { role: 'listitem' }, tile(setupTile)), h('div', { role: 'listitem' }), h('div', { role: 'listitem' }), h('div', { role: 'listitem' })];
    add(tiles, tileSlots);
    add(main,
      h('section', { 'aria-labelledby': 'today-title' }, h('h2', { id: 'today-title', class: 'section-title' }, 'Today'), tiles),
      h('section', { class: 'card', 'aria-labelledby': 'setup-title' },
        h('div', { class: 'card-head' }, h('div', {}, setupTitle, h('p', { class: 'muted small' }, 'What oml needs on this computer, and how to fix what is missing.')), again),
        doctorOut),
      h('section', { class: 'card', 'aria-labelledby': 'studio-title' },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'studio-title' }, 'Service Studio'), h('p', { class: 'muted small' }, 'Modules open in Service Studio. Copy one to a file to explore it here. The open module does not change.'))),
        studioStatus,
        h('div', { class: 'field' }), studioAgain, h('p', { class: 'hint' }, CONNECT_NOTE),
        studioOut),
      h('section', { 'aria-labelledby': 'links-title' },
        h('h2', { id: 'links-title', class: 'section-title' }, 'Go to'),
        h('div', { class: 'links' }, ctx.pages.filter(p => p.id !== 'home' && NAV.some(g => g.ids.includes(p.id))).map(p =>
          h('a', { class: 'link-card', href: '#/' + p.id }, h('strong', {}, icon(p.id), p.title), h('span', {}, p.description)))),
        state.info ? h('p', { class: 'muted small' }, 'Files the dashboard makes (pulled modules, OpenAPI, architecture pages) are saved in ', h('code', { text: state.info.workFolder }), '. Nothing is overwritten.') : null));

    // The other three tiles: one cheap local read (usage log, check history, code-mode logs), no login.
    const overview = async () => {
      let o;
      try { o = await api('overview'); } catch { return; }
      const sc = o.score, log = o.log || {}, code = o.code || {};
      tileSlots[1].replaceChildren(tile(sc
        ? { id: 'tile-score', label: 'Environment score', iconName: 'server', href: '#/server', value: String(sc.score),
            tone: sc.change > 0 ? 'ok' : sc.change < 0 ? 'fail' : '',
            note: (sc.change !== null && sc.change !== undefined ? `${sc.change > 0 ? '▲' : sc.change < 0 ? '▼' : ''} ${signed(sc.change)} · ` : '') + when(sc.time) }
        : { id: 'tile-score', label: 'Environment score', iconName: 'server', href: '#/server', value: 'No check yet', note: o.server && o.server.configured ? 'Run the first check on the server' : 'Connect the platform server' }));
      tileSlots[2].replaceChildren(tile(log.enabled
        ? { id: 'tile-commands', label: 'oml commands today', iconName: 'activity', href: '#/activity', value: String(log.today || 0),
            tone: log.failed ? 'warn' : '', note: log.today ? `${log.failed || 0} failed · last at ${new Date(log.last).toLocaleTimeString([], { timeStyle: 'short' })}` : 'No command yet today' }
        : { id: 'tile-commands', label: 'oml commands today', iconName: 'activity', href: '#/activity', value: 'Log off', note: 'OML_LOG is 0: nothing is counted' }));
      tileSlots[3].replaceChildren(tile(code.waiting
        ? { id: 'tile-waiting', label: 'Waiting for you', iconName: 'agents', href: '#/activity', value: String(code.waiting), tone: 'warn',
            note: code.waiting === 1 ? 'A code-mode program waits for your approval' : 'Code-mode programs wait for your approval' }
        : { id: 'tile-waiting', label: 'Waiting for you', iconName: 'agents', href: '#/activity', value: 'Nothing', note: code.recent ? `${code.recent} recent code-mode programs, none paused` : 'No code-mode program in this folder' }));
    };

    // The server keeps the last result for this dashboard run (doctor contacts Service Studio's MCP server);
    // "Check again" asks for a fresh one.
    const runDoctor = async fresh => {
      const data = await task(again, doctorOut, 'Checking your setup…', async () => need(await api('doctor' + (fresh ? '?fresh=1' : '')), [0, 1]));
      if (!data || !data.output || !data.output.checks) return;
      const label = { ok: 'OK', warn: 'Check', fail: 'Fix needed', skip: 'Skipped' };
      const checks = data.output.checks;
      const count = status => checks.filter(c => c.status === status).length;
      const open = checks.filter(c => c.status === 'warn' || c.status === 'fail');
      const names = open.map(c => checkNames[c.name] || c.name).join(', ');
      const failed = checks.filter(c => c.status === 'fail').map(c => checkNames[c.name] || c.name).join(', ');
      const summary = !data.output.ok ? `Something must be fixed: ${failed}.`
        : `Ready: ${count('ok')} OK` + (open.length ? `, ${open.length} to check (${names})` : '') + (count('skip') ? `, ${count('skip')} skipped` : '') + '.';
      tileSlots[0].replaceChildren(tile({ ...setupTile, value: !data.output.ok ? 'Fix needed' : 'Ready', tone: !data.output.ok ? 'fail' : open.length ? 'warn' : 'ok',
        note: !data.output.ok ? failed : open.length ? `${open.length} to check: ${names}` : `All ${count('ok')} checks OK` }));
      const mcp = data.output.checks.find(c => c.name === 'studioMcp');
      if (mcp) studioStatus.replaceChildren(h('span', { class: 'badge ' + (mcp.status === 'ok' ? 'ok' : 'warn') }, h('span', { class: 'dot' }), mcp.status === 'ok' ? 'MCP server answers' : 'MCP server not reachable'),
        h('span', { class: 'muted' }, mcp.status === 'ok' ? mcp.detail : 'Start it in Service Studio: Edit > MCP Server > Start MCP server.'));
      add(doctorOut,
        h('div', { class: 'row' }, h('span', { class: 'badge ' + (!data.output.ok ? 'fail' : open.length ? 'warn' : 'ok') }, h('span', { class: 'dot' }), data.output.ok ? 'Ready to use' : 'Fix needed'),
          h('span', {}, summary), data.checkedAt ? h('span', { class: 'muted small' }, 'Checked at ' + data.checkedAt) : null),
        h('details', { class: 'doctor-details', open: open.length > 0 }, h('summary', {}, `Show the ${checks.length} checks`),
          h('ul', { class: 'checks', 'aria-label': 'Setup checks' }, checks.map(c => h('li', {},
            h('div', {}, h('span', { class: 'badge ' + c.status }, h('span', { class: 'dot' }), label[c.status] || c.status)),
            h('div', {}, h('div', { class: 'what' }, checkNames[c.name] || c.name), h('div', { class: 'detail', text: c.detail }),
              c.fix ? h('div', { class: 'fix' + (c.status === 'fail' ? ' fail' : '') }, h('strong', {}, 'Fix: '), c.fix) : null))))),
        cli('oml doctor --text'));
    };
    const runStudio = async () => {
      const data = await task(studioAgain, studioOut, 'Asking Service Studio…', async () => api('studio'));
      if (!data) return;
      if (data.code !== 0) {
        add(studioOut, h('div', { class: 'message info' }, h('p', {}, 'Service Studio\'s MCP server does not answer. You only need it to pull, publish or refresh references.'),
          h('p', { class: 'small muted' }, 'To start it: in Service Studio, open Edit > MCP Server > Start MCP server.')), cli(data.cli));
        return;
      }
      const apps = (data.output && data.output.apps) || [];
      const open = apps.filter(a => a.isOpen);
      const out = h('div');
      add(studioOut, open.length === 0 ? h('p', { class: 'muted' }, 'Service Studio answers, but no module is open.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, h('th', {}, 'Open module'), h('th', {}, ''))),
          h('tbody', {}, open.map(a => {
            const b = iconButton('download', `Copy ${a.name} from Service Studio to a file`, null, { small: true });
            b.addEventListener('click', async () => {
              const r = await task(b, out, `Saving ${a.name}… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: a.name }), [0, 1]));
              if (r) { setModule(r.path); workset.add(r.path, { kind: 'studio', module: a.name }); add(out, h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path }), ' ', h('a', { href: '#/explorer' }, 'Open it in the Module explorer')), pullWarning(r), cli(r.cli)); }
            });
            return h('tr', {}, h('td', {}, a.name), h('td', { class: 'num' }, b));
          })))), out, cli(data.cli));
    };
    again.addEventListener('click', () => runDoctor(true));
    studioAgain.addEventListener('click', runStudio);
    await Promise.all([overview(), runDoctor(false)]);
  },
};
