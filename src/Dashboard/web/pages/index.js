// The page registry, in sidebar order (see nav.js). A page is { id, group, title, description, icon, render(main, ctx) }.
import home from './home.js';
import modules from './modules.js';
import explorer from './explorer.js';
import previews from './previews.js';
import prompt from './prompt.js';
import recipes from './recipes.js';
import assets from './assets.js';
import openapi from './openapi.js';
import architecture from './architecture.js';
import check from './check.js';
import review from './review.js';
import lifetime from './lifetime.js';
import activity from './activity.js';
import cookbook from './cookbook.js';
import docs from './docs.js';
import rules from './rules.js';
import tools from './tools.js';

export const PAGES = [
  home, modules, explorer, previews, prompt, recipes, assets, openapi, architecture, check, review,
  lifetime, activity, cookbook, docs, rules, tools,
];
