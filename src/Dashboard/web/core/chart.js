// Small SVG charts built with the DOM (no library): the environment score over time and a module's sparkline.
// Colours come from CSS classes (the page's CSP blocks style attributes), so both themes follow the tokens.
import { h, add } from './dom.js';
import { onLeave } from './lifecycle.js';

const NS = 'http://www.w3.org/2000/svg';
function s(tag, attrs, ...children) {
  const el = document.createElementNS(NS, tag);
  for (const [k, v] of Object.entries(attrs || {})) if (v !== null && v !== undefined) el.setAttribute(k, v);
  for (const c of children.flat()) if (c !== null && c !== undefined) el.append(c instanceof Node ? c : document.createTextNode(String(c)));
  return el;
}

export const when = t => { const d = new Date(t); return isNaN(d) ? String(t || '') : d.toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }); };
const day = t => { const d = new Date(t); return isNaN(d) ? '' : d.toLocaleDateString([], { month: 'short', day: 'numeric' }); };
export const signed = n => n === null || n === undefined ? '' : (n > 0 ? '+' : n < 0 ? '−' : '±') + Math.abs(n);

// points: [{ time, score, change, high, medium, low, modules, apps }], oldest first. One series, so no legend: the
// card title names it. Hover or focus (arrow keys, Home, End) shows one run; the table under it has every value.
export function scoreChart(points, { label = 'Environment score' } = {}) {
  const wrap = h('div', { class: 'chart' });
  const tip = h('div', { class: 'chart-tip', role: 'status', 'aria-live': 'polite', hidden: true });
  const plot = h('div', { class: 'chart-plot', tabindex: '0', role: 'img' });
  const first = points[0], last = points[points.length - 1];
  plot.setAttribute('aria-label', `${label}: ${points.length} runs, from ${first.score} on ${when(first.time)} to ${last.score} on ${when(last.time)}. Use the arrow keys to read each run.`);
  add(wrap, plot, tip);
  let selected = -1, geometry = null;

  const draw = () => {
    const W = Math.max(280, plot.clientWidth || 640), H = 220;
    const pad = { l: 34, r: 40, t: 14, b: 28 };
    const scores = points.map(p => p.score);
    const lo = Math.max(0, Math.min(90, Math.floor((Math.min(...scores) - 6) / 10) * 10));
    const x = i => points.length === 1 ? (pad.l + W - pad.r) / 2 : pad.l + i * (W - pad.l - pad.r) / (points.length - 1);
    const y = v => pad.t + (100 - v) * (H - pad.t - pad.b) / (100 - lo);
    const ticks = []; for (let v = lo; v <= 100; v += (100 - lo) > 40 ? 20 : 10) ticks.push(v);
    // Dates under the axis: the first, the last, and up to three between when there is room.
    const every = Math.max(1, Math.ceil(points.length / Math.max(2, Math.floor((W - pad.l - pad.r) / 90))));
    const svg = s('svg', { width: W, height: H, viewBox: `0 0 ${W} ${H}`, class: 'chart-svg', 'aria-hidden': 'true' },
      ticks.map(v => s('g', { class: 'chart-grid' }, s('line', { x1: pad.l, x2: W - pad.r, y1: y(v), y2: y(v) }), s('text', { x: pad.l - 8, y: y(v) + 4, 'text-anchor': 'end' }, v))),
      points.map((p, i) => (i % every === 0 && x(points.length - 1) - x(i) >= 64) || i === points.length - 1 ? s('text', { class: 'chart-axis', x: x(i), y: H - 8, 'text-anchor': points.length === 1 ? 'middle' : i === 0 ? 'start' : i === points.length - 1 ? 'end' : 'middle' }, day(p.time)) : null),
      s('line', { class: 'chart-cross', x1: 0, x2: 0, y1: pad.t, y2: H - pad.b, visibility: 'hidden' }),
      points.length > 1 ? s('polyline', { class: 'chart-line', points: points.map((p, i) => `${x(i)},${y(p.score)}`).join(' ') }) : null,
      points.length <= 40 ? points.map((p, i) => s('circle', { class: 'chart-dot', cx: x(i), cy: y(p.score), r: 4 })) : null,
      s('circle', { class: 'chart-dot chart-focus', cx: 0, cy: 0, r: 5, visibility: 'hidden' }),
      s('text', { class: 'chart-value', x: x(points.length - 1) + 8, y: y(last.score) + 4 }, last.score));
    plot.replaceChildren(svg);
    geometry = { x, y, W, svg };
    if (selected >= 0) show(selected);
  };

  const show = i => {
    selected = i;
    const p = points[i], { x, y, W, svg } = geometry;
    const cross = svg.querySelector('.chart-cross'), dot = svg.querySelector('.chart-focus');
    cross.setAttribute('x1', x(i)); cross.setAttribute('x2', x(i)); cross.setAttribute('visibility', 'visible');
    dot.setAttribute('cx', x(i)); dot.setAttribute('cy', y(p.score)); dot.setAttribute('visibility', 'visible');
    tip.replaceChildren(h('strong', {}, when(p.time)),
      h('div', {}, `Score ${p.score}`, p.change !== null && p.change !== undefined ? h('span', { class: 'muted' }, ` (${signed(p.change)} since the run before)`) : null),
      h('div', { class: 'muted' }, `${p.high} high · ${p.medium} medium · ${p.low} low · ${p.modules} modules`),
      p.apps ? h('div', { class: 'muted' }, p.apps) : null);
    tip.hidden = false;
    // Beside the point, flipped to the left half when it would leave the chart.
    const left = x(i) > W * 0.6;
    tip.classList.toggle('left', left);
    tip.style.left = left ? '' : (x(i) + 14) + 'px';
    tip.style.right = left ? (W - x(i) + 14) + 'px' : '';
  };
  const hide = () => {
    selected = -1; tip.hidden = true;
    if (geometry) geometry.svg.querySelectorAll('.chart-cross, .chart-focus').forEach(el => el.setAttribute('visibility', 'hidden'));
  };
  const nearest = clientX => {
    const box = plot.getBoundingClientRect(), px = clientX - box.left;
    let best = 0;
    points.forEach((_, i) => { if (Math.abs(geometry.x(i) - px) < Math.abs(geometry.x(best) - px)) best = i; });
    return best;
  };
  plot.addEventListener('pointermove', e => { if (geometry) show(nearest(e.clientX)); });
  plot.addEventListener('pointerleave', () => { if (document.activeElement !== plot) hide(); });
  plot.addEventListener('focus', () => show(selected >= 0 ? selected : points.length - 1));
  plot.addEventListener('blur', hide);
  plot.addEventListener('keydown', e => {
    const to = { ArrowLeft: selected - 1, ArrowRight: selected + 1, Home: 0, End: points.length - 1 }[e.key];
    if (to === undefined) return;
    e.preventDefault();
    show(Math.max(0, Math.min(points.length - 1, to)));
  });

  // Drawn at the real width, again when the width changes (the sidebar, a phone turned).
  const observer = new ResizeObserver(() => draw());
  observer.observe(plot);
  onLeave(() => observer.disconnect());
  requestAnimationFrame(draw);
  return wrap;
}

// A module's score over the runs, as a small line. Decoration only: the numbers are in the row beside it.
export function sparkline(values) {
  const v = values.filter(n => typeof n === 'number');
  if (v.length < 2) return null;
  const W = 84, H = 22, lo = Math.min(...v), hi = Math.max(...v), span = hi - lo || 1;
  const x = i => 2 + i * (W - 4) / (v.length - 1), y = n => H - 3 - (n - lo) * (H - 6) / span;
  return s('svg', { class: 'spark', width: W, height: H, viewBox: `0 0 ${W} ${H}`, 'aria-hidden': 'true' },
    s('polyline', { points: v.map((n, i) => `${x(i)},${y(n)}`).join(' ') }),
    s('circle', { cx: x(v.length - 1), cy: y(v[v.length - 1]), r: 2.5 }));
}
