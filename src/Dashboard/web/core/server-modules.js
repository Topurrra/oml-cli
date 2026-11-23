// The modules and versions on the platform server, read with oml server (REST, Service Studio closed): an application
// filter, a table with search and paging, and per module its versions with "Save to a file" (and, on the Platform
// server page, a compare of two versions). Shared by the Platform server page and the Get modules page.
import { h, add } from './dom.js';
import { api, need } from './api.js';
import { task, cli, errorBox } from './ui.js';
import { when } from './chart.js';
import { reviewResult } from './review-view.js';
import { viewer } from './viewer.js';
import { setModule } from './state.js';
import { workset } from './workset.js';
import { dataTable, actionButton } from './table.js';

export function serverModules(server, opts = {}) {
  const out = h('div', { class: 'stack' });
  const status = h('div');
  const table = dataTable({
    caption: 'Modules on the server', empty: 'No modules.', searchPlaceholder: 'Search modules and applications',
    columns: [
      { key: 'name', label: 'Module', width: '32%', render: m => h('strong', {}, m.name) },
      { key: 'application', label: 'Application', width: '30%' },
      { key: 'lastVersion', label: 'Latest version', width: '38%', value: m => m.lastVersion || 0, render: m => [
        m.lastVersion ? 'v' + m.lastVersion : '', m.uploadedBy ? h('span', { class: 'muted' }, ` by ${m.uploadedBy}`) : null,
        m.uploadedDate ? h('div', { class: 'tiny muted' }, when(m.uploadedDate)) : null,
        m.lastVersionId && m.publishedVersionId !== m.lastVersionId ? h('div', {}, h('span', { class: 'badge warn' }, 'Latest not published')) : null] },
    ],
    filters: [{ key: 'application', label: 'Applications' }],
    detail: m => versionsPanel(m.name, opts),
    rowKey: m => m.name,
  });
  table.el.hidden = true;
  const load = async button => {
    const data = await task(button, status, 'Logging in and reading the module list…', async () => need(await api('server/modules')));
    if (!data) return;
    const modules = (data.output && data.output.modules) || [];
    table.update(modules);
    table.el.hidden = false;
    if (opts.onCount) opts.onCount(modules.length);
    status.replaceChildren(h('p', { class: 'hint' }, `${modules.length} modules on ${data.output.host}.`), cli(data.cli));
  };
  const show = actionButton('server', 'Show the modules', () => load(show), { primary: !opts.secondary });
  show.disabled = !server.configured;
  add(out, server.configured ? null : h('p', { class: 'hint' }, 'Connect the platform server first: see the Platform server page.'),
    h('div', { class: 'toolbar' }, show), table.el, status);
  return { el: out, load: () => load(show) };
}

// The versions of one module, loaded when its row opens.
function versionsPanel(name, opts) {
  const panel = h('div', { class: 'stack' });
  task(null, panel, `Reading the versions of ${name}…`, async () => need(await api('server/versions?module=' + encodeURIComponent(name))))
    .then(data => { if (data) add(panel, versionsView(name, data.output.versions || [], opts), cli(data.cli)); });
  return panel;
}

function versionsView(name, versions, opts) {
  const out = h('div', { class: 'stack' });
  const save = v => {
    const b = actionButton('download', 'Save', null, { small: true, aria: `Save ${name} version ${v.version} to a new file` });
    b.addEventListener('click', async () => {
      const r = await task(b, out, `Saving ${name} version ${v.version}…`, async () => need(await api('server/pull', { module: name, version: v.version })));
      if (!r || !r.path) return;
      setModule(r.path); workset.add(r.path, { kind: 'server', module: name, version: v.version });
      add(out, h('div', { class: 'message' }, 'Saved to ', h('code', { text: r.path }), ' and added to the working set. ', h('a', { href: '#/explorer' }, 'Open it in the Module explorer')), cli(r.cli));
      if (opts.onSaved) opts.onSaved(r.path);
    });
    return b;
  };
  const table = dataTable({
    caption: `Versions of ${name}`, rows: versions, search: false, sort: { key: 'version', desc: true },
    columns: [
      { key: 'version', label: 'Version', width: '30%', render: v => ['v' + v.version, v.published ? h('span', {}, ' ', h('span', { class: 'badge ok' }, 'Published')) : null] },
      { key: 'uploadedDate', label: 'Uploaded', width: '50%', render: v => [when(v.uploadedDate), v.uploadedBy ? h('span', { class: 'muted' }, ' by ' + v.uploadedBy) : null] },
      { key: 'save', label: '', width: '20%', sortable: false, render: save },
    ],
  });
  if (!opts.compare || versions.length < 2) return h('div', { class: 'stack' }, table.el, out);
  const id = 'cmp-' + name.replace(/\W/g, '_');
  const pick = (suffix, label, value) => h('div', { class: 'field' }, h('label', { for: id + suffix }, label),
    h('select', { id: id + suffix }, versions.map(v => h('option', { value: String(v.version), selected: v.version === value }, 'v' + v.version + (v.published ? ' (published)' : '')))));
  const published = versions.find(v => v.published);
  const to = versions[0].version, from = published && published.version !== to ? published.version : versions[1].version;
  const fromField = pick('-from', 'From', from), toField = pick('-to', 'To', to);
  const result = h('div', { class: 'stack' });
  const compare = actionButton('review', 'Compare', async () => {
    const [a, b] = [fromField.querySelector('select').value, toField.querySelector('select').value];
    if (a === b) { result.replaceChildren(errorBox(new Error('Choose two different versions.'))); return; }
    const data = await task(compare, result, `Downloading v${a} and v${b} and comparing…`, async () => need(await api('server/review', { module: name, from: a, to: b })));
    if (!data) return;
    add(result, reviewResult(data, { title: `${name}: v${a} → v${b}`, heading: 'h3' }),
      data.view ? viewer({ view: data.view, title: 'Review report', width: 1200, height: 900, themed: true }) : null);
  }, { primary: true });
  return h('div', { class: 'stack' }, table.el, out,
    h('div', { class: 'compare' }, h('h3', {}, 'Compare two versions'), h('div', { class: 'row compare-row' }, fromField, toField, compare),
      h('p', { class: 'hint' }, 'Production risks, changes and new check findings between the two versions.')), result);
}
