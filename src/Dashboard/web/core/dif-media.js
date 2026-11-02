import { h, add } from './dom.js';
import { KEY } from './api.js';

// Images, video and galleries in a Dif walkthrough. The files are served by GET /dif-media (DashboardDif.cs): only
// files below the Markdown file's folder, with a media extension. Every element comes from h(), so text from the
// walkthrough is never parsed as HTML.
//   ![Label](media/save.png)            one image (a .mp4 or .webm path is a video with controls)
//   :::gallery side-by-side [Title]     side-by-side | grid | carousel, then ![label](path) lines, then :::
const VIDEO = /\.(mp4|webm)$/i;
export const KINDS = ['side-by-side', 'grid', 'carousel'];
// Code spans first, so `![x](y)` written as code stays text.
const IMAGE = /(`[^`\n]*`)|!\[([^\]\n]*)\]\(([^)\s]+)\)/g;
const TOKEN = /\u0001(\d+)\u0001/g;

export const mediaUrl = (md, path) => '/dif-media?key=' + encodeURIComponent(KEY) + '&md=' + encodeURIComponent(md) + '&path=' + encodeURIComponent(path);

// One item: the media with its label under it. A file the server refuses or cannot find shows the reason, not a broken image.
export function figure(md, label, path) {
  const video = VIDEO.test(path);
  const url = mediaUrl(md, path);
  const box = h('figure', { class: 'dif-figure' });
  const media = video
    ? h('video', { class: 'dif-media', controls: true, preload: 'metadata', src: url, 'aria-label': label || path })
    : h('img', { class: 'dif-media', src: url, alt: label || path });
  media.addEventListener('error', async () => {
    let why = 'the dashboard could not read it';
    try { const res = await fetch(url, { cache: 'no-store' }); if (!res.ok) why = (await res.text()).trim() || ('HTTP ' + res.status); } catch { /* keep the default */ }
    media.replaceWith(h('div', { class: 'dif-missing', role: 'alert' }, (video ? 'Video' : 'Image') + ' not shown: ' + why));
    box.classList.add('missing');
  }, { once: true });
  add(box, media, label ? h('figcaption', {}, label) : null);
  return box;
}

// :::gallery <kind> [title] ... ::: -> side-by-side, grid, or carousel (one at a time, Previous/Next, "n of m").
export function gallery(md, head, body) {
  const m = /^\s*(\S*)\s*(?:\[(.*)\])?\s*$/.exec(head) || [];
  const kind = m[1] || 'side-by-side', title = (m[2] || '').trim();
  const items = [];
  for (const line of body) for (const f of line.matchAll(IMAGE)) if (!f[1]) items.push({ label: f[2].trim(), path: f[3] });
  const box = h('section', { class: 'dif-gallery ' + kind, 'aria-label': title || 'Gallery' });
  if (!KINDS.includes(kind)) return add(box, h('div', { class: 'message error', role: 'alert' }, 'Unknown gallery kind "' + kind + '". Use side-by-side, grid or carousel.'));
  if (!items.length) return add(box, h('div', { class: 'message error', role: 'alert' }, 'A gallery needs lines like ![Label](media/file.png).'));
  if (title) add(box, h('h3', { class: 'dif-gallery-title' }, title));
  const figures = items.map(i => figure(md, i.label, i.path));
  if (kind !== 'carousel') return add(box, h('div', { class: 'dif-gallery-items' }, figures));

  let at = 0;
  const count = h('span', { class: 'dif-count small muted', 'aria-live': 'polite' });
  const prev = h('button', { class: 'button small', type: 'button' }, 'Previous');
  const next = h('button', { class: 'button small', type: 'button' }, 'Next');
  const show = n => {
    at = (n + figures.length) % figures.length;
    figures.forEach((f, i) => { f.hidden = i !== at; });
    count.textContent = `${at + 1} of ${figures.length}`;
  };
  prev.addEventListener('click', () => show(at - 1));
  next.addEventListener('click', () => show(at + 1));
  prev.disabled = next.disabled = figures.length < 2;
  show(0);
  return add(box, h('div', { class: 'dif-gallery-items' }, figures), h('div', { class: 'row dif-carousel-bar' }, prev, count, next));
}

// ![label](path) inside ordinary Markdown: each image becomes a token before markdown() runs (which has no images)
// and placeMedia() swaps the tokens in the finished HTML for figures. Returns the text to render.
export function stash(text, found) {
  return text.replace(IMAGE, (all, code, label, path) => code ? all : `\u0001${found.push({ label: label.trim(), path }) - 1}\u0001`);
}

export function placeMedia(root, md, found) {
  if (!found.length) return root;
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  const nodes = [];
  for (let n = walker.nextNode(); n; n = walker.nextNode()) if (n.nodeValue.includes('\u0001')) nodes.push(n);
  for (const n of nodes) {
    const parts = [];
    let last = 0;
    for (const m of n.nodeValue.matchAll(TOKEN)) {
      const item = found[+m[1]];
      parts.push(n.nodeValue.slice(last, m.index), item ? figure(md, item.label, item.path) : '');
      last = m.index + m[0].length;
    }
    parts.push(n.nodeValue.slice(last));
    n.replaceWith(...parts.filter(p => p !== ''));
  }
  return root;
}

// For the page self-test: wait until every figure either shows its file or shows why it does not.
export async function settled(root, ms = 15000) {
  const done = f => f.classList.contains('missing') || [...f.querySelectorAll('img')].every(i => i.complete && i.naturalWidth > 0) && [...f.querySelectorAll('video')].every(v => v.readyState >= 1);
  for (const end = Date.now() + ms; Date.now() < end; await new Promise(r => setTimeout(r, 100))) if ([...root.querySelectorAll('.dif-figure')].every(done)) return true;
  return false;
}
