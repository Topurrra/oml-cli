import { h, add, icon } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, pullWarning, CONNECT_NOTE } from '../core/ui.js';
import { checkNames } from '../data/cookbook.js';
import { state, setModule } from '../core/state.js';
import { workset } from '../core/workset.js';

export default {
  id: 'home', group: 'Start', title: 'Home', icon: 'home',
  description: 'Check your setup, open the module you work on, and find your way around.',
  async render(main, ctx) {
    const doctorOut = h('div');
    const again = h('button', { class: 'button', type: 'button' }, 'Check again');
    // The page itself never calls `studio` or `pull`: they ask Service Studio for a session, which shows a dialog
    // there. The server check below comes from `doctor` (an MCP initialize only); the rest waits for a click.
    const studioStatus = h('div', { class: 'row small' }, h('span', { class: 'muted' }, 'Checking whether the MCP server answers…'));
    const studioOut = h('div');
    const studioAgain = h('button', { class: 'button', type: 'button' }, 'Show open modules in Service Studio');
    add(main,
      h('section', { class: 'card', 'aria-labelledby': 'setup-title' },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'setup-title' }, 'Setup check'), h('p', { class: 'muted small' }, 'What oml needs on this computer, and how to fix what is missing.')), again),
        doctorOut),
      h('section', { class: 'card', 'aria-labelledby': 'studio-title' },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', { id: 'studio-title' }, 'Service Studio'), h('p', { class: 'muted small' }, 'Modules open in Service Studio. Copy one to a file to explore it here. The open module does not change.'))),
        studioStatus,
        h('div', { class: 'field' }), studioAgain, h('p', { class: 'hint' }, CONNECT_NOTE),
        studioOut),
      h('section', { class: 'card', 'aria-labelledby': 'links-title' },
        h('h2', { id: 'links-title' }, 'Go to'),
        h('div', { class: 'links' }, ctx.pages.filter(p => p.id !== 'home').map(p =>
          h('a', { class: 'link-card', href: '#/' + p.id }, h('strong', {}, icon(p.id), p.title), h('span', {}, p.desc)))),
        state.info ? h('p', { class: 'muted small' }, 'Files the dashboard makes (pulled modules, OpenAPI, architecture pages) are saved in ', h('code', { text: state.info.workFolder }), '. Nothing is overwritten.') : null));

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
            const b = h('button', { class: 'button small', type: 'button', 'aria-label': `Copy ${a.name} from Service Studio to a file` }, 'Copy to a file');
            b.addEventListener('click', async () => {
              const r = await task(b, out, `Saving ${a.name}… Approve the request in Service Studio if it asks.`, async () => need(await api('pull', { module: a.name }), [0, 1]));
              if (r) { setModule(r.path); workset.add(r.path, { kind: 'studio', module: a.name }); add(out, h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path }), ' ', h('a', { href: '#/explorer' }, 'Open it in the Module explorer')), pullWarning(r), cli(r.cli)); }
            });
            return h('tr', {}, h('td', {}, a.name), h('td', { class: 'num' }, b));
          })))), out, cli(data.cli));
    };
    again.addEventListener('click', () => runDoctor(true));
    studioAgain.addEventListener('click', runStudio);
    await runDoctor(false);
  },
};
