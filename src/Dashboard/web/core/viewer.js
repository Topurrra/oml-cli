import { h, add } from './dom.js';
import { onLeave } from './lifecycle.js';

// Viewer: one way to show every image and frame.
//
// viewer(opts) returns an element: a toolbar and the picture. viewer.open(opts) shows the same in a full-window dialog
// (Esc closes it). Use it for every image and /view/ page, so they all zoom and open the same way.
//   title       required: the accessible name (the image alt text or the frame title)
//   src         an image address (blob: or data:), or
//   view        the id of a /view/<id> page (a preview or a report), with width and height: its size in pixels
//   themed      true for reports (architecture, check, review): the frame follows the dashboard theme (#theme=)
//   fullHeight  true for previews: offers "Full height"; the page reports its height (see ReportsHeight in DashboardApi.cs)
//   before      { src, label }: the original image, for a Before / After switch; label describes it
//   label       describes the shown image (with before: the result)
//   maxHeight   the height of an inline image box in pixels (default 520)
// Controls: Fit, 100%, zoom - and +, Full screen, Open in a new tab. On a focused picture: F fit, 1 real size, + and -
// zoom, 0 back to fit, arrows scroll. Ctrl + wheel zooms at the pointer and dragging pans (images; a frame keeps its
// clicks and scrolls with its scroll bars).
const ZOOMS = [0.1, 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8];

export const viewSrc = opts => '/view/' + opts.view + (opts.themed ? '#theme=' + (document.documentElement.dataset.theme || 'light') : '');

function viewer(opts, dialog) {
  const isFrame = !!opts.view;
  let natW = opts.width || 1200, natH = opts.height || 800, fit = true, scale = 1, tall = false, reported = 0, before = false;
  const content = isFrame
    ? h('iframe', { src: viewSrc(opts), title: opts.title, sandbox: 'allow-scripts', loading: 'lazy', 'data-themed': opts.themed ? opts.view : null })
    : h('img', { src: opts.src, alt: opts.title, draggable: 'false' });
  const box = h('div', { class: 'viewer-box' }, content);
  const stage = h('div', { class: 'viewer-stage ' + (isFrame ? 'frame' : 'image'), tabindex: '0', role: 'group',
    'aria-label': opts.title + '. Keys: F fit, 1 real size, plus and minus zoom.' }, box);
  if (!isFrame && !dialog) stage.style.maxHeight = (opts.maxHeight || 520) + 'px';
  const zoomText = h('span', { class: 'mono small zoom-text' });
  const label = h('span', { class: 'small muted grow' }, opts.label || '');
  const button = (text, aria, onclick) => h('button', { class: 'button small', type: 'button', 'aria-label': aria + ': ' + opts.title, onclick }, text);
  const fitButton = button('Fit', 'Fit to the box', () => { fit = true; layout(); });
  const realButton = button('100%', 'Real size, 100%', () => zoomTo(1));
  const fitScale = () => {
    const width = stage.clientWidth || natW;
    if (isFrame) return Math.min(1, width / natW);
    const height = dialog ? stage.clientHeight : (opts.maxHeight || 520);
    return Math.min(width / natW, (height - 4) / natH, 8); // 4: the border, so fit never scrolls
  };
  function layout() {
    const s = fit ? fitScale() : scale;
    const height = tall && reported ? reported : natH;
    if (isFrame) Object.assign(content.style, { width: natW + 'px', height: height + 'px', transform: `scale(${s})` });
    else { Object.assign(content.style, { width: natW * s + 'px', height: natH * s + 'px' }); content.classList.toggle('pixels', s >= 2); }
    box.style.width = Math.round(natW * s) + 'px';
    box.style.height = Math.round(height * s) + 'px';
    zoomText.textContent = Math.round(s * 100) + '%';
    fitButton.setAttribute('aria-pressed', String(fit));
    realButton.setAttribute('aria-pressed', String(!fit && scale === 1));
  }
  // Keeps the point under the pointer (or the middle of the box) in place.
  function zoomTo(next, x = stage.clientWidth / 2, y = stage.clientHeight / 2) {
    const old = fit ? fitScale() : scale;
    scale = Math.min(8, Math.max(Math.min(old, 0.1), next)); fit = false; // a fit below 10% stays the floor
    const k = scale / old, left = (stage.scrollLeft + x) * k - x, top = (stage.scrollTop + y) * k - y;
    layout();
    stage.scrollLeft = left; stage.scrollTop = top;
  }
  const step = dir => {
    const now = fit ? fitScale() : scale;
    return dir > 0 ? (ZOOMS.find(z => z > now + 1e-6) || 8) : ([...ZOOMS].reverse().find(z => z < now - 1e-6) || Math.min(now, 0.1));
  };
  stage.addEventListener('keydown', e => {
    if (e.ctrlKey || e.altKey || e.metaKey) return;
    if (e.key === 'f' || e.key === 'F' || e.key === '0') { fit = true; layout(); }
    else if (e.key === '1') zoomTo(1);
    else if (e.key === '+' || e.key === '=') zoomTo(step(1));
    else if (e.key === '-') zoomTo(step(-1));
    else return;
    e.preventDefault();
  });
  stage.addEventListener('wheel', e => {
    if (!e.ctrlKey) return;
    e.preventDefault();
    const r = stage.getBoundingClientRect();
    zoomTo(step(e.deltaY < 0 ? 1 : -1), e.clientX - r.left, e.clientY - r.top);
  }, { passive: false });
  if (!isFrame) {
    content.addEventListener('load', () => { natW = content.naturalWidth || 1; natH = content.naturalHeight || 1; layout(); });
    let drag = null;
    stage.addEventListener('pointerdown', e => {
      if (e.button !== 0) return;
      drag = { x: e.clientX, y: e.clientY, left: stage.scrollLeft, top: stage.scrollTop };
      stage.setPointerCapture(e.pointerId); stage.classList.add('dragging');
    });
    stage.addEventListener('pointermove', e => { if (drag) { stage.scrollLeft = drag.left - (e.clientX - drag.x); stage.scrollTop = drag.top - (e.clientY - drag.y); } });
    const end = () => { drag = null; stage.classList.remove('dragging'); };
    stage.addEventListener('pointerup', end); stage.addEventListener('pointercancel', end);
  // While Full height is on, the frame is taller, so the page reports the new frame size: keep the height measured
  // at the natural size, or each report would grow the frame again.
  } else content.omlHeight = px => { if (tall) return; reported = Math.min(20000, Math.max(100, Math.round(px))); content.dataset.height = reported; };
  const tallButton = isFrame && opts.fullHeight ? button('Full height', 'Show the whole page height', () => {
    tall = !tall; tallButton.setAttribute('aria-pressed', String(tall)); layout();
  }) : null;
  if (tallButton) tallButton.setAttribute('aria-pressed', 'false');
  const sides = opts.before ? h('div', { class: 'segmented', role: 'group', 'aria-label': 'Before or after' }, [['Before', true], ['After', false]].map(([text, value]) => {
    const b = h('button', { type: 'button', 'aria-pressed': String(value === before) }, text);
    b.addEventListener('click', () => {
      before = value; sides.querySelectorAll('button').forEach(x => x.setAttribute('aria-pressed', String(x === b)));
      content.src = before ? opts.before.src : opts.src; content.alt = before ? 'Before: ' + opts.title : opts.title;
      label.textContent = before ? opts.before.label || '' : opts.label || '';
    });
    return b;
  })) : null;
  const tab = h('a', { class: 'button small', href: isFrame ? viewSrc(opts) : opts.src, target: '_blank', rel: 'noopener', 'aria-label': 'Open in a new tab: ' + opts.title }, 'New tab');
  tab.addEventListener('click', () => { tab.href = isFrame ? viewSrc(opts) : (before ? opts.before.src : opts.src); }); // the theme may have changed
  const bar = h('div', { class: 'viewer-bar' }, sides, label, fitButton, realButton,
    button('−', 'Zoom out', () => zoomTo(step(-1))), zoomText, button('+', 'Zoom in', () => zoomTo(step(1))), tallButton,
    dialog ? button('Close', 'Close the full-screen view', () => dialog.close()) : button('Full screen', 'Full screen', () => viewer.open(opts)), tab);
  requestAnimationFrame(layout);
  if (window.ResizeObserver) {
    let shown = false;
    const watch = new ResizeObserver(() => {
      if (!stage.isConnected) { if (shown) watch.disconnect(); return; }
      shown = true;
      if (fit) layout();
    });
    watch.observe(stage);
    onLeave(() => watch.disconnect());
  }
  return h('div', { class: 'viewer' }, bar, stage);
}

viewer.open = opts => {
  const dialog = h('dialog', { class: 'viewer-dialog', 'aria-label': opts.title });
  add(dialog, viewer(opts, dialog));
  document.body.append(dialog);
  dialog.addEventListener('close', () => dialog.remove());
  dialog.showModal();
  dialog.querySelector('.viewer-stage').focus();
  onLeave(() => { if (dialog.open) dialog.close(); });
};

// A sandboxed /view/ page has no origin; a preview reports its height with postMessage (only a number is taken).
window.addEventListener('message', e => {
  const height = e.data && typeof e.data.omlHeight === 'number' ? e.data.omlHeight : null;
  if (height === null) return;
  for (const f of document.querySelectorAll('.viewer iframe')) if (f.contentWindow === e.source && f.omlHeight) f.omlHeight(height);
});

export { viewer };
