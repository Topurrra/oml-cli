// The page registry, in sidebar order (see nav.js). A page is { id, group, title, description, icon, render(main, ctx) }.
import home from './home.js';
import modules from './modules.js';
import explorer from './explorer.js';
import architecture from './architecture.js';
import agents from './agents.js';
import lifetime from './lifetime.js';
import server from './server.js';
import activity from './activity.js';
import learned from './learned.js';
import tools from './tools.js';
import prompt from './prompt.js';
import recipes from './recipes.js';
import dif from './dif.js';

export const PAGES = [
  home, modules, explorer, architecture, agents, activity, learned, server, lifetime, tools,
  // Not in the sidebar: Work with agents shows them as tabs. Still reachable by URL: #/prompt, #/recipes, #/dif.
  prompt, recipes, dif,
];
