// The page registry, in sidebar order (see nav.js). A page is { id, group, title, description, icon, render(main, ctx) }.
import home from './home.js';
import modules from './modules.js';
import explorer from './explorer.js';
import assets from './assets.js';
import openapi from './openapi.js';
import architecture from './architecture.js';
import check from './check.js';
import review from './review.js';
import agents from './agents.js';
import lifetime from './lifetime.js';
import activity from './activity.js';
import learned from './learned.js';
import tools from './tools.js';
import prompt from './prompt.js';
import recipes from './recipes.js';
import dif from './dif.js';

export const PAGES = [
  home, modules, explorer, agents, assets, openapi, architecture, check, review,
  lifetime, activity, learned, tools,
  // Not in the sidebar (nav.js lists only agents); still reachable by URL: #/prompt, #/recipes, #/dif.
  prompt, recipes, dif,
];
