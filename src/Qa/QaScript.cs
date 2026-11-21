namespace OmlCli.Qa;

// The helper that runs inside the page. It knows how an OutSystems page names things: a widget's Name is the last part
// of its DOM id ("b2-b3-ThemeButton" is the widget ThemeButton inside two block instances), "$b1" ids are block
// wrappers, buttons and links carry data-button / data-link, feedback messages are .feedback-message-<type>, and
// invalid inputs show a .validation-message. Every call re-installs it when a navigation replaced the page.
static class QaScript
{
    public const string Helper = """
        (() => {
          const vis = e => {
            if (!e || !e.isConnected) return false;
            const r = e.getBoundingClientRect();
            if (r.width === 0 && r.height === 0) return false;
            const s = getComputedStyle(e);
            return s.visibility !== 'hidden' && s.display !== 'none' && +s.opacity !== 0;
          };
          const txt = e => (e.innerText || e.value || e.getAttribute('aria-label') || e.getAttribute('title') || '').replace(/\s+/g, ' ').trim();
          const CLICKABLE = 'button,a,[data-button],[data-link],input[type=submit],input[type=button],[role=button],[role=link],[role=tab],[role=menuitem],[role=switch],[role=checkbox],[onclick],summary';
          const FIELDS = 'input:not([type=hidden]),textarea,select,[contenteditable=true]';
          const name = id => { const n = id && id.split('-').pop(); return !n || n.startsWith('$') ? null : n; };   // b2-$b1 is a block wrapper
          const byName = n => [...document.querySelectorAll('[id]')].filter(e => e.id === n || e.id.endsWith('-' + n));
          const labelOf = e => {
            if (e.labels && e.labels.length) return txt(e.labels[0]);
            return e.getAttribute('aria-label') || e.getAttribute('placeholder') || '';
          };
          const byText = t => {
            const l = t.toLowerCase();
            const all = [...document.querySelectorAll(CLICKABLE + ',label')].filter(vis);
            let m = all.filter(e => txt(e).toLowerCase() === l);
            if (!m.length) m = all.filter(e => txt(e).toLowerCase().includes(l));
            if (!m.length) m = [...document.querySelectorAll(FIELDS)].filter(e => vis(e) && labelOf(e).toLowerCase() === l);
            return m.filter(e => !m.some(o => o !== e && e.contains(o)));
          };
          const desc = e => ({ tag: e.tagName.toLowerCase(), id: e.id || undefined, text: txt(e).slice(0, 60) || undefined });
          const field = e => {
            if (!e) return e;
            if (e.tagName === 'LABEL') return e.control || e.querySelector(FIELDS);
            return e.matches(FIELDS) ? e : e.querySelector(FIELDS);
          };
          const target = e => {
            if (e.id && name(e.id)) {
              const n = name(e.id), same = byName(n).filter(vis);
              return same.length <= 1 ? n : n + '#' + same.indexOf(e);
            }
            const t = txt(e);
            return t ? 'text:' + t.slice(0, 40) : null;
          };
          const ROWS = 3;
          // Where an element is in the module: the screen (the last part of the URL path), the web blocks around it (Reactive
          // puts data-block="Module.Block" on each block instance) and the nearest widget Name on the way up.
          const pathOf = e => {
            const blocks = [];
            let widget = null;
            for (let n = e; n && n.nodeType === 1; n = n.parentElement) {
              if (!widget && n.id && name(n.id)) widget = name(n.id);
              const b = n.getAttribute('data-block');
              if (b) blocks.unshift(b);
            }
            const screen = decodeURIComponent(location.pathname.replace(/\/$/, '').split('/').pop() || '');
            return { screen, blocks, widget, path: [screen, ...blocks, widget].filter(Boolean).join(' > ') };
          };
          const PROPS = ['display', 'position', 'visibility', 'opacity', 'width', 'height', 'margin', 'padding', 'border', 'border-radius', 'box-sizing',
            'color', 'background-color', 'background-image', 'font-family', 'font-size', 'font-weight', 'line-height', 'text-align', 'white-space', 'overflow',
            'flex-direction', 'justify-content', 'align-items', 'gap', 'grid-template-columns', 'z-index', 'box-shadow', 'cursor', 'transform', 'outline'];
          const hiddenBy = e => {
            for (let n = e; n && n.nodeType === 1; n = n.parentElement) {
              const s = getComputedStyle(n), d = { tag: n.tagName.toLowerCase(), id: n.id || undefined, cls: (n.className && n.className.baseVal === undefined ? n.className : '').split(' ').filter(Boolean).slice(0, 3).join('.') || undefined };
              if (s.display === 'none') return { why: 'display: none', on: d };
              if (s.visibility === 'hidden') return { why: 'visibility: hidden', on: d };
              if (+s.opacity === 0) return { why: 'opacity: 0', on: d };
            }
            const r = e.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) return { why: `size ${Math.round(r.width)} x ${Math.round(r.height)} px` };
            if (r.right < 0 || r.bottom < 0 || r.left > document.documentElement.scrollWidth) return { why: 'outside the page' };
            return null;
          };
          const short = e => (e.tagName.toLowerCase() + (e.id ? '#' + (name(e.id) || e.id) : '') + [...e.classList].slice(0, 2).map(c => '.' + c).join(''));
          return {
            pathOf(e) { return pathOf(e || window.__omlqaLast); },
            // What the last located element is and why it looks as it does: its place in the module, box, the computed values that
            // shape it, the layout of its parent, and what hides or covers it.
            lookLast() {
              const e = window.__omlqaLast;
              if (!e) return null;
              const s = getComputedStyle(e), r = e.getBoundingClientRect(), p = e.parentElement, ps = p ? getComputedStyle(p) : null;
              const computed = {};
              for (const k of PROPS) { const v = s.getPropertyValue(k); if (v && v !== 'none' && v !== 'normal' && v !== 'auto' && v !== '0px' && v !== 'rgba(0, 0, 0, 0)' && v !== 'static' && v !== 'visible') computed[k] = v; }
              const px = k => Math.round(parseFloat(s.getPropertyValue(k)) || 0);
              return {
                ...pathOf(e), tag: e.tagName.toLowerCase(), id: e.id || undefined, classes: [...e.classList], text: txt(e).slice(0, 80) || undefined,
                box: { x: Math.round(r.left + scrollX), y: Math.round(r.top + scrollY), w: Math.round(r.width), h: Math.round(r.height),
                  margin: [px('margin-top'), px('margin-right'), px('margin-bottom'), px('margin-left')].join(' '),
                  padding: [px('padding-top'), px('padding-right'), px('padding-bottom'), px('padding-left')].join(' '),
                  border: [px('border-top-width'), px('border-right-width'), px('border-bottom-width'), px('border-left-width')].join(' ') },
                computed,
                parent: p ? { el: short(p), display: ps.display, flexDirection: ps.display.includes('flex') ? ps.flexDirection : undefined, gridTemplateColumns: ps.display.includes('grid') ? ps.gridTemplateColumns : undefined,
                  gap: ps.gap !== 'normal' ? ps.gap : undefined, width: Math.round(p.getBoundingClientRect().width) } : undefined,
                children: e.children.length || undefined,
                hidden: hiddenBy(e) || undefined,
                covered: (() => { const h = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2); return h && !(e === h || e.contains(h) || h.contains(e)) ? short(h) : undefined; })(),
                clip: { x: r.left + scrollX, y: r.top + scrollY, width: Math.max(1, r.width), height: Math.max(1, r.height) },
              };
            },
            // The elements wider than the window, deepest first (their parents are wide because of them), with their place in the module.
            overflowing(width) {
              const w = width || document.documentElement.clientWidth;
              const wide = [...document.body.querySelectorAll('*')].filter(e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.right > w + 1 && vis(e); });
              const leaves = wide.filter(e => !wide.some(o => o !== e && e.contains(o)));
              return leaves.slice(0, 10).map(e => { const r = e.getBoundingClientRect(); return { el: short(e), path: pathOf(e).path, right: Math.round(r.right), width: Math.round(r.width) }; });
            },
            // A record of the screen to compare later (oml browser diff): every visible named widget with its place, box, text and the
            // computed values people notice.
            snapshot() {
              const seen = new Map();
              for (const e of document.querySelectorAll('[id]')) {
                const n = name(e.id);
                if (!n || !vis(e) || /^(reactContainer|transitionContainer)$/.test(n) || e.tagName === 'SCRIPT' || e.tagName === 'LINK') continue;
                const p = pathOf(e).path, key = seen.has(p) ? p + '#' + [...seen.keys()].filter(k => k === p || k.startsWith(p + '#')).length : p;
                const r = e.getBoundingClientRect(), s = getComputedStyle(e);
                seen.set(key, { box: [Math.round(r.left + scrollX), Math.round(r.top + scrollY), Math.round(r.width), Math.round(r.height)], text: txt(e).slice(0, 80),
                  style: { color: s.color, background: s.backgroundColor, font: s.fontSize + ' ' + s.fontWeight, display: s.display } });
              }
              return { url: location.href, viewport: { w: innerWidth, h: innerHeight }, body: document.body.className, widgets: Object.fromEntries(seen) };
            },
            locate(t, nth, wantField) {
              let how, m;
              const hash = t.match(/^(.*)#(\d+)$/);
              if (hash && !t.startsWith('css:')) { t = hash[1]; nth = nth ?? +hash[2]; }
              if (t.startsWith('css:')) { how = 'css'; m = [...document.querySelectorAll(t.slice(4))]; }
              else if (t.startsWith('text:')) { how = 'text'; m = byText(t.slice(5)); }
              else { how = 'widget'; m = byName(t); if (!m.length) { how = 'text'; m = byText(t); } }
              if (wantField) m = m.map(field).filter(Boolean);
              const shown = m.filter(vis), pool = shown.length ? shown : m;
              if (!pool.length) return { found: 0, how };
              if (pool.length > 1 && nth == null) return { found: pool.length, how, ambiguous: pool.slice(0, 5).map(desc) };
              const e = pool[nth ?? 0];
              if (!e) return { found: pool.length, how, error: `#${nth} is out of range: ${pool.length} match` };
              e.scrollIntoView({ block: 'center', inline: 'center' });
              window.__omlqaLast = e;
              const r = e.getBoundingClientRect(), x = r.left + r.width / 2, y = r.top + r.height / 2;
              const hit = document.elementFromPoint(x, y);
              const covered = hit && !(e === hit || e.contains(hit) || hit.contains(e) || (e.labels && [...e.labels].some(l => l.contains(hit)))) ? desc(hit) : undefined;
              const disabled = !!(e.disabled || e.getAttribute('aria-disabled') === 'true' || e.closest('[disabled]'));
              return { found: pool.length, how, x, y, visible: vis(e), disabled, covered, el: desc(e), kind: e.tagName === 'SELECT' ? 'select' : e.type || undefined };
            },
            focusLast() {
              const e = window.__omlqaLast;
              if (!e) return false;
              e.focus();
              if (e.select) e.select();
              return true;
            },
            valueOfLast() { const e = window.__omlqaLast; return e ? (e.isContentEditable ? e.innerText : e.value) : null; },
            selectLast(option) {
              const e = window.__omlqaLast;
              if (!e || e.tagName !== 'SELECT') return { error: 'not a dropdown (select)' };
              const o = [...e.options].find(o => o.text.trim() === option || o.value === option)
                || [...e.options].find(o => o.text.trim().toLowerCase().includes(option.toLowerCase()));
              if (!o) return { error: 'no option ' + JSON.stringify(option), options: [...e.options].map(o => o.text.trim()).slice(0, 20) };
              e.value = o.value;
              e.dispatchEvent(new Event('input', { bubbles: true }));
              e.dispatchEvent(new Event('change', { bubbles: true }));
              return { selected: o.text.trim() };
            },
            feedback() {
              return [...document.querySelectorAll('.feedback-message')].filter(vis).map(e => ({
                type: ((e.className.match(/feedback-message-(\w+)/) || [])[1]) || 'info',
                text: txt(e.querySelector('.feedback-message-text') || e),
              }));
            },
            invalid() {
              return [...document.querySelectorAll('.validation-message')].filter(vis).map(e => {
                const f = e.previousElementSibling && field(e.previousElementSibling);
                return { target: f ? target(f) : undefined, text: txt(e) };
              });
            },
            text() { return document.body ? document.body.innerText : ''; },
            table(t, nth) {
              const r = this.locate(t, nth);
              if (!r.found || r.ambiguous || r.error) return r;
              const e = window.__omlqaLast, tbl = e.tagName === 'TABLE' ? e : e.querySelector('table');
              if (!tbl) return { error: 'not a table' };
              const head = [...tbl.querySelectorAll('thead th, thead td')].map(txt);
              const rows = [...tbl.querySelectorAll('tbody tr')].map(tr => [...tr.cells].map(txt));
              return { columns: head, rows };
            },
            recordAt(el) {
              const e = el && el.closest && el.closest(CLICKABLE + ',' + FIELDS);
              if (!e || !target(e)) return null;
              return { target: target(e), text: txt(e).slice(0, 50) || undefined, tag: e.tagName === 'A' ? 'a' : e.matches(FIELDS) ? 'field' : 'button', field: e.matches(FIELDS), inputs: this.summary().inputs };
            },
            boxes() {
              const items = [...document.querySelectorAll(CLICKABLE + ',' + FIELDS)].filter(vis)
                .filter((e, _, all) => !all.some(o => o !== e && o.contains(e) && o.matches('a,button')))
                .map(e => { const r = e.getBoundingClientRect(); return { target: target(e), tag: e.tagName === 'A' ? 'a' : e.matches(FIELDS) ? 'field' : 'button', x: r.left, y: r.top, w: r.width, h: r.height }; })
                .filter(i => i.target);
              return { viewport: { w: innerWidth, h: innerHeight }, scrollWidth: document.documentElement.scrollWidth, items };
            },
            summary() {
              const actions = [...document.querySelectorAll(CLICKABLE)].filter(vis)
                .filter((e, _, all) => !all.some(o => o !== e && o.contains(e) && o.matches('a,button')))
                .map(e => ({ target: target(e), text: txt(e).slice(0, 50) || undefined, href: e.tagName === 'A' && e.getAttribute('href') && !e.getAttribute('href').startsWith('#') ? e.href : undefined, disabled: e.disabled || undefined }))
                .filter(a => a.target);
              const inputs = [...document.querySelectorAll(FIELDS)].filter(vis).map(e => ({
                target: target(e), type: e.tagName === 'SELECT' ? 'select' : e.tagName === 'TEXTAREA' ? 'textarea' : e.type || 'text',
                label: labelOf(e) || undefined, value: e.type === 'password' ? (e.value ? '***' : '') : (e.tagName === 'SELECT' ? (e.selectedOptions[0] || {}).text : e.value) || undefined,
                required: e.required || undefined, disabled: e.disabled || undefined,
              }));
              const tables = [...document.querySelectorAll('table')].filter(vis).map(t => {
                const rows = [...t.querySelectorAll('tbody tr')];
                return { target: target(t), columns: [...t.querySelectorAll('thead th, thead td')].map(txt), rows: rows.length,
                  sample: rows.slice(0, ROWS).map(tr => [...tr.cells].map(c => txt(c).slice(0, 40))) };
              });
              const used = new Set([...actions, ...inputs, ...tables].map(x => x.target));
              const widgets = [...new Set([...document.querySelectorAll('[id]')].filter(e => vis(e) && e.tagName !== 'LINK' && e.tagName !== 'SCRIPT')
                .map(e => name(e.id)).filter(n => n && !used.has(n) && !/^(reactContainer|transitionContainer)$/.test(n)))].slice(0, 60);
              const headings = [...document.querySelectorAll('h1,h2,h3,.heading1,.heading2,.heading3')].filter(vis).map(txt).filter(Boolean).slice(0, 15);
              return { title: document.title, headings, feedback: this.feedback(), invalid: this.invalid(), actions, inputs, tables, widgets };
            },
          };
        })()
        """;

    // An expression that installs the helper when the page has none, then calls one of its methods.
    public static string Call(string method, params object?[] args) =>
        $"(window.__omlqa || (window.__omlqa = {Helper})).{method}({string.Join(", ", args.Select(a => System.Text.Json.JsonSerializer.Serialize(a)))})";
}
