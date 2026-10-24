import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, copyButton, segmented, toast, fileName } from '../core/ui.js';
import { onLeave } from '../core/lifecycle.js';
import { state } from '../core/state.js';
import { workset } from '../core/workset.js';
import { PROMPTS } from '../data/prompts.js';

export default {
  id: 'prompt', group: 'Agents', title: 'Prompt builder', icon: 'prompt',
  description: 'Write a clear task for an AI agent: the modules, what to build, the exact elements to use and what it may not do.',
  async render(main) {
    const saved = store.get('prompt', {});
    const defaults = { mode: 'small', task: '', elements: [], noInstall: true, noPublish: true, preview: false, validate: true, review: true };
    const opts = { ...defaults, ...saved };
    const moduleInput = h('input', { type: 'text', id: 'p-module', class: 'code', value: workset.list().length ? '' : state.module, placeholder: 'C:\\Work\\Orders.oml', spellcheck: 'false' });
    const taskInput = h('textarea', { id: 'p-task', placeholder: 'Add an entity Invoice (Number Text 20 mandatory, Amount Decimal, IssuedOn Date) with Invoice_CreateOrUpdate and Invoice_Delete server actions, and a Reactive list screen InvoiceList.' });
    taskInput.value = opts.task;
    const elements = [...opts.elements];
    const chips = h('div', { class: 'signature', 'aria-live': 'polite' });
    const setList = h('div');
    const findName = h('input', { type: 'search', id: 'p-find', class: 'code', placeholder: 'Customer_Validate or Customer*', spellcheck: 'false' });
    const findIn = h('input', { type: 'text', id: 'p-in', class: 'code', placeholder: 'C:\\Work\\Producers', value: store.get('producers', ''), spellcheck: 'false' });
    const findButton = h('button', { class: 'button', type: 'button' }, 'Find');
    const findOut = h('div', { class: 'stack' });
    const checks = {
      validate: ['Stage the result and validate it', opts.validate],
      review: ['Review the change: oml review … --md', opts.review],
      noInstall: ['Do not install', opts.noInstall],
      noPublish: ['Do not publish', opts.noPublish],
      preview: ['Preview the UI at 1366 and 1920 px (add dark or 390 px only when needed)', opts.preview],
    };
    const boxes = Object.fromEntries(Object.entries(checks).map(([k, [, v]]) => [k, h('input', { type: 'checkbox', id: 'p-' + k, checked: v })]));
    const output = h('textarea', { id: 'p-out', class: 'code', readonly: true, rows: 14 });

    const clean = p => p.trim().replace(/^"(.*)"$/, '$1');
    const staged = p => p.replace(/\.oml$/i, '') + '.staged.oml';
    const specFile = m => (m.name || fileName(m.path).replace(/\.oml$/i, '')) + '.spec.json';
    // The modules of this task: the working set, plus a typed module that is not in it.
    function modules() {
      const list = workset.list();
      const typed = clean(moduleInput.value);
      if (typed && !list.some(m => m.path.toLowerCase() === typed.toLowerCase())) list.unshift({ path: typed, name: fileName(typed).replace(/\.oml$/i, ''), role: 'change', source: { kind: 'path' } });
      return { change: list.filter(m => m.role !== 'read'), read: list.filter(m => m.role === 'read'), all: list };
    }

    function text() {
      const { change, read, all } = modules();
      const lines = [];
      const template = PROMPTS[opts.mode === 'architect' ? 1 : 0];
      const head = template.text.split('\n').slice(0, 2);
      lines.push(head[0], head[1]);
      if (opts.mode === 'architect') lines.push('Plan lanes, give each worker subagent its brief, check every result yourself, and merge the parts with one oml apply.');
      lines.push('');
      lines.push(change.length === 0 ? 'Module: (choose a module to change).' : change.length === 1 ? `Module: ${change[0].path}.` : `Modules to change: ${change.map(m => m.path).join(', ')}.`);
      const taskText = taskInput.value.trim();
      if (taskText) lines.push(/[.!?]$/.test(taskText) ? taskText : taskText + '.');
      if (read.length) lines.push(`Read only (producers; read them with --in, never change them): ${read.map(m => m.path).join(', ')}.`);
      if (elements.length) {
        lines.push(`Use these elements from other modules: ${elements.map(e => e.name).join(', ')}.`);
        const known = new Set(all.map(m => m.path.toLowerCase()));
        const files = [...new Set(elements.map(e => e.file).filter(f => f && !known.has(f.toLowerCase())))];
        if (files.length) lines.push(`The producer module file${files.length > 1 ? 's are' : ' is'} ${files.join(', ')}.`);
      }
      if (all.length) {
        lines.push('', 'Where the modules come from:');
        for (const m of all) lines.push(`- ${fileName(m.path)}: ${workset.sourceLine(m)}`);
        lines.push('');
      }
      if (boxes.preview.checked) lines.push('Preview the UI at 1366 and 1920 px (add dark or 390 px only when needed).');
      if (boxes.validate.checked) lines.push('Stage the result and validate it.');
      if (boxes.review.checked && !change.length) lines.push('Then run oml review on the module and its staged file with --md, and show me the Markdown.');
      if (boxes.review.checked) for (const m of change)
        lines.push(`Then run oml review ${m.path} ${staged(m.path)} --md ${m.path.replace(/\.oml$/i, '')}.review.md and show me the Markdown.`);
      const not = [boxes.noInstall.checked && 'install', boxes.noPublish.checked && 'publish'].filter(Boolean);
      if (not.length) lines.push(`Do not ${not.join(' or ')}.`);
      return lines.join('\n').replace(/\n{3,}/g, '\n\n');
    }

    // The same task as `oml run` steps: read the modules, then (after the agent wrote spec.json) apply, validate and review.
    function steps() {
      const { change, read } = modules();
      const ins = read.flatMap(m => ['--in', m.path]);
      const list = [];
      for (const m of change) list.push(['inspect', m.path, '--own']);
      if (change.length && elements.length) list.push(['describe', change[0].path, ...elements.map(e => e.name), ...ins]);
      for (const m of change) {
        list.push(['apply', m.path, specFile(m), staged(m.path)]);
        if (boxes.validate.checked) list.push(['validate', staged(m.path), '--warnings']);
        if (boxes.review.checked) list.push(['review', m.path, staged(m.path), '--md', m.path.replace(/\.oml$/i, '') + '.review.md']);
      }
      return `// Write ${change.map(specFile).join(', ')} first, then: oml run steps.json --continue. Install and publish are not run steps: they need your OK.\n` +
        JSON.stringify({ steps: list }, null, 2) + '\n';
    }

    const stepsButton = copyButton(() => steps(), 'Copy as oml run steps', 'Steps');
    const stepsHint = h('p', { class: 'hint', 'aria-live': 'polite' });
    function build() {
      output.value = text();
      const changing = modules().change.length > 0;
      stepsButton.disabled = !changing;
      stepsHint.textContent = changing ? '' : 'Run steps need a module to change: choose one in Get modules, or type one.';
      store.set('prompt', { mode: opts.mode, task: taskInput.value, elements, ...Object.fromEntries(Object.entries(boxes).map(([k, b]) => [k, b.checked])) });
      chips.replaceChildren(...elements.map((e, i) => h('span', { class: 'chip' }, e.name,
        h('button', { type: 'button', 'aria-label': 'Remove ' + e.name, onclick: () => { elements.splice(i, 1); build(); } }, '×'))));
      if (!elements.length) add(chips, h('span', { class: 'muted small' }, 'No elements yet. Find them below and add them, so the names are exact.'));
      const set = workset.list();
      setList.replaceChildren(set.length === 0 ? h('p', { class: 'hint' }, 'No working set yet. ', h('a', { href: '#/modules' }, 'Get modules'), ', or type one module below.') :
        h('ul', { class: 'checklist', 'aria-label': 'Modules of the task' }, set.map(m => {
          const id = 'p-role-' + Math.random().toString(36).slice(2);
          const role = h('select', { id, onchange: e => workset.setRole(m.path, e.target.value) }, h('option', { value: 'change' }, 'Change'), h('option', { value: 'read' }, 'Read only'));
          role.value = m.role;
          return h('li', {}, h('label', { for: id, class: 'sr-only' }, 'Role of ' + m.name), role, h('span', {}, h('strong', {}, m.name), h('span', { class: 'tiny muted' }, ' · ' + workset.sourceLine(m))));
        })));
    }
    [moduleInput, taskInput].forEach(i => i.addEventListener('input', build));
    Object.values(boxes).forEach(b => b.addEventListener('change', build));
    onLeave(workset.onChange(build));

    const runFind = async () => {
      const name = findName.value.trim();
      if (!name) { findName.focus(); return; }
      store.set('producers', findIn.value.trim());
      const { change, read } = modules();
      const folders = [...findIn.value.split(';').map(s => s.trim()).filter(Boolean), ...read.map(m => m.path)];
      const module = change[0] && change[0].path;
      const data = await task(findButton, findOut, 'Searching…', async () => need(await api('find', { name, path: module || undefined, in: folders })));
      if (!data) return;
      const matches = ((data.output && data.output.matches) || []).filter(m => m.source !== 'this module');
      add(findOut, matches.length === 0 ? h('p', { class: 'muted' }, 'No element with that name in referenced or producer modules.') :
        h('div', { class: 'table-wrap' }, h('table', {}, h('tbody', {}, matches.map(m => h('tr', {},
          h('td', {}, h('strong', {}, m.qualified), h('div', { class: 'tiny muted' }, `${m.kind} · ${m.source}`)),
          h('td', { class: 'num' }, h('button', { class: 'button small', type: 'button', 'aria-label': 'Add ' + m.qualified, onclick: () => {
            if (!elements.some(e => e.name === m.qualified)) elements.push({ name: m.qualified, file: m.source === 'producer file' ? m.file : null });
            build(); toast('Added ' + m.qualified);
          } }, 'Add'))))))), cli(data.cli));
    };
    findButton.addEventListener('click', runFind);
    findName.addEventListener('keydown', e => { if (e.key === 'Enter') runFind(); });

    const kindControl = segmented('Task kind', [['small', 'Small task'], ['architect', 'Architect']], opts.mode, v => { opts.mode = v; build(); });

    add(main, h('div', { class: 'grid-2' },
      h('section', { class: 'card', 'aria-label': 'Task details' },
        h('div', { class: 'field' }, h('span', { class: 'label' }, h('strong', { class: 'small' }, 'Task kind')), kindControl),
        h('div', { class: 'field' }, h('span', { class: 'label' }, h('strong', { class: 'small' }, '1. Modules (working set)')), setList),
        h('div', { class: 'field' }, h('label', { for: 'p-module' }, 'Another module to change (optional)'), moduleInput),
        h('div', { class: 'field' }, h('label', { for: 'p-task' }, '2. Task: what should the agent build or change?'), taskInput,
          h('p', { class: 'hint' }, 'Describe one change with exact names, types, screens and behaviour. One task per message works best; split big features: data model first, then logic, then screens.')),
        h('div', { class: 'field' }, h('span', { class: 'label' }, h('strong', { class: 'small' }, '3. Elements from other modules')), chips),
        h('div', { class: 'grid-2' },
          h('div', { class: 'field' }, h('label', { for: 'p-find' }, 'Find an element'), findName),
          h('div', { class: 'field' }, h('label', { for: 'p-in' }, 'Producer folders (optional)'), findIn, h('p', { class: 'hint' }, 'Read only modules are searched too.'))),
        h('div', { class: 'field' }, h('div', { class: 'row' }, findButton), findOut),
        h('fieldset', { class: 'field' }, h('legend', { class: 'sr-only' }, 'Limits for the agent'),
          h('p', {}, h('strong', { class: 'small' }, '4. Limits')),
          Object.entries(checks).map(([k, [label]]) => h('label', { class: 'check', for: 'p-' + k }, boxes[k], label)))),
      h('section', { class: 'card', 'aria-label': 'Task for the agent' },
        h('div', { class: 'card-head' }, h('h2', {}, 'Task for the agent'),
          h('div', { class: 'row' }, copyButton(() => output.value, 'Copy task', 'Task'), stepsButton)), stepsHint,
        h('label', { for: 'p-out', class: 'sr-only' }, 'Task text'), output,
        h('p', { class: 'hint' }, 'Paste it into your agent. Agents ask you before anything that changes a real module or an environment.'),
        cli('oml run steps.json --continue'))));
    build();

    // tests/dashboard-check.ps1 opens #/prompt?selftest=1: the working set API and the prompt text are checked in the
    // page itself, then the person's own working set is put back. Success sets <html data-selftest="ok">.
    if (new URLSearchParams(location.hash.split('?')[1] || '').get('selftest') === '1') selfTest();
    function selfTest() {
      const saved = ['workset', 'sources', 'recent'].map(k => [k, store.get(k, null)]);
      const fail = m => { throw new Error('Prompt self-test: ' + m); };
      const has = (t, part) => { if (!t.includes(part)) fail('missing "' + part + '" in:\n' + t); };
      const root = document.documentElement;
      try {
        workset.clear();
        opts.mode = 'small';
        let calls = 0;
        const stop = workset.onChange(() => calls++);
        workset.add('C:\\T\\Orders.oml', { kind: 'studio', module: 'Orders' });
        workset.add('C:\\T\\Shop.oml', { kind: 'lifetime', app: 'Shop', env: 'Development' }, 'read');
        workset.add('c:\\t\\orders.oml', { kind: 'studio', module: 'Orders' }); // the same file in other case: still one entry
        stop();
        workset.add('C:\\T\\Extra.oml', { kind: 'folder', folder: 'C:\\T' });
        workset.remove('C:\\T\\Extra.oml');
        const list = workset.list();
        if (list.length !== 2 || calls !== 3) fail(`list ${list.length}, calls ${calls}`);
        if (workset.changes().map(m => m.name).join() !== 'Orders' || workset.reads().map(m => m.name).join() !== 'Shop') fail('roles');
        if (list[0].source.kind !== 'studio' || !list[0].source.at) fail('source');
        moduleInput.value = '';
        elements.splice(0, elements.length, { name: 'Shop.Customer_Get', file: null });
        boxes.review.checked = true; boxes.validate.checked = true;
        const t = text();
        has(t, '[oml] Small task');
        has(t, 'Use oml (the OutSystems 11 CLI) and its skill.');
        has(t, 'Module: C:\\T\\Orders.oml.');
        has(t, 'Read only (producers; read them with --in, never change them): C:\\T\\Shop.oml.');
        has(t, '- Orders.oml: copied from the module Orders open in Service Studio (oml pull). This is a copy; do not install over the open module without asking.');
        has(t, '- Shop.oml: downloaded from LifeTime: application Shop, as it runs in Development (oml lt download).');
        has(t, 'Then run oml review C:\\T\\Orders.oml C:\\T\\Orders.staged.oml --md C:\\T\\Orders.review.md and show me the Markdown.');
        has(t, 'Use these elements from other modules: Shop.Customer_Get.');
        boxes.preview.checked = true;
        has(text(), 'Preview the UI at 1366 and 1920 px (add dark or 390 px only when needed).');
        boxes.preview.checked = false;
        opts.mode = 'architect';
        const ta = text();
        has(ta, '[oml] Architect task');
        has(ta, 'Use oml (the OutSystems 11 CLI) as the architect: read the skill\'s architect.md and follow it');
        opts.mode = 'small';
        has(text(), '[oml] Small task');
        const run = JSON.parse(steps().replace(/^\/\/[^\n]*\n/, '')).steps.map(s => s.join(' '));
        const expect = ['inspect C:\\T\\Orders.oml --own', 'describe C:\\T\\Orders.oml Shop.Customer_Get --in C:\\T\\Shop.oml', 'apply C:\\T\\Orders.oml Orders.spec.json C:\\T\\Orders.staged.oml',
          'validate C:\\T\\Orders.staged.oml --warnings', 'review C:\\T\\Orders.oml C:\\T\\Orders.staged.oml --md C:\\T\\Orders.review.md'];
        if (run.join('|') !== expect.join('|')) fail('steps: ' + run.join(' | '));
        if (stepsButton.disabled || !steps().startsWith('// Write Orders.spec.json first')) fail('steps button or comment');
        boxes.review.checked = false;
        if (text().includes('oml review')) fail('review step not optional');
        workset.setRole('C:\\T\\Shop.oml', 'change');
        if (!text().includes('Modules to change: C:\\T\\Orders.oml, C:\\T\\Shop.oml.')) fail('two change modules');
        workset.setRole('C:\\T\\Orders.oml', 'read'); workset.setRole('C:\\T\\Shop.oml', 'read');
        if (!stepsButton.disabled || !stepsHint.textContent) fail('run steps must be off without a module to change');
        root.dataset.selftest = 'ok';
      } finally {
        for (const [k, v] of saved) { if (v === null) { try { localStorage.removeItem('oml-dash-' + k); } catch { /* nothing kept */ } } else store.set(k, v); }
        elements.splice(0, elements.length);
        boxes.review.checked = true;
        opts.mode = 'small';
        build();
      }
    }
  },
};
