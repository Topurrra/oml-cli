import { h, add } from '../core/dom.js';
import { copyButton } from '../core/ui.js';
import { PROMPTS } from '../data/prompts.js';

export default {
  id: 'recipes', group: 'Agents', title: 'Prompts for agents', icon: 'recipes',
  description: 'Two ready prompts for an agent: a small task or an architect task. Copy one, replace the placeholders, and paste it into your agent.',
  render(main) {
    const cards = PROMPTS.map(p => h('article', { class: 'card prompt-card', 'aria-label': p.title + ' prompt' },
      h('h3', {}, p.title),
      h('p', { class: 'muted small' }, 'When to use: ' + p.when),
      h('div', { class: 'row' },
        copyButton(p.text, 'Copy', 'Prompt', 'Copy the ' + p.title + ' prompt'),
        h('details', {},
          h('summary', {}, 'Show the template'),
          h('pre', { class: 'mono small', text: p.text })))));
    add(main, h('section', { class: 'stack', 'aria-label': 'Prompts for agents' }, cards));
  },
};
