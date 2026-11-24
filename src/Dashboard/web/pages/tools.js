import { h, add, icon } from '../core/dom.js';
import { store } from '../core/store.js';
import { hashParams } from '../core/state.js';
import { TOOLS_A, badJsonAt, stripComments, validate } from '../tools.js';
import { TOOLS_B, minifyCss, beautifyCss, diffLines, luminance, formatXml, minifyXml } from '../tools-more.js';
import { TOOLS_FILES } from '../tools-assets.js';
import { dataTable } from '../core/table.js';

// Files first (they convert on this computer), then the text tools (they run only in this browser).
const GROUPS = [['Files', TOOLS_FILES], ['Text', [...TOOLS_A, ...TOOLS_B]]];
const TOOLS = GROUPS.flatMap(([, list]) => list);

// Small self-checks for the logic that is easy to get wrong, and one render of every tool (a tool that throws while it
// builds its form, as Base64 once did, fails here); a failure shows up as a script error in the page check.
function toolSelfTest() {
  const eq = (a, b, what) => { if (a !== b) throw new Error(`tools self-check ${what}: ${JSON.stringify(a)} !== ${JSON.stringify(b)}`); };
  eq(minifyCss('a :hover { color: red ; background: url("x;y.png") }\n/* c */ b{margin:0 auto;}'), 'a :hover{color:red;background:url("x;y.png")}b{margin:0 auto}', 'minify');
  eq(beautifyCss('a,b{color:red;background:url("x;y.png")}'), 'a, b {\n  color: red;\n  background: url("x;y.png");\n}\n', 'beautify');
  eq(stripComments('{"a":"//x", /* c */ "b":1} // end').replace(/\s+/g, ''), '{"a":"//x","b":1}', 'jsonc');
  eq(diffLines(['a', 'b', 'c'], ['a', 'c', 'd']).map(r => r[0]).join(''), ' - +', 'diff');
  const errors = []; validate({ entities: [{ nme: 'X' }] }, { type: 'object', properties: { entities: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'], additionalProperties: false } } }, additionalProperties: false }, '', errors, {});
  eq(errors.length, 2, 'schema');
  eq(badJsonAt('{\n  "a": 1,\n  "b": [1, 2,]\n}').at, 25, 'json position');
  eq(badJsonAt('{"a": [1, {"b": null}]}'), null, 'valid json');
  eq(Math.round((luminance([255, 255, 255]) + 0.05) / (luminance([0, 0, 0]) + 0.05)), 21, 'contrast');
  eq(formatXml('<?xml version="1.0"?><a x="1 &amp; 2"><b>t</b><c/><!--n--><p>Hi <i>you</i>!</p></a>'),
    '<?xml version="1.0"?>\n<a x="1 &amp; 2">\n  <b>t</b>\n  <c/>\n  <!--n-->\n  <p>Hi <i>you</i>!</p>\n</a>\n', 'xml format');
  eq(minifyXml('<a>\n  <b> t </b>\n  <![CDATA[x<y]]>\n</a>'), '<a><b> t </b><![CDATA[x<y]]></a>', 'xml minify');
  let bad = null; try { formatXml('<a>\n<b></a>'); } catch (e) { bad = e; }
  eq(!!(bad && bad.line), true, 'xml error line');
  for (const t of TOOLS) { try { t.render(h('div')); } catch (e) { throw new Error(`tools self-check: the ${t.name} tool does not open: ${e.message}`); } }
  // The shared table every page uses: paging, search, a filter and sorting.
  const rows = Array.from({ length: 40 }, (_, i) => ({ name: 'Item' + i, kind: i % 2 ? 'odd' : 'even', n: i }));
  const t = dataTable({ rows, columns: [{ key: 'name', label: 'Name' }, { key: 'kind', label: 'Kind' }, { key: 'n', label: 'N', num: true }], filters: [{ key: 'kind', label: 'Kinds' }] });
  const info = () => t.el.querySelector('.table-info').textContent;
  eq(info(), '1–15 of 40', 'table paging');
  eq(t.el.querySelectorAll('tbody tr').length, 15, 'table page size');
  const search = t.el.querySelector('input[type="search"]');
  search.value = 'item39'; search.dispatchEvent(new Event('input'));
  eq(t.el.querySelectorAll('tbody tr').length, 1, 'table search');
  search.value = ''; search.dispatchEvent(new Event('input'));
  const filter = t.el.querySelector('.table-filter select'); filter.value = 'odd'; filter.dispatchEvent(new Event('change'));
  eq(info(), '1–15 of 20 (40 in all)', 'table filter');
  t.el.querySelectorAll('th .sort')[2].click(); t.el.querySelectorAll('th .sort')[2].click();
  eq(t.el.querySelector('tbody tr td').textContent, 'Item39', 'table sort');
  return true;
}

export default {
  id: 'tools', group: 'Tools', title: 'Developer tools', icon: 'tools',
  description: 'Convert images and fonts for the web, and small text tools. Text tools run only in this browser; files are converted on this computer and deleted after.',
  render(main) {
    // #/tools?tool=images opens that tool (the old Fonts and images page links here).
    let current = hashParams().get('tool') || store.get('tool', TOOLS[0].id);
    if (!TOOLS.some(t => t.id === current)) current = TOOLS[0].id;
    const list = h('nav', { class: 'panel tool-list', 'aria-label': 'Tools' });
    const body = h('section', { class: 'card', 'aria-live': 'off' });
    const draw = () => {
      list.querySelectorAll('button').forEach(b => b.setAttribute('aria-current', String(b.dataset.id === current)));
      const tool = TOOLS.find(t => t.id === current);
      body.replaceChildren(h('h2', {}, tool.name), h('p', { class: 'muted small' }, tool.desc));
      tool.render(body);
    };
    for (const [title, tools] of GROUPS) {
      add(list, h('p', { class: 'tool-group' }, title),
        tools.map(t => h('button', { type: 'button', 'data-id': t.id, onclick: () => { current = t.id; store.set('tool', t.id); draw(); } }, t.icon ? icon(t.icon) : null, t.name)));
    }
    add(main, h('div', { class: 'tools' }, list, body));
    draw();
    setTimeout(toolSelfTest); // a broken tool shows up as a script error
  },
};
