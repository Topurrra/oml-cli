import { h, add } from '../core/dom.js';
import { cli } from '../core/ui.js';
import { cookbook } from '../data/cookbook.js';

export default {
  id: 'cookbook', group: 'Learn', title: 'Command cookbook', icon: 'cookbook',
  description: 'Every oml command with a real example. Copy one and paste it into PowerShell.',
  render(main) {
    const search = h('input', { type: 'search', placeholder: 'Search commands, for example preview or font', 'aria-label': 'Search commands' });
    const list = h('div');
    const entries = [];
    for (const group of cookbook) {
      const section = h('section', { class: 'card cook-group', 'aria-label': group.group }, h('h2', {}, group.group));
      const rows = group.items.map(([command, text]) => {
        const row = h('div', { class: 'cook' }, h('p', {}, text), cli(command));
        entries.push([(command + ' ' + text + ' ' + group.group).toLowerCase(), row, section]);
        return row;
      });
      add(section, rows);
      add(list, section);
    }
    search.addEventListener('input', () => {
      const q = search.value.trim().toLowerCase();
      const visible = new Set();
      for (const [t, row, section] of entries) { row.hidden = q && !q.split(/\s+/).every(w => t.includes(w)); if (!row.hidden) visible.add(section); }
      list.querySelectorAll('.cook-group').forEach(s => { s.hidden = !visible.has(s); });
    });
    add(main, h('div', { class: 'field' }, search), list,
      h('p', { class: 'hint' }, 'Full reference: skill/reference/commands.md. Run oml --help for the short list.'));
  },
};
