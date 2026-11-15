// The agent page: everything an agent session needs in one place. It renders the prompt builder, the ready
// prompts and the change walkthrough (oml show-change) as three sections of one page.
import { h, add } from '../core/dom.js';
import prompt from './prompt.js';
import recipes from './recipes.js';
import dif from './dif.js';

export default {
  id: 'agents', group: 'Work with agents', title: 'Work with agents', icon: 'prompt',
  description: 'Write a task for an agent, copy a ready prompt, and read the change walkthrough it made.',
  async render(main, ctx) {
    for (const [section, page] of [['Write a task', prompt], ['Ready prompts', recipes], ['Change walkthroughs', dif]]) {
      add(main, h('section', { class: 'card', 'aria-labelledby': 'agents-' + page.id },
        h('h2', { id: 'agents-' + page.id }, section)));
      await page.render(main, ctx);
    }
  },
};
