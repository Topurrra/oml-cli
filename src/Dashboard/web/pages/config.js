// Work with agents > Config: the project settings in .oml/oml-config.json, the profile agents follow. Every setting
// of `oml config show`, grouped, with the right control for its kind; a change is saved at once with `oml config set`,
// which checks it (kind, range, locked). Logins and tokens are never settings, so none can be typed here.
import { h, add, icon } from '../core/dom.js';
import { api, need } from '../core/api.js';
import { task, cli, toast, errorBox } from '../core/ui.js';
import { store } from '../core/store.js';
import { section, iconButton } from '../core/table.js';

const GROUPS = [
  ['ui', 'Screens and UI', 'What the app supports: agents design and test only these.'],
  ['checks', 'Checks agents run', 'Steps an agent runs on a change before it reports.'],
  ['ask', 'Questions agents ask', 'Off: the agent decides from the other settings and says what it did.'],
  ['autoApprove', 'Without asking', 'On: the agent runs these without your yes. Entity data is never deleted.'],
  ['commands', 'Commands oml may run', 'Off: oml refuses the command, and the agent leaves the step to you.'],
  ['project', 'Project and servers', 'Addresses and names. Logins and tokens stay in environment variables, never here.'],
  ['habits', 'Agent habits', 'Code mode, memory, docs and the application map.'],
  ['hooks', 'Hooks', 'Guards the agent hooks apply (oml hooks install).'],
  ['more', 'Apply, migrate and output', null],
  ['performance', 'Speed', 'Warm workers keep Service Studio loaded between commands.'],
];
const groupOf = key => {
  const head = key.split('.')[0];
  if (['ui', 'checks', 'ask', 'autoApprove', 'commands', 'hooks', 'performance'].includes(head)) return head;
  if (['app', 'author', 'outsystems', 'browser'].includes(head)) return 'project';
  if (['code', 'memory', 'docs', 'map'].includes(head)) return 'habits';
  return 'more';
};
const label = key => {
  const last = key.includes('.') ? key.split('.').slice(1).join('.') : key;
  const words = { Ui: 'UI', Url: 'URL', Mcp: 'MCP', Odc: 'ODC', Rtl: 'RTL', Api: 'API', Dev: 'DEV' };
  return last.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, c => c.toUpperCase()).split(' ').map(w => words[w] || w).join(' ');
};

export default {
  id: 'config', group: 'Agents', title: 'Project settings', icon: 'settings',
  description: 'The settings in .oml/oml-config.json: what agents do, skip and ask in this project.',
  async render(main) {
    const dir = h('input', { type: 'text', id: 'cfg-dir', class: 'code', placeholder: 'Project folder (empty: where the dashboard started)', spellcheck: 'false', value: store.get('config-dir', '') });
    const filter = h('input', { type: 'search', id: 'cfg-filter', placeholder: 'Search settings', autocomplete: 'off' });
    const status = h('div');
    const body = h('div', { class: 'stack' });
    const reload = iconButton('refresh', 'Read the settings again', () => load(), { primary: true });
    dir.addEventListener('keydown', e => { if (e.key === 'Enter') load(); });
    add(main, h('div', { class: 'toolbar' }, h('label', { for: 'cfg-dir', class: 'sr-only' }, 'Project folder'), h('div', { class: 'grow' }, dir), reload),
      status, h('div', { class: 'toolbar cfg-search' }, h('div', { class: 'table-search' }, icon('search'), h('label', { for: 'cfg-filter', class: 'sr-only' }, 'Search settings'), filter)), body);

    let rows = [];
    filter.addEventListener('input', () => {
      const q = filter.value.trim().toLowerCase();
      for (const r of rows) r.el.hidden = !!q && !(r.key + ' ' + r.about).toLowerCase().includes(q);
      body.querySelectorAll('details.section').forEach(s => { const any = [...s.querySelectorAll('.cfg-row')].some(x => !x.hidden); s.hidden = !any; if (q && any) s.open = true; });
    });

    async function load() {
      store.set('config-dir', dir.value.trim());
      body.replaceChildren();
      const data = await task(reload, status, 'Reading the settings…', async () => need(await api('config' + (dir.value.trim() ? '?dir=' + encodeURIComponent(dir.value.trim()) : '')), [0, 1]));
      if (!data) return;
      const o = data.output || {};
      if (!dir.value.trim()) dir.placeholder = data.folder;
      const missing = /\(not there yet\)$/.test(o.file || '');
      status.replaceChildren();
      add(status,
        h('p', { class: 'hint' }, missing ? 'No settings file yet: these are the defaults. The first change writes ' : 'From ', h('code', { text: String(o.file || '').replace(/ \(not there yet\)$/, '') }), '.'),
        o.error ? h('div', { class: 'message error', role: 'alert' }, h('strong', {}, 'The file has a problem. '), o.error) : null,
        (o.problems || []).length ? h('div', { class: 'message warn' }, h('strong', {}, 'Ignored: '), o.problems.join(' ')) : null);
      rows = [];
      const byGroup = new Map(GROUPS.map(([id]) => [id, []]));
      for (const s of o.settings || []) byGroup.get(groupOf(s.key)).push(s);
      for (const [id, title, hint] of GROUPS) {
        const list = byGroup.get(id);
        if (!list.length) continue;
        const sec = section(title, { key: 'config-' + id, open: id === 'checks' || id === 'autoApprove' });
        const changed = list.filter(s => s.default !== null && s.default !== undefined).length;
        sec.count(changed ? `${list.length} · ${changed} changed` : list.length);
        add(sec.body, hint ? h('p', { class: 'hint cfg-group-hint' }, hint) : null, list.map(s => { const r = row(s); rows.push(r); return r.el; }));
        add(body, sec.el);
      }
      add(body, cli('oml config show' + (dir.value.trim() ? ' --dir ' + quote(dir.value.trim()) : '')));
    }

    await load();

    // One setting: its name, what it does, its control, and "Default: x" with a reset when it differs.
    function row(s) {
      const id = 'cfg-' + s.key.replace(/\W/g, '-');
      const note = h('span', { class: 'cfg-saved', 'aria-live': 'polite' });
      const save = async value => {
        note.textContent = 'Saving…'; note.className = 'cfg-saved';
        try {
          const r = need(await api('config/set', { key: s.key, value, dir: dir.value.trim() || undefined }));
          s.value = r.output && r.output.value !== undefined ? String(r.output.value) : value;
          note.textContent = 'Saved'; note.className = 'cfg-saved ok';
          toast(`${s.key} = ${s.value === '' ? '(empty)' : s.value}`);
          setTimeout(() => { if (note.textContent === 'Saved') note.textContent = ''; }, 2500);
          return true;
        } catch (e) {
          note.replaceChildren(); note.className = 'cfg-saved';
          add(note, errorBox(e));
          return false;
        }
      };
      const control = controlFor(s, id, save);
      const reset = s.default !== null && s.default !== undefined && !s.locked
        ? h('span', { class: 'cfg-default' }, 'Default: ', h('code', { text: s.default === '' ? '(empty)' : String(s.default) }),
          iconButton('refresh', `Set ${s.key} back to its default`, async () => { if (await save(String(s.default))) load(); }, { small: true }))
        : null;
      const el = h('div', { class: 'cfg-row' + (s.locked ? ' locked' : '') },
        h('div', { class: 'cfg-text' }, h('label', { for: id, class: 'cfg-name' }, label(s.key), ' ', h('code', { class: 'cfg-key', text: s.key })),
          h('p', { class: 'cfg-about' }, s.about), s.locked ? h('p', { class: 'hint' }, 'Locked: this guard always stays on.') : null, reset),
        h('div', { class: 'cfg-control' }, control, note));
      return { el, key: s.key, about: s.about || '' };
    }
  },
};

const quote = s => /[\s"]/.test(s) ? '"' + s.replace(/"/g, '\\"') + '"' : s;

// The control for a setting, from the choices oml config show gives: on/off is a switch, one or more of a list is a set
// of checkboxes, a few words is a row of buttons, a number range is a number box, anything else is a text box.
function controlFor(s, id, save) {
  const choices = s.choices || '';
  if (choices === 'on, off') {
    const box = h('input', { type: 'checkbox', id, role: 'switch', class: 'switch', checked: s.value === 'true', disabled: !!s.locked });
    box.addEventListener('change', async () => { if (!await save(box.checked ? 'on' : 'off')) box.checked = !box.checked; });
    return box;
  }
  const many = /^(.*) \(one or more, comma-separated\)$/.exec(choices);
  if (many) {
    const options = many[1].split(', ');
    const picked = new Set(String(s.value).split(',').map(x => x.trim()).filter(Boolean));
    const boxes = options.map(v => h('input', { type: 'checkbox', id: id + '-' + v, value: v, checked: picked.has(v) }));
    const group = h('div', { class: 'cfg-many', role: 'group', id, 'aria-label': s.key }, boxes.map((b, i) => h('label', { class: 'check inline', for: b.id }, b, options[i])));
    boxes.forEach(b => b.addEventListener('change', async () => {
      const value = boxes.filter(x => x.checked).map(x => x.value).join(',');
      if (!value) { b.checked = true; toast('Keep at least one.'); return; }
      if (!await save(value)) b.checked = !b.checked;
    }));
    return group;
  }
  const range = /^a whole number from (\d+) to (\d+)$/.exec(choices);
  if (range) {
    const input = h('input', { type: 'number', id, min: range[1], max: range[2], step: '1', value: s.value, class: 'cfg-number' });
    input.addEventListener('change', async () => { if (!input.checkValidity()) { input.reportValidity(); return; } if (!await save(input.value)) input.value = s.value; });
    return input;
  }
  const words = choices && !/an address/.test(choices) ? choices.split(', ') : null;
  if (words && words.length <= 4 && words.every(w => /^[a-z]+$/.test(w))) {
    const group = h('div', { class: 'segmented', role: 'group', id, 'aria-label': s.key });
    for (const w of words) {
      const b = h('button', { type: 'button', 'aria-pressed': String(w === s.value) }, w);
      b.addEventListener('click', async () => {
        if (await save(w)) group.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b)));
      });
      add(group, b);
    }
    return group;
  }
  const input = h('input', { type: 'text', id, class: 'code', value: s.value, placeholder: /an address/.test(choices) ? 'Empty: the default' : 'Not set', spellcheck: 'false' });
  let last = s.value;
  // After a save the box shows what oml config stored (it drops https:// and a path from a host, for example).
  const commit = async () => { if (input.value.trim() === last) return; if (await save(input.value.trim())) { last = s.value; input.value = s.value; } else input.value = last; };
  input.addEventListener('change', commit);
  input.addEventListener('keydown', e => { if (e.key === 'Enter') commit(); });
  return input;
}
