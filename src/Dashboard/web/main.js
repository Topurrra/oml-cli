// The shell: starts the page, the router and the sidebar. Pages live in pages/, shared helpers in core/.
import { h, add, icon } from './core/dom.js';
import { store } from './core/store.js';
import { api } from './core/api.js';
import * as ui from './core/ui.js';
import { state } from './core/state.js';
import { route } from './core/router.js';
import { viewSrc } from './core/viewer.js';
import { PAGES } from './pages/index.js';
import { NAV } from './nav.js';

const TITLES = Object.fromEntries(NAV.map(g => [g.group, g.title]));
const ctx = { title: g => TITLES[g] || g, pages: PAGES };

function buildNav() {
  const nav = document.getElementById('nav');
  const byId = new Map(PAGES.map(p => [p.id, p]));
  let count = 0;
  for (const group of NAV) {
    const links = group.ids.map(id => byId.get(id)).filter(Boolean);
    if (!links.length) continue;
    add(nav, h('div', { class: 'nav-group' }, h('p', { class: 'nav-group-title' }, group.title),
      links.map(p => h('a', { class: 'nav-link', href: '#/' + p.id, 'data-id': p.id }, icon(p.id), p.title))));
    count += links.length;
  }
  // The sidebar and the registry agree; if a page is missing from NAV it is still reachable but not listed.
  return count;
}

async function start() {
  buildNav();
  const root = document.documentElement;

  const toggle = document.getElementById('theme-toggle');
  const label = () => toggle.setAttribute('aria-label', `Switch to ${root.dataset.theme === 'dark' ? 'light' : 'dark'} theme`);
  label();
  toggle.addEventListener('click', () => {
    root.dataset.theme = root.dataset.theme === 'dark' ? 'light' : 'dark';
    try { localStorage.setItem('oml-theme', root.dataset.theme); } catch { /* not remembered */ }
    label();
    // Report frames follow: only the #theme= part changes, so the report does not load again.
    document.querySelectorAll('iframe[data-themed]').forEach(f => { f.src = viewSrc({ view: f.dataset.themed, themed: true }); });
  });

  ui.dropGuard();

  // Ctrl+K (or Cmd+K): the docs search, from any page.
  const openSearch = () => {
    const box = root.dataset.page === 'docs' && document.getElementById('docs-q');
    if (box) { box.focus(); box.select(); return; }
    location.hash = '#/docs?focus=' + Date.now();
  };
  document.getElementById('search-open').addEventListener('click', openSearch);
  document.addEventListener('keydown', e => { if ((e.ctrlKey || e.metaKey) && !e.altKey && e.key.toLowerCase() === 'k') { e.preventDefault(); openSearch(); } });

  // "Show timings": adds --timings to the commands that load a module, and shows the phases under the CLI line.
  const timings = document.getElementById('timings-toggle');
  const showTimings = () => timings.setAttribute('aria-pressed', String(!!store.get('timings', false)));
  showTimings();
  timings.addEventListener('click', () => { store.set('timings', !store.get('timings', false)); showTimings(); ui.toast(store.get('timings', false) ? 'Timings on: commands add --timings' : 'Timings off'); });

  const menu = document.getElementById('menu-toggle');
  menu.addEventListener('click', () => {
    const nav = document.getElementById('nav');
    nav.classList.toggle('open');
    menu.setAttribute('aria-expanded', String(nav.classList.contains('open')));
  });
  document.getElementById('stop').addEventListener('click', async () => {
    if (!confirm('Stop the oml dashboard? You can start it again with oml dashboard.')) return;
    try { await api('shutdown', {}); } catch { /* it may already be gone */ }
    document.getElementById('page').replaceChildren(h('div', { class: 'empty' }, 'The dashboard has stopped. You can close this tab.'));
  });

  const KEY = new URLSearchParams(location.search).get('key') || '';
  if (!KEY) {
    document.getElementById('page').append(h('div', { class: 'message error', role: 'alert' }, 'This page needs its key. Open the full address that oml dashboard printed in its window.'));
    return;
  }
  try {
    state.info = await api('info');
    document.getElementById('version').textContent = state.info.version ? 'oml ' + state.info.version : '';
  } catch (e) { document.getElementById('page').append(ui.errorBox(e)); }
  window.addEventListener('hashchange', () => route(PAGES, ctx));
  await route(PAGES, ctx);
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
