import { h, add, icon } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need, inspect } from '../core/api.js';
import { task, cli, copyButton, download } from '../core/ui.js';
import { state } from '../core/state.js';
import { modulePicker } from '../core/workset.js';

export default {
  id: 'openapi', group: 'Module', title: 'OpenAPI', icon: 'openapi',
  description: 'Export the exposed REST APIs of a module as OpenAPI 3.0.3, fill in the missing details, and download the finished file.',
  render(main) {
    const body = h('div', { class: 'stack' });
    const picker = modulePicker(async (path, button, status) => {
      const data = await task(button, status, 'Reading the module…', () => inspect(path));
      if (data) show(path, data);
    });
    const servers = h('input', { type: 'text', id: 'oa-server', class: 'code', value: store.get('openapi-servers', ''), placeholder: 'https://dev.example.com/Orders/rest/OrdersAPI', spellcheck: 'false' });
    add(main, picker.el,
      h('section', { class: 'card', 'aria-label': 'OpenAPI options' }, h('div', { class: 'field' }, h('label', { for: 'oa-server' }, 'Server addresses (--server, optional)'), servers,
        h('p', { class: 'hint' }, 'The base address of the API in each environment. Separate several with ;. Empty: the file keeps a <FILL: …> placeholder.'))),
      body);

    function show(path, data) {
      const apis = (data.output && data.output.restServices) || [];
      if (!apis.length) { body.replaceChildren(h('div', { class: 'empty' }, 'This module exposes no REST API.')); return; }
      const out = h('div', { class: 'stack' });
      body.replaceChildren(h('section', { class: 'card' }, h('h2', {}, 'Exposed REST APIs'),
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['API', 'Methods', ''].map(t => h('th', {}, t)))),
          h('tbody', {}, apis.map(a => {
            const b = h('button', { class: 'button small primary', type: 'button', 'aria-label': 'Generate OpenAPI for ' + a.name }, 'Generate');
            b.addEventListener('click', () => generate(path, a.name, b, out));
            return h('tr', {}, h('td', {}, h('strong', {}, a.name), h('div', { class: 'tiny muted' }, a.baseUrl || '')),
              h('td', {}, (a.methods || []).map(m => h('div', { class: 'small mono' }, `${m.method} ${m.url}`))), h('td', { class: 'num' }, b));
          }))))), out);
    }

    async function generate(path, apiName, button, out) {
      store.set('openapi-servers', servers.value.trim());
      const list = servers.value.split(';').map(x => x.trim()).filter(Boolean);
      const data = await task(button, out, `Generating OpenAPI for ${apiName}…`, async () => need(await api('openapi', { path, api: apiName, servers: list })));
      if (!data) return;
      for (const file of data.files || []) add(out, openApiFile(file, data.cli));
    }

    function openApiFile(file, command) {
      let doc;
      try { doc = JSON.parse(file.text); } catch { return h('div', { class: 'message error' }, 'The generated file is not valid JSON.'); }
      const endpoints = [];
      for (const [p, ops] of Object.entries(doc.paths || {}))
        for (const [method, op] of Object.entries(ops || {})) if (typeof op === 'object') endpoints.push([method.toUpperCase(), p, op.summary || op.operationId || '']);
      const fills = [];
      (function walk(node, trail) {
        if (typeof node === 'string') { if (node.includes('<FILL:')) fills.push({ trail, text: node }); return; }
        if (node && typeof node === 'object') for (const [k, v] of Object.entries(node)) walk(v, trail.concat(Array.isArray(node) ? Number(k) : k));
      })(doc, []);
      const inputs = fills.map((f, i) => {
        const id = `fill-${Math.random().toString(36).slice(2)}-${i}`;
        const hint = (/<FILL:\s*([^>]*)>/.exec(f.text) || [])[1] || '';
        const input = h('input', { type: 'text', id, placeholder: hint });
        return { f, input, el: h('div', { class: 'field' }, h('label', { for: id }, f.trail.join(' › ')), input, h('p', { class: 'hint' }, 'Now: ', h('code', { text: f.text }))) };
      });
      const progress = h('span', { class: 'badge' });
      const update = () => { const n = inputs.filter(x => x.input.value.trim()).length; progress.className = 'badge ' + (n === inputs.length ? 'ok' : 'warn'); progress.textContent = `${n} of ${inputs.length} filled`; };
      inputs.forEach(x => x.input.addEventListener('input', update));
      update();
      const finished = () => {
        const copyDoc = JSON.parse(file.text);
        for (const { f, input } of inputs) {
          const value = input.value.trim();
          if (!value) continue;
          let node = copyDoc;
          for (const k of f.trail.slice(0, -1)) node = node[k];
          node[f.trail[f.trail.length - 1]] = /^\s*<FILL:[^>]*>\s*$/.test(f.text) ? value : f.text.replace(/<FILL:[^>]*>/, value);
        }
        return JSON.stringify(copyDoc, null, 2) + '\n';
      };
      return h('section', { class: 'card', 'aria-label': file.name },
        h('div', { class: 'card-head' }, h('div', {}, h('h2', {}, file.name), h('p', { class: 'muted small' }, 'Saved without your values in ', h('code', { text: file.path }))),
          h('div', { class: 'row' }, copyButton(finished, 'Copy JSON', 'OpenAPI'), h('button', { class: 'button small primary', type: 'button', onclick: () => download(file.name, finished(), 'application/json') }, icon('download'), 'Download'))),
        h('h3', {}, `Endpoints (${endpoints.length})`),
        h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, ['Method', 'Path', 'Summary'].map(t => h('th', {}, t)))),
          h('tbody', {}, endpoints.map(([m, p, s]) => h('tr', {}, h('td', {}, h('span', { class: 'badge accent' }, m)), h('td', { class: 'mono small' }, p), h('td', {}, s)))))),
        h('div', { class: 'card-head' }, h('h3', {}, `Placeholders (${fills.length})`), progress),
        fills.length ? h('p', { class: 'hint' }, 'Fill in what you know. Empty fields keep their <FILL: …> text, so you can finish later.') : h('p', { class: 'muted' }, 'Nothing to fill in.'),
        h('div', { class: 'grid-2' }, inputs.map(x => x.el)),
        cli(command));
    }
    if (state.module) picker.go();
  },
};
