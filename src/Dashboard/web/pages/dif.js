import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { api, need } from '../core/api.js';
import { task, cli, copy, pathField, toast, errorBox } from '../core/ui.js';
import { markdown, colorCode } from '../core/markdown.js';
import { reviewResult } from '../core/review-view.js';
import { flowView, mergeFlows } from '../core/dif-flow.js';
import { onLeave } from '../core/lifecycle.js';
import { hashParams } from '../core/state.js';
import { gallery, stash, placeMedia, settled } from '../core/dif-media.js';

// The Dif page (oml show-change <file.md>): an agent's change walkthrough. Plain Markdown plus three oml parts:
//   ```callstack    rows "+" new, "-" removed, " " unchanged; [[...]] links and "# note" at the end of a row
//   ```review       "before: Orders.oml" and "after: Orders.staged.oml": oml review of the two, from the model
//   ```flow         "module:" and "action:": the action's flow as a diagram (oml flow)
//   ```flow diff    "before:", "after:" and "action:": both flows in one diagram, new green, removed red, changed amber
//   [[Orders.oml#MainFlow/Orders/LoadData]]   opens oml describe of that element under the paragraph
//   ![Label](media/x.png), :::gallery side-by-side|grid|carousel [Title] ... :::   images and video (core/dif-media.js)
// Relative paths are read from the Markdown file's folder. Comments stay in this browser, per file, and copy as a
// numbered list for the agent.
const REF = /\[\[([^\]#]+\.oml)(?:#([^\]]+))?\]\]/gi;
const reviews = new Map(); // before|after -> review answer, so a reload of the file does not run the review again

const absolute = p => /^([a-z]:[\\/]|\\\\)/i.test(p);
const join = (folder, p) => absolute(p) ? p : folder.replace(/[\\/]+$/, '') + '\\' + p.replace(/\//g, '\\');

export default {
  id: 'dif', group: 'Agents', title: 'Dif', icon: 'dif',
  description: 'Read the change walkthrough an agent wrote: what changes and why, call stacks, the review of the staged module. Comment and copy your notes back to the agent.',
  async render(main) {
    const head = h('section', { class: 'card', 'aria-label': 'Walkthrough file' });
    const notes = h('section', { class: 'card', 'aria-label': 'Comments' });
    const doc = h('article', { class: 'card dif-doc', 'aria-label': 'Walkthrough' });
    add(main, head, notes, doc);
    let current = null;

    // No file yet: type the path of one (the page shows no file the person did not give).
    function opener(hint) {
      const field = pathField('Markdown file', store.get('dif-file', ''), 'C:\\Work\\specs\\orders-export.md', hint);
      const open = h('button', { class: 'button primary', type: 'button' }, 'Open');
      const go = async () => {
        if (!field.value()) { field.input.focus(); return; }
        const data = await task(open, doc, 'Reading…', () => api('dif', { path: field.value() }));
        if (data) { store.set('dif-file', data.file); show(data, false); }
      };
      open.addEventListener('click', go);
      field.input.addEventListener('keydown', e => { if (e.key === 'Enter') go(); });
      head.replaceChildren(field.el, open);
      notes.hidden = true;
      doc.replaceChildren(h('div', { class: 'doc' }, h('p', { class: 'muted' }, 'An agent writes the walkthrough as Markdown, for example specs/<name>.md, and runs oml show-change <file>. ',
        'Besides normal Markdown it can show call stacks, element links and the review of the staged module.')));
    }

    function show(data, started) {
      current = data;
      const reload = h('button', { class: 'button small', type: 'button' }, 'Reload');
      reload.addEventListener('click', async () => { const again = await refetch(true); if (again) render(again); });
      head.replaceChildren(h('div', { class: 'card-head' }, h('h2', { class: 'mono small', text: data.file }), h('div', { class: 'row' }, reload)),
        cli('oml show-change ' + (/\s/.test(data.file) ? '"' + data.file + '"' : data.file)));
      notes.hidden = false;
      render(data);
      // The file the agent keeps editing: show its newest version (checked every 2 seconds while the page is open).
      const timer = setInterval(async () => {
        const again = await refetch(false);
        if (again && again.modified !== current.modified) { render(again); toast('The walkthrough changed; showing the new version'); }
      }, 2000);
      onLeave(() => clearInterval(timer));
      async function refetch(loud) {
        try { return await (started ? api('dif') : api('dif', { path: data.file })); }
        catch (e) { if (loud) doc.prepend(errorBox(e)); return null; }
      }
    }

    function render(data) {
      current = data;
      const y = window.scrollY;
      doc.replaceChildren(...blocks(data.markdown, data.folder));
      comments();
      window.scrollTo(0, y);
    }

    // ---- the Markdown, split at the oml fences ----
    function blocks(text, folder) {
      const lines = String(text).replace(/\r/g, '').split('\n');
      const out = [];
      let plain = [];
      const found = []; // ![label](path) in plain text, set aside while markdown() runs (core/dif-media.js)
      const flush = () => { if (plain.join('').trim()) out.push(placeMedia(links(markdown(stash(plain.join('\n'), found)), folder), current.file, found)); plain = []; found.length = 0; };
      let code = false;
      for (let i = 0; i < lines.length; i++) {
        const open = /^:::\s*gallery\b(.*)$/.exec(lines[i]);
        if (open && !code) {
          const body = [];
          for (i++; i < lines.length && !/^:::\s*$/.test(lines[i]); i++) body.push(lines[i]);
          flush();
          out.push(gallery(current.file, open[1], body));
          continue;
        }
        if (/^```/.test(lines[i]) && !/^```\s*(callstack|review|flow)\b/.test(lines[i])) code = !code;
        const fence = /^```\s*(callstack|review|flow)\b(.*)$/.exec(lines[i]);
        if (!fence) { plain.push(lines[i]); continue; }
        const body = [];
        for (i++; i < lines.length && !/^```\s*$/.test(lines[i]); i++) body.push(lines[i]);
        flush();
        out.push(fence[1] === 'callstack' ? callstack(body, folder) : fence[1] === 'flow' ? flow(body, folder, /^\s*diff\b/.test(fence[2])) : review(body, folder));
      }
      flush();
      return out;
    }

    // [[Module.oml#Element/Path]] in text: a button that shows oml describe under its paragraph.
    function links(root, folder) {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      const found = [];
      for (let n = walker.nextNode(); n; n = walker.nextNode()) if (n.nodeValue.includes('[[') && !n.parentElement.closest('pre')) found.push(n);
      for (const n of found) {
        const parts = [];
        let last = 0;
        for (const m of n.nodeValue.matchAll(REF)) {
          parts.push(n.nodeValue.slice(last, m.index), ref(m[1], m[2], folder, n.parentElement.closest('p, li, td') || root));
          last = m.index + m[0].length;
        }
        if (!parts.length) continue;
        parts.push(n.nodeValue.slice(last));
        n.replaceWith(...parts.filter(p => p !== ''));
      }
      return root;
    }

    function ref(module, element, folder, anchor) {
      const path = join(folder, module.trim());
      const label = element ? element.trim() : module.trim();
      const b = h('button', { class: 'dif-ref', type: 'button', title: path + (element ? ' > ' + element : ''), 'aria-expanded': 'false' }, label);
      let panel = null;
      b.addEventListener('click', async () => {
        if (panel) { panel.remove(); panel = null; b.setAttribute('aria-expanded', 'false'); return; }
        panel = h('div', { class: 'dif-panel' });
        anchor.after(panel);
        b.setAttribute('aria-expanded', 'true');
        if (!element) { add(panel, h('p', { class: 'muted' }, 'Module ', h('code', { text: path })), cli('oml inspect "' + path + '" --own')); return; }
        await describe(panel, path, element.trim());
      });
      return b;
    }

    // oml describe of one element, into a panel.
    async function describe(panel, path, element) {
      const data = await task(null, panel, 'Reading the element…', async () => need(await api('describe', { path, name: element })));
      if (!data) return;
      const d = Array.isArray(data.output) ? data.output[0] : data.output || {};
      add(panel, h('div', { class: 'row small' }, h('strong', {}, d.element || element), d.kind ? h('span', { class: 'badge accent' }, d.kind) : null),
        d.text ? colorCode(d.text, 'describe') : colorCode(JSON.stringify(d, null, 2), 'json'), cli(data.cli));
    }

    // ```flow ("module:", "action:") and ```flow diff ("before:", "after:", "action:"): oml flow of the action, drawn.
    // In a diff an action that one side does not have is all new or all removed. A node opens describe under the diagram.
    function flow(body, folder, diff) {
      const v = Object.fromEntries(body.map(l => /^\s*(module|before|after|action|title)\s*:\s*(.+?)\s*$/i.exec(l)).filter(Boolean).map(m => [m[1].toLowerCase(), m[2]]));
      const box = h('div', { class: 'dif-flow' });
      const need2 = diff ? ['before', 'after', 'action'] : ['module', 'action'];
      if (need2.some(k => !v[k])) { add(box, h('div', { class: 'message error', role: 'alert' }, `A ${diff ? 'flow diff' : 'flow'} fence needs ${need2.map(k => '"' + k + ': ..."').join(', ')} lines.`)); return box; }
      const read = async file => {
        const data = await api('flow', { path: join(folder, file), name: v.action });
        if (data.code === 0) return data;
        if (diff && /no element|has no flow/i.test(JSON.stringify(data.output || ''))) return null; // the action is new or removed
        throw new Error((data.output && data.output.error) || data.stderr || 'oml flow failed');
      };
      task(null, box, `Reading the flow of ${v.action}…`, async () => diff ? [await read(v.before), await read(v.after)] : [null, await read(v.module)])
        .then(result => {
          if (!result) return;
          const [b, a] = result;
          if (diff && !a && !b) { add(box, h('div', { class: 'message error', role: 'alert' }, `Neither ${v.before} nor ${v.after} has a flow named ${v.action}.`)); return; }
          const graph = diff ? mergeFlows(b && b.output, a && a.output) : a.output;
          const panel = h('div', { class: 'dif-panel', hidden: true });
          let open = null;
          const onNode = async n => {
            const where = n.status === 'del' ? v.before : (diff ? v.after : v.module);
            const key = n.id + '|' + n.status;
            if (open === key) { panel.hidden = true; open = null; return; }
            open = key; panel.hidden = false;
            await describe(panel, join(folder, where), graph.element + '/' + n.id);
          };
          const caption = v.title || (diff ? `${v.action}: ${v.before} \u2192 ${v.after}` : `${v.action} in ${v.module}`);
          add(box, flowView(graph, { title: caption, onNode, diff }), panel, cli(diff ? `oml flow "${join(folder, v.after)}" ${graph.element}` : (a && a.cli)));
        });
      return box;
    }

    // ```callstack: one row per line; the first character marks it (+ new, - removed, space unchanged).
    function callstack(rows, folder) {
      const pre = h('pre', { class: 'dif-stack', 'aria-label': 'Call stack' });
      for (const row of rows) {
        const mark = row[0] === '+' || row[0] === '-' ? row[0] : ' ';
        let text = mark === ' ' && row[0] !== ' ' ? row : row.slice(1);
        let note = '';
        const hash = text.search(/\s#\s/);
        if (hash >= 0) { note = text.slice(hash + 3).trim(); text = text.slice(0, hash); }
        const line = h('span', { class: 'dif-row' + (mark === '+' ? ' add' : mark === '-' ? ' del' : '') },
          h('span', { class: 'dif-mark', 'aria-hidden': 'true' }, mark === ' ' ? ' ' : mark));
        line.setAttribute('aria-label', (mark === '+' ? 'new: ' : mark === '-' ? 'removed: ' : '') + text.trim());
        let last = 0;
        for (const m of text.matchAll(REF)) { add(line, text.slice(last, m.index), ref(m[1], m[2], folder, pre)); last = m.index + m[0].length; }
        add(line, text.slice(last).replace(/\s+$/, ''), note ? h('span', { class: 'dif-note' }, '  # ' + note) : null, '\n');
        add(pre, line);
      }
      return pre;
    }

    // ```review: "before:" and "after:" module files; oml review of the two, as on the Review page.
    function review(body, folder) {
      const values = Object.fromEntries(body.map(l => /^\s*(before|after|title)\s*:\s*(.+?)\s*$/i.exec(l)).filter(Boolean).map(m => [m[1].toLowerCase(), m[2]]));
      const box = h('div', { class: 'dif-review' });
      if (!values.before || !values.after) { add(box, h('div', { class: 'message error', role: 'alert' }, 'A review fence needs a "before: <module.oml>" line and an "after: <staged.oml>" line.')); return box; }
      const before = join(folder, values.before), after = join(folder, values.after);
      const key = before.toLowerCase() + '|' + after.toLowerCase();
      const draw = data => box.replaceChildren(reviewResult(data, { compact: true, heading: 'h3', title: values.title || ('Review: ' + values.after), label: 'Review of ' + values.after }));
      if (reviews.has(key)) draw(reviews.get(key));
      else task(null, box, 'Comparing ' + values.before + ' with ' + values.after + '…', async () => need(await api('review', { before, after })))
        .then(data => { if (data) { reviews.set(key, data); draw(data); } });
      return box;
    }

    // ---- comments ----
    const saved = () => (store.get('dif-comments', {})[current.file] || []);
    function keep(list) { const all = store.get('dif-comments', {}); all[current.file] = list; store.set('dif-comments', all); comments(); }

    function comments() {
      const list = saved();
      const copyAll = h('button', { class: 'button small', type: 'button', disabled: !list.length }, 'Copy comments');
      copyAll.addEventListener('click', () => copy(asMarkdown(list), 'Comments'));
      const clear = h('button', { class: 'button small', type: 'button', disabled: !list.length }, 'Clear');
      clear.addEventListener('click', () => { if (confirm('Delete every comment on this file?')) keep([]); });
      notes.replaceChildren(h('div', { class: 'card-head' }, h('h2', {}, `Comments (${list.length})`), h('div', { class: 'row' }, copyAll, clear)),
        list.length ? h('ol', { class: 'dif-comments' }, list.map((c, n) => {
          const del = h('button', { class: 'button small', type: 'button', 'aria-label': 'Delete comment ' + (n + 1) }, 'Delete');
          del.addEventListener('click', () => keep(saved().filter(x => x.id !== c.id)));
          return h('li', {}, h('div', { class: 'tiny muted' }, c.section || 'Top'), h('blockquote', { class: 'dif-quote', text: c.quote }), h('p', { text: c.text }), del);
        })) : h('p', { class: 'hint' }, 'Select text in the walkthrough and press Comment. Comments stay in this browser; Copy comments gives the agent a numbered list.'));
    }

    function asMarkdown(list) {
      return [`Comments on ${current.file}:`, ...list.map((c, n) => `${n + 1}. ${c.section ? 'In "' + c.section + '", on' : 'On'} "${c.quote.replace(/\s+/g, ' ').slice(0, 200)}": ${c.text}`)].join('\n');
    }

    // A selection inside the walkthrough shows a Comment button next to it.
    const button = h('button', { class: 'button small primary dif-comment-button', type: 'button', hidden: true }, 'Comment');
    document.body.append(button);
    onLeave(() => { button.remove(); document.removeEventListener('selectionchange', place); });
    let pending = null;
    function place() {
      const sel = document.getSelection();
      const text = sel && !sel.isCollapsed ? sel.toString().trim() : '';
      if (!text || !doc.contains(sel.anchorNode) || doc.querySelector('.dif-form')) { button.hidden = true; return; }
      const box = sel.getRangeAt(0).getBoundingClientRect();
      pending = { quote: text.slice(0, 600), section: sectionOf(sel.anchorNode), after: (sel.anchorNode.parentElement || doc).closest('p, li, pre, table, blockquote, h2, h3, h4, .dif-review') || doc.lastElementChild };
      button.style.top = Math.round(window.scrollY + box.bottom + 6) + 'px';
      button.style.left = Math.round(window.scrollX + Math.max(8, box.left)) + 'px';
      button.hidden = false;
    }
    document.addEventListener('selectionchange', place);
    button.addEventListener('mousedown', e => e.preventDefault());
    button.addEventListener('click', () => { button.hidden = true; if (pending) form(pending); });

    function sectionOf(node) {
      let el = node.nodeType === 1 ? node : node.parentElement;
      while (el && el !== doc && !/^H[1-4]$/.test(el.tagName)) {
        let prev = el.previousElementSibling;
        while (prev && !/^H[1-4]$/.test(prev.tagName) && !prev.querySelector('h1, h2, h3, h4')) prev = prev.previousElementSibling;
        if (prev) return (/^H[1-4]$/.test(prev.tagName) ? prev : [...prev.querySelectorAll('h1, h2, h3, h4')].pop()).textContent.trim();
        el = el.parentElement;
      }
      return el && el !== doc ? el.textContent.trim() : '';
    }

    function form(p) {
      const area = h('textarea', { rows: 3, 'aria-label': 'Comment', placeholder: 'What should the agent change or explain?' });
      const save = h('button', { class: 'button small primary', type: 'button' }, 'Save');
      const cancel = h('button', { class: 'button small', type: 'button' }, 'Cancel');
      const box = h('div', { class: 'dif-form' }, h('blockquote', { class: 'dif-quote', text: p.quote }), area, h('div', { class: 'row' }, save, cancel, h('span', { class: 'tiny muted' }, 'Ctrl+Enter saves, Esc cancels')));
      const done = () => box.remove();
      const commit = () => {
        if (!area.value.trim()) { area.focus(); return; }
        keep([...saved(), { id: Date.now().toString(36) + Math.random().toString(36).slice(2, 6), quote: p.quote, section: p.section, text: area.value.trim() }]);
        done(); toast('Comment saved');
      };
      save.addEventListener('click', commit);
      cancel.addEventListener('click', done);
      area.addEventListener('keydown', e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); commit(); } else if (e.key === 'Escape') done(); });
      p.after.after(box);
      area.focus();
    }

    // Last, so every helper above exists when the first file is drawn.
    // #/dif?file=C:\Work\specs\x.md opens that file: a link a person can keep (as #/explorer?module=).
    const linked = hashParams().get('file');
    const first = await task(null, doc, 'Loading…', () => linked ? api('dif', { path: linked }) : api('dif'));
    if (!first) { if (linked) opener(); return; }
    if (first.file) show(first, !linked); else opener(first.hint);
    if (first.file && hashParams().get('selftest') === '1') await selfTest();

    // #/dif?selftest=1 (tests/dashboard-check.ps1): open an element link, select text, comment, copy, then remove the
    // comment again. The result is in <html data-selftest>.
    async function selfTest() {
      const root = document.documentElement;
      const wait = async (test, ms = 15000) => { for (const end = Date.now() + ms; Date.now() < end; await new Promise(r => setTimeout(r, 100))) if (test()) return true; return false; };
      try {
        const link = doc.querySelector('.dif-ref');
        if (!link) throw new Error('no element link');
        link.click();
        if (!await wait(() => doc.querySelector('.dif-panel .code-view, .dif-panel .message'))) throw new Error('element link opened nothing');
        if (doc.querySelector('.dif-panel .message.error')) throw new Error('element link: ' + doc.querySelector('.dif-panel .message').textContent);
        // A flow diagram: a node opens describe under it.
        if (doc.querySelector('.dif-flow')) {
          if (!await wait(() => doc.querySelector('.dif-flow .flow-node[role="button"]') || doc.querySelector('.dif-flow .message.error'))) throw new Error('flow diagram not drawn');
          if (doc.querySelector('.dif-flow .message.error')) throw new Error('flow: ' + doc.querySelector('.dif-flow .message.error').textContent);
          doc.querySelector('.dif-flow .flow-node[role="button"]').dispatchEvent(new MouseEvent('click', { bubbles: true }));
          if (!await wait(() => doc.querySelector('.dif-flow .dif-panel .code-view, .dif-flow .dif-panel .message'))) throw new Error('flow node opened nothing');
          if (doc.querySelector('.dif-flow .dif-panel .message.error')) throw new Error('flow node: ' + doc.querySelector('.dif-flow .dif-panel .message').textContent);
        }
        // Images: every figure shows its file or says why not; a gallery image must really have loaded.
        if (doc.querySelector('.dif-figure')) {
          if (!await settled(doc)) throw new Error('media did not finish loading');
          const loaded = [...doc.querySelectorAll('.dif-figure img')].filter(i => i.complete && i.naturalWidth > 0).length;
          if (doc.querySelector('.dif-gallery') && ![...doc.querySelectorAll('.dif-gallery img')].some(i => i.complete && i.naturalWidth > 0)) throw new Error('no gallery image loaded');
          root.dataset.selftestMedia = `loaded=${loaded} missing=${doc.querySelectorAll('.dif-figure.missing').length}`;
        }
        const para = [...doc.querySelectorAll('.doc p')].find(p => p.textContent.trim().length > 10);
        const range = document.createRange();
        range.selectNodeContents(para);
        document.getSelection().removeAllRanges();
        document.getSelection().addRange(range);
        place();
        if (button.hidden) throw new Error('no Comment button for a selection');
        button.click();
        const area = doc.querySelector('.dif-form textarea');
        area.value = 'Self-test comment';
        area.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', ctrlKey: true, bubbles: true }));
        const list = saved();
        if (list.length !== 1 || !asMarkdown(list).includes('Self-test comment') || !/^1\. In "/m.test(asMarkdown(list))) throw new Error('comment not saved: ' + asMarkdown(list));
        if (!/Comments \(1\)/.test(notes.textContent)) throw new Error('comment list not shown');
        keep([]);
        document.getSelection().removeAllRanges();
        window.scrollTo(0, 0); // the comment box scrolled the page; the test's screenshot shows the top
        root.dataset.selftest = 'ok';
      } catch (e) { root.dataset.selftest = 'fail: ' + e.message; }
    }
  },
};
