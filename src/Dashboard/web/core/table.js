// Shared widgets for every page: one table (search, filters, sort, paging, fixed columns), collapsible sections,
// tabs and icon buttons. Pages pass data and small render functions; the look and the behaviour live here once.
import { h, add, icon } from './dom.js';
import { store } from './store.js';

let uid = 0;
const nextId = prefix => `${prefix}-${++uid}`;

// A button with only an icon: the label is its accessible name and its tooltip.
export function iconButton(name, label, onclick, opts = {}) {
  return h('button', {
    class: 'icon-button' + (opts.primary ? ' primary' : '') + (opts.small ? ' small' : ''), type: 'button',
    'aria-label': label, title: label, onclick, disabled: opts.disabled,
  }, icon(name));
}

// A button with an icon and a short word, for the one main action of a card.
export function actionButton(name, label, onclick, opts = {}) {
  return h('button', { class: 'button' + (opts.primary ? ' primary' : '') + (opts.small ? ' small' : ''), type: 'button', onclick, 'aria-label': opts.aria },
    icon(name), label);
}

// A card that opens and closes; the open state is remembered per key. Returns { el, body, head }.
export function section(title, opts = {}) {
  const key = opts.key ? 'section:' + opts.key : null;
  const open = key ? store.get(key, opts.open !== false) : opts.open !== false;
  const head = h('div', { class: 'section-actions' });
  const count = h('span', { class: 'count section-count' });
  const body = h('div', { class: 'section-body' });
  const summary = h('summary', {}, icon('chevron'), h('h2', {}, title), count);
  const el = h('details', { class: 'card section', open }, summary, body);
  // Actions sit beside the title but must not toggle the section when clicked.
  head.addEventListener('click', e => e.stopPropagation());
  summary.append(head);
  el.addEventListener('toggle', () => { if (key) store.set(key, el.open); });
  return { el, body, head, count: n => { count.textContent = n === undefined || n === null ? '' : String(n); } };
}

// Tabs: a row of buttons and one pane per tab; the chosen tab is remembered per key.
export function tabs(list, opts = {}) {
  const key = opts.key ? 'tab:' + opts.key : null;
  let current = (key && store.get(key, null)) || list[0].id;
  if (!list.some(t => t.id === current)) current = list[0].id;
  const bar = h('div', { class: 'tabs', role: 'tablist', 'aria-label': opts.label || 'Sections' });
  const pane = h('div', { class: 'tab-pane', role: 'tabpanel' });
  const buttons = list.map(t => {
    const b = h('button', { type: 'button', role: 'tab', id: nextId('tab'), 'aria-selected': String(t.id === current) },
      t.icon ? icon(t.icon) : null, t.label);
    b.addEventListener('click', () => show(t.id));
    return b;
  });
  add(bar, buttons);
  const rendered = new Map();
  function show(id) {
    current = id;
    if (key) store.set(key, id);
    buttons.forEach((b, i) => b.setAttribute('aria-selected', String(list[i].id === id)));
    const i = list.findIndex(t => t.id === id);
    pane.setAttribute('aria-labelledby', buttons[i].id);
    // Each tab renders once and is kept, so a half-typed form survives switching tabs.
    if (!rendered.has(id)) { const box = h('div'); rendered.set(id, box); list[i].render(box); }
    pane.replaceChildren(rendered.get(id));
  }
  bar.addEventListener('keydown', e => {
    if (e.key !== 'ArrowRight' && e.key !== 'ArrowLeft') return;
    const i = list.findIndex(t => t.id === current);
    const j = (i + (e.key === 'ArrowRight' ? 1 : list.length - 1)) % list.length;
    show(list[j].id); buttons[j].focus();
  });
  show(current);
  return { el: h('div', { class: 'tabs-wrap' }, bar, pane), show };
}

const text = v => v === null || v === undefined ? '' : String(v);

// The table. columns: [{ key, label, width ('20%'), num, nowrap, render(row) -> node|string, value(row) -> sort/search
// value }]. filters: [{ key, label }] adds a select of the distinct values of that column. detail(row) adds an
// expand button per row that shows a full-width detail row. Returns { el, update(rows) }.
export function dataTable(opts) {
  const columns = opts.columns;
  const pageSizes = [15, 30, 100];
  let rows = opts.rows || [];
  let page = 0;
  let size = opts.pageSize || 15;
  let sortKey = opts.sort ? opts.sort.key : null;
  let sortDir = opts.sort && opts.sort.desc ? -1 : 1;
  const open = new Set();
  const details = new Map();

  const valueOf = (c, r) => c.value ? c.value(r) : r[c.key];
  const searchText = r => (columns.map(c => text(valueOf(c, r))).join(' ') + ' ' + text(r.searchText)).toLowerCase();

  const searchId = nextId('table-search');
  const search = h('input', { type: 'search', id: searchId, placeholder: opts.searchPlaceholder || 'Search', autocomplete: 'off', spellcheck: 'false' });
  const filterSelects = (opts.filters || []).map(f => {
    const id = nextId('table-filter');
    const select = h('select', { id }, h('option', { value: '' }, 'All ' + f.label.toLowerCase()));
    return { f, select, el: h('div', { class: 'table-filter' }, h('label', { for: id, class: 'sr-only' }, f.label), select) };
  });
  const info = h('span', { class: 'table-info tiny muted', 'aria-live': 'polite' });
  const first = iconButton('first', 'First page', () => go(0), { small: true });
  const prev = iconButton('prev', 'Previous page', () => go(page - 1), { small: true });
  const next = iconButton('next', 'Next page', () => go(page + 1), { small: true });
  const last = iconButton('last', 'Last page', () => go(Infinity), { small: true });
  const sizeId = nextId('table-size');
  const sizeSelect = h('select', { id: sizeId, class: 'table-size' }, pageSizes.map(n => h('option', { value: n, selected: n === size }, n + ' rows')));
  sizeSelect.addEventListener('change', () => { size = Number(sizeSelect.value); page = 0; draw(); });

  const showTools = opts.search !== false;
  const tools = showTools ? h('div', { class: 'table-tools' },
    h('div', { class: 'table-search' }, icon('search'), h('label', { for: searchId, class: 'sr-only' }, opts.searchLabel || 'Search the table'), search),
    filterSelects.map(s => s.el)) : null;

  const colgroup = h('colgroup', {}, opts.detail ? h('col', { class: 'col-toggle' }) : null,
    columns.map(c => { const col = h('col'); if (c.width) col.style.width = c.width; return col; }));
  const headCells = columns.map(c => {
    if (c.sortable === false) return h('th', { scope: 'col', class: c.num ? 'num' : '' }, c.label);
    const b = h('button', { type: 'button', class: 'sort' }, c.label, icon('sort'));
    const th = h('th', { scope: 'col', class: c.num ? 'num' : '' }, b);
    b.addEventListener('click', () => { if (sortKey === c.key) sortDir = -sortDir; else { sortKey = c.key; sortDir = 1; } page = 0; draw(); });
    return th;
  });
  const thead = h('thead', {}, h('tr', {}, opts.detail ? h('th', { scope: 'col' }, h('span', { class: 'sr-only' }, 'Details')) : null, headCells));
  const tbody = h('tbody');
  const table = h('table', { class: 'data-table' + (opts.className ? ' ' + opts.className : '') }, opts.caption ? h('caption', { class: 'sr-only' }, opts.caption) : null, colgroup, thead, tbody);
  const empty = h('p', { class: 'empty small' });
  const pager = h('div', { class: 'table-pager' }, info, h('span', { class: 'grow' }),
    h('label', { for: sizeId, class: 'sr-only' }, 'Rows per page'), sizeSelect, first, prev, next, last);
  const el = h('div', { class: 'data-table-wrap' }, tools, h('div', { class: 'table-wrap' }, table), empty, pager);

  search.addEventListener('input', () => { page = 0; draw(); });
  filterSelects.forEach(s => s.select.addEventListener('change', () => { page = 0; draw(); }));

  function fillFilters() {
    for (const s of filterSelects) {
      const keep = s.select.value;
      const values = [...new Set(rows.map(r => text(r[s.f.key])).filter(Boolean))].sort((a, b) => a.localeCompare(b));
      s.select.replaceChildren(h('option', { value: '' }, 'All ' + s.f.label.toLowerCase()), ...values.map(v => h('option', { value: v, selected: v === keep }, v)));
      s.el.hidden = values.length < 2;
    }
  }

  function go(p) { page = p; draw(); }

  function draw() {
    const words = search.value.trim().toLowerCase().split(/\s+/).filter(Boolean);
    let shown = rows.filter(r => words.every(w => searchText(r).includes(w))
      && filterSelects.every(s => !s.select.value || text(r[s.f.key]) === s.select.value));
    if (sortKey) {
      const c = columns.find(x => x.key === sortKey);
      shown = [...shown].sort((a, b) => {
        const x = valueOf(c, a), y = valueOf(c, b);
        if (typeof x === 'number' && typeof y === 'number') return (x - y) * sortDir;
        return text(x).localeCompare(text(y), undefined, { numeric: true }) * sortDir;
      });
    }
    headCells.forEach((th, i) => th.setAttribute('aria-sort', columns[i].key === sortKey ? (sortDir > 0 ? 'ascending' : 'descending') : 'none'));
    const pages = Math.max(1, Math.ceil(shown.length / size));
    page = Math.max(0, Math.min(page === Infinity ? pages - 1 : page, pages - 1));
    const slice = shown.slice(page * size, page * size + size);
    const span = columns.length + (opts.detail ? 1 : 0);
    tbody.replaceChildren(...slice.flatMap(r => {
      const cells = columns.map(c => h('td', { class: [c.num ? 'num' : '', c.nowrap ? 'nowrap' : '', c.mono ? 'mono small' : ''].filter(Boolean).join(' ') },
        c.render ? c.render(r) : text(r[c.key])));
      if (!opts.detail) return [h('tr', {}, cells)];
      const key = opts.rowKey ? opts.rowKey(r) : r;
      const expanded = open.has(key);
      const toggle = h('button', { type: 'button', class: 'row-toggle', 'aria-expanded': String(expanded), 'aria-label': 'Details' }, icon('chevron'));
      toggle.addEventListener('click', () => { if (open.has(key)) open.delete(key); else open.add(key); draw(); });
      const out = [h('tr', { class: expanded ? 'is-open' : '' }, h('td', { class: 'toggle-cell' }, toggle), cells)];
      // A detail is built once per row and kept: opening it again, searching or paging does not load it again.
      if (expanded) {
        if (!details.has(key)) details.set(key, opts.detail(r));
        out.push(h('tr', { class: 'detail-row' }, h('td', { colspan: span }, details.get(key))));
      }
      return out;
    }));
    const total = rows.length;
    empty.textContent = total === 0 ? (opts.empty || 'Nothing here yet.') : 'Nothing matches. Clear the search or the filters.';
    empty.hidden = shown.length > 0;
    table.parentElement.hidden = shown.length === 0;
    info.textContent = shown.length === 0 ? '' : `${page * size + 1}–${page * size + slice.length} of ${shown.length}` + (shown.length !== total ? ` (${total} in all)` : '');
    const many = shown.length > pageSizes[0];
    pager.hidden = shown.length === 0;
    [first, prev].forEach(b => { b.disabled = page === 0; });
    [next, last].forEach(b => { b.disabled = page >= pages - 1; });
    [first, prev, next, last, sizeSelect].forEach(b => { b.hidden = !many; });
    if (tools) tools.hidden = total <= 5;
    info.hidden = total <= 5;
  }

  fillFilters();
  draw();
  // update keeps the open rows and their details only when the caller asks (a live refresh of the same rows).
  return { el, update(newRows, keep) { rows = newRows || []; if (!keep) details.clear(); fillFilters(); draw(); }, rows: () => rows };
}
