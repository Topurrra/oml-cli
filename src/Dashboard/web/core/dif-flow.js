import { h, add } from './dom.js';

// The Dif page's flow diagrams: an oml flow graph (nodes with Service Studio positions, connectors) drawn as SVG, and
// the merge of two graphs for `flow diff` (new nodes and connectors green, removed red and dashed, changed amber).
// Model units: one Service Studio grid step is 2660; a node is 914 wide. Every node and label is built with
// createElementNS and textContent, so text from the module is never parsed as markup.
const NS = 'http://www.w3.org/2000/svg';
const STEP = 2660, PX = 190 / STEP, PY = 112 / STEP, W = 156, H = 46, PAD = 28;

function s(tag, attrs, ...children) {
  const el = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs || {})) if (v !== null && v !== undefined && v !== false) el.setAttribute(k, v);
  for (const c of children.flat()) if (c !== null && c !== undefined && c !== false) el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return el;
}

const clip = (t, n) => (t = String(t || '')).length > n ? t.slice(0, n - 1) + '…' : t;

// Before and after as one graph. A node is matched by its id (its path segment in the flow); a removed node keeps
// its old place unless a node of the new flow sits there, then it moves right until the place is free.
export function mergeFlows(before, after) {
  const b = new Map((before?.nodes || []).map(n => [n.id, n]));
  const a = new Map((after?.nodes || []).map(n => [n.id, n]));
  const nodes = [];
  for (const n of a.values()) {
    const old = b.get(n.id);
    const status = !before ? 'add' : !old ? 'add' : (old.kind !== n.kind || (old.detail || '') !== (n.detail || '')) ? 'chg' : 'same';
    nodes.push({ ...n, status, before: old && status === 'chg' ? old.detail || old.kind : undefined });
  }
  const taken = (x, y) => nodes.some(n => Math.abs(n.x - x) < STEP * 0.6 && Math.abs(n.y - y) < STEP * 0.6);
  for (const n of b.values()) {
    if (a.has(n.id)) continue;
    let x = n.x;
    while (taken(x, n.y)) x += STEP;
    nodes.push({ ...n, x, status: 'del' });
  }
  const key = e => e.from + '\u0001' + e.to + '\u0001' + e.kind;
  const be = new Set((before?.edges || []).map(key));
  const ae = new Set((after?.edges || []).map(key));
  const edges = [
    ...(after?.edges || []).map(e => ({ ...e, status: !before || !be.has(key(e)) ? 'add' : 'same' })),
    ...(before?.edges || []).filter(e => !ae.has(key(e))).map(e => ({ ...e, status: 'del' })),
  ];
  return { element: after?.element || before?.element, nodes, edges };
}

// The diagram. onNode(node) is called on a click or Enter on a node.
export function flowView(graph, { title, onNode, diff } = {}) {
  const nodes = graph.nodes || [];
  if (!nodes.length) return h('p', { class: 'muted' }, 'This flow has no nodes.');
  const minX = Math.min(...nodes.map(n => n.x)), minY = Math.min(...nodes.map(n => n.y));
  const pos = new Map(nodes.map(n => [n.id + (n.status === 'del' ? '\u0002' : ''), { cx: PAD + 60 + W / 2 + (n.x - minX) * PX, cy: PAD + H / 2 + (n.y - minY) * PY }]));
  const at = (id, status) => pos.get(id + (status === 'del' ? '\u0002' : '')) || pos.get(id) || pos.get(id + '\u0002');
  let right = Math.max(...[...pos.values()].map(p => p.cx)) + W / 2 + PAD;
  const height = Math.max(...[...pos.values()].map(p => p.cy)) + H / 2 + PAD;
  const svg = s('svg', { class: 'flow-svg', role: 'img', 'aria-label': title || ('Flow of ' + graph.element) });
  const defs = s('defs');
  for (const st of ['same', 'add', 'del']) defs.append(s('marker', { id: 'flow-arrow-' + st + '-' + uid, class: 'flow-arrow ' + st, viewBox: '0 0 10 10', refX: 9, refY: 5, markerWidth: 7, markerHeight: 7, orient: 'auto-start-reverse' }, s('path', { d: 'M0,0 L10,5 L0,10 z' })));
  svg.append(defs);

  // Connectors first, under the nodes. A connector that would cross other nodes in its column, or that goes back up
  // (a loop), runs beside the column instead of through it.
  const edgesG = s('g', { class: 'flow-edges' });
  const lanes = new Map();
  for (const e of graph.edges || []) {
    const fromNode = nodes.find(n => n.id === e.from && (e.status === 'del' || n.status !== 'del')) || nodes.find(n => n.id === e.from);
    const toNode = nodes.find(n => n.id === e.to && (e.status === 'del' || n.status !== 'del')) || nodes.find(n => n.id === e.to);
    if (!fromNode || !toNode) continue;
    const p = at(fromNode.id, fromNode.status), q = at(toNode.id, toNode.status);
    const sameColumn = Math.abs(p.cx - q.cx) < 1;
    const between = nodes.some(n => n !== fromNode && n !== toNode && Math.abs(at(n.id, n.status).cx - p.cx) < 1 && at(n.id, n.status).cy > Math.min(p.cy, q.cy) && at(n.id, n.status).cy < Math.max(p.cy, q.cy));
    let d, lx, ly;
    if (q.cy > p.cy && sameColumn && !between) { d = `M${p.cx},${p.cy + H / 2} L${q.cx},${q.cy - H / 2}`; lx = p.cx + 6; ly = p.cy + H / 2 + 14; }
    else if (q.cy > p.cy && !sameColumn) {
      const mid = p.cy + H / 2 + 18;
      d = `M${p.cx},${p.cy + H / 2} L${p.cx},${mid} L${q.cx},${mid} L${q.cx},${q.cy - H / 2}`; lx = p.cx + 6; ly = mid - 4;
    } else {
      // Beside the column: each such connector of a column gets its own lane, so they do not lie on each other.
      const back = q.cy <= p.cy;
      const side = back ? -1 : 1;
      const lane = (lanes.get(p.cx + side) || 0) + 1;
      lanes.set(p.cx + side, lane);
      const x = Math.max(p.cx, q.cx) * (side > 0 ? 1 : 0) + Math.min(p.cx, q.cx) * (side < 0 ? 1 : 0) + side * (W / 2 + 14 + lane * 10);
      d = `M${p.cx + side * W / 2},${p.cy} L${x},${p.cy} L${x},${q.cy} L${q.cx + side * W / 2},${q.cy}`; lx = x + 4 * side; ly = p.cy - 5;
      right = Math.max(right, x + 70);
    }
    const st = e.status || 'same';
    const g = s('g', { class: 'flow-edge ' + st }, s('path', { d, 'marker-end': `url(#flow-arrow-${st}-${uid})` }));
    if (e.kind && e.kind !== 'Sequence' && e.kind !== 'Connector') g.append(s('text', { x: lx, y: ly, 'text-anchor': lx < p.cx ? 'end' : 'start' }, e.kind));
    edgesG.append(g);
  }
  svg.append(edgesG);

  const nodesG = s('g', { class: 'flow-nodes' });
  for (const n of nodes) {
    const { cx, cy } = at(n.id, n.status);
    const x = cx - W / 2, y = cy - H / 2;
    const shape = n.kind === 'If' || n.kind === 'Switch'
      ? s('polygon', { points: `${cx},${y} ${x + W},${cy} ${cx},${y + H} ${x},${cy}` })
      : s('rect', { x, y, width: W, height: H, rx: n.kind === 'Start' || n.kind === 'End' ? H / 2 : 8 });
    const label = { add: 'new', del: 'removed', chg: 'changed' }[n.status];
    const tip = [`${n.name || n.id} (${n.kind})`, n.detail, n.before ? 'before: ' + n.before : null, label && diff ? label : null].filter(Boolean).join('\n');
    const g = s('g', { class: 'flow-node ' + (n.status || 'same'), tabindex: onNode ? 0 : null, role: onNode ? 'button' : null, 'aria-label': tip.replace(/\n/g, '; ') },
      s('title', {}, tip), shape,
      ...((n.name || n.id) === n.kind && !n.detail
        ? [s('text', { x: cx, y: cy + 4, 'text-anchor': 'middle', class: 'flow-name' }, n.kind)]
        : [s('text', { x: cx, y: cy - 3, 'text-anchor': 'middle', class: 'flow-name' }, clip(n.name || n.id, 20)),
           s('text', { x: cx, y: cy + 13, 'text-anchor': 'middle', class: 'flow-kind' }, clip(n.kind + (n.detail ? ': ' + n.detail : ''), 26))]));
    if (onNode) {
      g.addEventListener('click', () => onNode(n));
      g.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onNode(n); } });
    }
    nodesG.append(g);
  }
  svg.append(nodesG);
  uid++;
  const width = Math.ceil(right + 10);
  svg.setAttribute('viewBox', `0 0 ${width} ${Math.ceil(height)}`);
  svg.setAttribute('width', width);
  svg.setAttribute('height', Math.ceil(height));

  const counts = diff ? ['add', 'del', 'chg'].map(st => [st, nodes.filter(n => n.status === st).length]) : [];
  return h('figure', { class: 'flow-figure' },
    title ? h('figcaption', {}, title) : null,
    h('div', { class: 'flow-scroll' }, svg),
    diff ? h('div', { class: 'row small flow-legend' },
      h('span', { class: 'badge ok' }, `${counts[0][1]} new`), h('span', { class: 'badge fail' }, `${counts[1][1]} removed`), h('span', { class: 'badge warn' }, `${counts[2][1]} changed`),
      h('span', { class: 'muted tiny' }, 'Select a node to see it in the module.')) : h('p', { class: 'muted tiny' }, 'Select a node to see it in the module.'));
}
let uid = 1;
