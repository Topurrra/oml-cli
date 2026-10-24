import { h, add, icon } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need, inspect } from '../core/api.js';
import { task, cli, segmented, blobUrl, download } from '../core/ui.js';
import { viewer } from '../core/viewer.js';
import { onLeave } from '../core/lifecycle.js';
import { state } from '../core/state.js';
import { modulePicker } from '../core/workset.js';

export default {
  id: 'previews', group: 'Module', title: 'Preview gallery', icon: 'previews',
  description: 'See every screen and block of a module, in light and dark, at desktop and phone width, with the automatic UI checks.',
  async render(main) {
    const body = h('div');
    const picker = modulePicker(async (path, button, status) => {
      const data = await task(button, status, 'Reading the module…', () => inspect(path));
      if (data) show(path, data);
    });
    add(main, picker.el, body);
    let theme = store.get('preview-theme', 'light'), width = store.get('preview-width', 1200);

    // Inputs (--arg), --height and interaction steps (--click, --type, --wait, --eval) of one card.
    function previewOptions(el) {
      const id = 'pv-' + el.name;
      const inputs = el.inputs.map(name => [name, h('input', { type: 'text', id: `${id}-arg-${name}`, class: 'code', placeholder: 'Default value', spellcheck: 'false' })]);
      const height = h('input', { type: 'number', id: id + '-height', min: '100', max: '10000', placeholder: '900' });
      const steps = h('ol', { class: 'steps' });
      const addStep = () => {
        const n = steps.children.length + 1;
        const kind = h('select', { 'aria-label': `Step ${n} kind` }, [['click', 'Click'], ['type', 'Type'], ['wait', 'Wait (ms)'], ['eval', 'Run JavaScript']].map(([v, t]) => h('option', { value: v }, t)));
        const value = h('input', { type: 'text', class: 'code', 'aria-label': `Step ${n} value`, placeholder: '#SaveButton', spellcheck: 'false' });
        const hints = { click: '#SaveButton', type: '#Name=Ann', wait: '300', eval: "document.title" };
        kind.addEventListener('change', () => { value.placeholder = hints[kind.value]; });
        const li = h('li', { class: 'row' }, kind, h('div', { class: 'grow' }, value),
          h('button', { class: 'button small', type: 'button', 'aria-label': `Remove step ${n}`, onclick: () => li.remove() }, 'Remove'));
        li.read = () => value.value.trim() ? { kind: kind.value, value: kind.value === 'wait' ? Number(value.value) : value.value.trim() } : null;
        add(steps, li);
        value.focus();
      };
      const el2 = h('details', { class: 'pv-options' }, h('summary', {}, 'Inputs and interaction steps'),
        inputs.length ? h('div', { class: 'grid-2' }, inputs.map(([name, input]) => h('div', { class: 'field' }, h('label', { for: input.id }, name), input))) : h('p', { class: 'hint' }, 'No inputs.'),
        h('p', { class: 'hint' }, 'Use OutSystems literals: 3, True, "Ann". Other text is used as plain text.'),
        h('div', { class: 'field' }, h('label', { for: height.id }, 'Screenshot height (px)'), height),
        h('p', { class: 'small' }, h('strong', {}, 'Steps'), ' run in order after the page loaded, before the screenshot. They need Edge or Chrome.'), steps,
        h('button', { class: 'button small', type: 'button', 'aria-label': 'Add a step for ' + el.name, onclick: addStep }, 'Add a step'));
      return {
        el: el2,
        body: () => ({
          args: inputs.filter(([, i]) => i.value.trim()).map(([name, i]) => ({ name, value: i.value.trim() })),
          height: height.value ? Number(height.value) : undefined,
          steps: [...steps.children].map(li => li.read()).filter(Boolean),
        }),
      };
    }
    // What the page did in the browser: steps, console output, blocked requests.
    function pageReport(report) {
      const list = (title, items, row) => items && items.length ? h('div', {}, h('p', { class: 'small' }, h('strong', {}, title)), h('ul', { class: 'checklist' }, items.map(row))) : null;
      return h('div', {},
        list('Steps', report.steps, x => h('li', {}, h('span', { class: 'badge ' + (x.ok ? 'ok' : 'fail') }, x.ok ? 'OK' : 'Failed'), h('span', {}, h('code', { text: x.step }),
          x.result !== undefined ? ' → ' + x.result : '', x.error ? h('span', { class: 'detail' }, ' — ' + x.error) : null))),
        list('Console', report.console, x => h('li', {}, h('span', { class: 'badge' + (x.level === 'error' ? ' fail' : x.level === 'warn' ? ' warn' : '') }, x.level), h('code', { text: x.text }))),
        list('Blocked (no network in the preview)', report.blocked, x => h('li', {}, h('code', { text: typeof x === 'string' ? x : JSON.stringify(x) }))),
        list('JavaScript errors', report.scriptErrors, x => h('li', {}, h('span', { class: 'badge fail' }, x.node), h('span', {}, x.message))),
        report.scriptsRan === false ? h('p', { class: 'hint' }, 'The page did not finish its scripts, so these lists are unknown.') : null);
    }

    function show(path, data) {
      const out = data.output || {};
      const elements = [...(out.screens || []).map(s => ({ name: s.name, kind: 'Screen', inputs: s.inputs || [] })), ...(out.blocks || []).map(b => ({ name: b.name, kind: 'Block', inputs: b.inputs || [] }))];
      if (!elements.length) { body.replaceChildren(h('div', { class: 'empty' }, 'This module has no screens or blocks.')); return; }
      const cards = [];
      const renderAll = h('button', { class: 'button primary', type: 'button' }, 'Render all');
      const gallery = h('div', { class: 'gallery' });
      const browserNote = state.info && !state.info.browser ? h('p', { class: 'hint' }, 'Screenshots need Edge or Chrome. The live previews work without them.') : null;
      for (const el of elements) {
        const out = h('div');
        const render = h('button', { class: 'button small', type: 'button', 'aria-label': 'Render ' + el.name }, 'Render');
        const shot = h('button', { class: 'button small', type: 'button', disabled: state.info && !state.info.browser, 'aria-label': 'Screenshot of ' + el.name }, 'Screenshot');
        const options = previewOptions(el);
        const go = async screenshot => {
          const w = width, t = theme;
          const r = await task(screenshot ? shot : render, out, screenshot ? 'Taking a screenshot… about 5 seconds.' : 'Rendering…', async () =>
            need(await api('preview', { path, element: el.name, theme: t, width: w, screenshot, ...options.body() })));
          if (!r) return;
          const report = r.output || {};
          const frameHeight = w <= 500 ? 760 : 640;
          let view;
          if (r.png) {
            const url = blobUrl(r.png, 'image/png');
            onLeave(() => URL.revokeObjectURL(url));
            view = viewer({ src: url, title: `Screenshot of ${el.name}, ${t} theme, ${w} px wide` });
          } else view = viewer({ view: r.view, title: `Preview of ${el.name}, ${t} theme, ${w} px wide`, width: w, height: frameHeight, fullHeight: true });
          const failed = (report.checklist || []).filter(c => !c.ok);
          add(out, view,
            r.png ? h('button', { class: 'button small', type: 'button', 'aria-label': `Download the PNG of ${el.name}`, onclick: () => download(`${el.name}-${t}-${w}.png`, r.png, 'image/png', true) }, icon('download'), 'Download PNG') : null,
            h('div', { class: 'row small' }, failed.length ? h('span', { class: 'badge warn' }, `${failed.length} of ${report.checklist.length} checks need work`) : h('span', { class: 'badge ok' }, 'All UI checks pass'),
              (report.scriptErrors || []).length ? h('span', { class: 'badge fail' }, `${report.scriptErrors.length} JavaScript error(s)`) : null,
              report.horizontalOverflow ? h('span', { class: 'badge fail' }, 'Wider than the frame') : null,
              (report.unsupported || []).length ? h('span', { class: 'badge' }, 'Not drawn: ' + report.unsupported.map(u => `${u.kind} ×${u.count}`).join(', ')) : null),
            h('ul', { class: 'checklist', 'aria-label': 'UI checks' }, (report.checklist || []).map(c => h('li', {},
              h('span', { class: 'badge ' + (c.ok ? 'ok' : 'warn') }, c.ok ? 'OK' : 'Check'), h('span', {}, c.name, c.detail ? h('span', { class: 'detail' }, ' — ' + c.detail) : null)))),
            pageReport(report),
            cli(r.cli));
        };
        render.addEventListener('click', () => go(false));
        shot.addEventListener('click', () => go(true));
        cards.push(() => go(false));
        add(gallery, h('section', { class: 'card', 'aria-label': el.name }, h('div', { class: 'card-head' },
          h('div', {}, h('h3', {}, el.name), h('span', { class: 'badge' }, el.kind)), h('div', { class: 'row' }, render, shot)), options.el, out));
      }
      // The first ones render at once and again when the theme or width changes (a preview takes well under a
      // second); the rest wait for a click. A newer round stops an older one.
      let round = 0;
      const auto = async () => { const mine = ++round; for (const go of cards.slice(0, 12)) { if (!gallery.isConnected || mine !== round) return; await go(); } };
      const progress = h('span', { class: 'small muted', 'aria-live': 'polite' });
      const stop = h('button', { class: 'button', type: 'button', hidden: true }, 'Stop');
      let stopping = false;
      stop.addEventListener('click', () => { stopping = true; stop.disabled = true; progress.textContent += ' Stopping after this one…'; });
      renderAll.addEventListener('click', async () => {
        const mine = ++round;
        renderAll.disabled = true; stop.hidden = false; stop.disabled = false; stopping = false;
        let done = 0;
        try {
          for (const go of cards) {
            if (stopping || mine !== round || !gallery.isConnected) break;
            progress.textContent = `Rendering ${done + 1} of ${cards.length}.`;
            await go();
            done++;
          }
        } finally {
          renderAll.disabled = false; stop.hidden = true;
          progress.textContent = done === cards.length ? `Rendered all ${cards.length}.` : `Rendered ${done} of ${cards.length}. Stopped.`;
        }
      });
      body.replaceChildren(
        h('div', { class: 'card' }, h('div', { class: 'row' },
          segmented('Theme', [['light', 'Light'], ['dark', 'Dark']], theme, v => { theme = v; store.set('preview-theme', v); auto(); }),
          segmented('Width', [[1200, 'Desktop 1200 px'], [390, 'Phone 390 px']], width, v => { width = v; store.set('preview-width', v); auto(); }),
          h('span', { class: 'grow' }), progress, stop, renderAll),
          h('p', { class: 'hint' }, 'The preview does not load OutSystems UI, so its classes look plain. Judge your own CSS, texts and layout.'), browserNote),
        gallery);
      auto();
    }
    if (state.module) picker.go();
  },
};
