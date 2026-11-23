// DOM helpers. Every element is built with h(), so text from modules and files is never parsed as HTML.
export const ICONS = {
  home: 'M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z',
  explorer: 'M4 5h6v6H4zM14 5h6M14 9h6M4 15h6M14 15h6v4h-6zM4 19h6',
  modules: 'M4 6h16v4H4zM4 14h16v4H4zM8 8h.01M8 16h.01',
  prompt: 'M4 5h16v11H8l-4 4zM8 9h8M8 12h5',
  agents: 'M4 5h16v11H8l-4 4zM8 9h8M8 12h5',
  recipes: 'M6 3h9l3 3v15H6zM9 9h6M9 13h6M9 17h4',
  assets: 'M4 5h16v14H4zM4 15l4-4 4 4 3-3 5 5M15 9h.01',
  openapi: 'M8 4H6a2 2 0 0 0-2 2v4l-2 2 2 2v4a2 2 0 0 0 2 2h2M16 4h2a2 2 0 0 1 2 2v4l2 2-2 2v4a2 2 0 0 1-2 2h-2',
  architecture: 'M9 3h6v5H9zM3 16h6v5H3zM15 16h6v5h-6zM12 8v4M6 16v-4h12v4',
  lifetime: 'M12 3a9 9 0 1 0 9 9M12 7v5l3 3M17 3h4v4',
  server: 'M7 18h10a4 4 0 0 0 .6-7.96A6 6 0 0 0 6.1 9.6 4.2 4.2 0 0 0 7 18zM9 14h6',
  activity: 'M3 12h4l3-8 4 16 3-8h4',
  learned: 'M12 3a6 6 0 0 0-4 10.5V17h8v-3.5A6 6 0 0 0 12 3zM9 20h6',
  cookbook: 'M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3zM5 17a3 3 0 0 1 3-3h11',
  rules: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM9 12l2 2 4-4',
  tools: 'M14 6a4 4 0 0 0-5 5l-6 6 3 3 6-6a4 4 0 0 0 5-5l-2 2-3-3z',
  check: 'M4 4h16v16H4zM8 12l3 3 5-6',
  review: 'M4 5h7v14H4zM13 5h7v14h-7zM6 9h3M15 9h3M6 13h3M15 13h3',
  dif: 'M6 4h9l4 4v12H6zM10 11h5M12.5 8.5v5M10 17h5',
  docs: 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM20 20l-4.8-4.8',
  copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
  download: 'M12 4v11M7 10l5 5 5-5M5 20h14',
  upload: 'M12 20V9M7 14l5-5 5 5M5 4h14',
  run: 'M7 4l12 8-12 8z',
  refresh: 'M20 11a8 8 0 1 0-2.3 5.7M20 4v7h-7',
  search: 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM20 20l-4.8-4.8',
  open: 'M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5',
  close: 'M6 6l12 12M18 6L6 18',
  prev: 'M15 5l-7 7 7 7',
  next: 'M9 5l7 7-7 7',
  first: 'M17 5l-7 7 7 7M7 5v14',
  last: 'M7 5l7 7-7 7M17 5v14',
  plus: 'M12 5v14M5 12h14',
  folder: 'M3 6a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v10a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z',
  eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12zM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6z',
  trash: 'M4 7h16M9 7V4h6v3M6 7l1 13h10l1-13',
  sort: 'M8 4v16M4 8l4-4 4 4M16 20V4M12 16l4 4 4-4',
  chevron: 'M6 9l6 6 6-6',
  stop: 'M6 6h12v12H6z',
  fonts: 'M4 20l6-16h1l6 16M7 14h7M17 20h4',
  images: 'M4 5h16v14H4zM4 15l4-4 4 4 3-3 5 5M15 9h.01',
};

export function h(tag, attrs, ...children) {
  const el = document.createElement(tag);
  for (const [name, value] of Object.entries(attrs || {})) {
    if (value === null || value === undefined || value === false) continue;
    if (name === 'class') el.className = value;
    else if (name === 'text') el.textContent = value;
    else if (name.startsWith('on')) el.addEventListener(name.slice(2), value);
    else if (name === 'value') el.value = value;
    else if (name === 'checked') el.checked = !!value;
    else el.setAttribute(name, value === true ? '' : value);
  }
  add(el, children);
  return el;
}

// Like Element.append, but skips null, undefined and false, so `cond ? node : null` is safe.
export function add(el, ...children) {
  for (const child of children.flat(Infinity)) {
    if (child === null || child === undefined || child === false) continue;
    el.appendChild(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return el;
}

export function icon(name) {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('aria-hidden', 'true');
  const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
  path.setAttribute('d', ICONS[name] || '');
  add(svg, path);
  return svg;
}
