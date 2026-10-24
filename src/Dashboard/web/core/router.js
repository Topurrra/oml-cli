import { h, add } from './dom.js';
import { errorBox } from './ui.js';
import { runCleanups } from './lifecycle.js';
import { state, setModule } from './state.js';

let renderId = 0;
let moved = false;

// Render the page for the current hash, or Home. `pages` is the registry, `ctx` is passed to each page's render.
export async function route(pages, ctx) {
  runCleanups();
  const root = document.documentElement;
  const id = (location.hash.match(/^#\/([\w-]+)/) || [])[1] || 'home';
  // #/explorer?module=C:\Work\Orders.oml opens that module: a link a person can keep.
  const linked = new URLSearchParams(location.hash.split('?')[1] || '').get('module');
  if (linked && linked !== state.module) setModule(linked);
  const p = pages.find(x => x.id === id) || pages[0];
  const mine = ++renderId;
  document.querySelectorAll('.nav-link').forEach(a => { if (a.dataset.id === p.id) a.setAttribute('aria-current', 'page'); else a.removeAttribute('aria-current'); });
  document.getElementById('page-group').textContent = ctx.title(p.group);
  document.getElementById('page-title').textContent = p.title;
  document.getElementById('page-desc').textContent = p.desc;
  document.title = p.title + ' · oml dashboard';
  const main = document.getElementById('page');
  main.replaceChildren();
  root.dataset.page = p.id;
  delete root.dataset.ready;
  document.getElementById('nav').classList.remove('open');
  document.getElementById('menu-toggle').setAttribute('aria-expanded', 'false');
  if (moved) document.getElementById('page-title').focus({ preventScroll: true });
  moved = true;
  window.scrollTo(0, 0);
  try { await p.render(main, ctx); }
  catch (e) { add(main, errorBox(e)); }
  if (mine === renderId) root.dataset.ready = p.id;
}
