import { h, add } from '../core/dom.js';
import { store } from '../core/store.js';
import { TOOLS_A, badJsonAt, stripComments, validate } from '../tools.js';
import { TOOLS_B, minifyCss, beautifyCss, diffLines, luminance } from '../tools-more.js';

const TOOLS = [...TOOLS_A, ...TOOLS_B];

// Small self-checks for the logic that is easy to get wrong; a failure shows up as a script error in the page check.
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
  return true;
}

export default {
  id: 'tools', group: 'Tools', title: 'Developer tools', icon: 'tools',
  description: 'Small tools that run only in this browser. Nothing you type here is sent anywhere.',
  render(main) {
    let current = store.get('tool', TOOLS[0] && TOOLS[0].id);
    if (!TOOLS.some(t => t.id === current)) current = TOOLS[0].id;
    const list = h('div', { class: 'panel tool-list', role: 'navigation', 'aria-label': 'Tools' });
    const body = h('section', { class: 'card', 'aria-live': 'off' });
    const draw = () => {
      list.querySelectorAll('button').forEach(b => b.setAttribute('aria-current', String(b.dataset.id === current)));
      const tool = TOOLS.find(t => t.id === current);
      body.replaceChildren(h('h2', {}, tool.name), h('p', { class: 'muted small' }, tool.desc));
      tool.render(body);
    };
    for (const t of TOOLS) add(list, h('button', { type: 'button', 'data-id': t.id, onclick: () => { current = t.id; store.set('tool', t.id); draw(); } }, t.name));
    add(main, h('div', { class: 'tools' }, list, body));
    draw();
    setTimeout(toolSelfTest); // a broken tool shows up as a script error
  },
};
