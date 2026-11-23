// The agent page: everything an agent session needs in one place, as three tabs: the prompt builder, the ready
// prompts, and the change walkthrough an agent wrote (oml dashboard --change). Each tab renders once and is kept.
import { add } from '../core/dom.js';
import { tabs } from '../core/table.js';
import prompt from './prompt.js';
import recipes from './recipes.js';
import dif from './dif.js';

export default {
  id: 'agents', group: 'Agents', title: 'Work with agents', icon: 'prompt',
  description: 'Write a task for an agent, copy a ready prompt, and read the change walkthrough it made.',
  async render(main, ctx) {
    add(main, tabs([
      { id: 'prompt', label: 'Write a task', icon: 'prompt', render: box => prompt.render(box, ctx) },
      { id: 'recipes', label: 'Ready prompts', icon: 'recipes', render: box => recipes.render(box, ctx) },
      { id: 'dif', label: 'Change walkthrough', icon: 'dif', render: box => dif.render(box, ctx) },
    ], { key: 'agents-tab', label: 'Work with agents' }).el);
  },
};
