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
          const name = id => !id || id.startsWith('$') ? null : id.split('-').pop();
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
          return {
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
            // oml preview --live: take out the page's rules that the old style sheets hold (matched by the browser's own
            // text of each rule, so both sides are parsed the same way) and add the new style sheets after everything else.
            restyle(removed, added) {
              const rules = css => { const s = new CSSStyleSheet(); try { s.replaceSync(css); } catch (e) { return []; } return [...s.cssRules].map(r => r.cssText); };
              const old = new Set(removed.flatMap(rules));
              const found = new Set();
              let unreadable = 0;
              for (const sheet of [...document.styleSheets]) {
                let list; try { list = sheet.cssRules; } catch (e) { unreadable++; continue; }
                for (let i = list.length - 1; i >= 0; i--) if (old.has(list[i].cssText)) { found.add(list[i].cssText); sheet.deleteRule(i); }
              }
              const s = new CSSStyleSheet();
              s.replaceSync(added.join('\n'));
              document.adoptedStyleSheets = [...document.adoptedStyleSheets, s];
              return { oldRules: old.size, oldRulesFound: found.size, newRules: s.cssRules.length, unreadableSheets: unreadable };
            },
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
