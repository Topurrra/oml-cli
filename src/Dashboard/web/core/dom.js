// DOM helpers. Every element is built with h(), so text from modules and files is never parsed as HTML.
export const ICONS = {
  home: 'M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6h-6v6H4a1 1 0 0 1-1-1z',
  explorer: 'M4 5h6v6H4zM14 5h6M14 9h6M4 15h6M14 15h6v4h-6zM4 19h6',
  modules: 'M4 6h16v4H4zM4 14h16v4H4zM8 8h.01M8 16h.01',
  previews: 'M3 5h18v12H3zM8 21h8M12 17v4',
  prompt: 'M4 5h16v11H8l-4 4zM8 9h8M8 12h5',
  recipes: 'M6 3h9l3 3v15H6zM9 9h6M9 13h6M9 17h4',
  assets: 'M4 5h16v14H4zM4 15l4-4 4 4 3-3 5 5M15 9h.01',
  openapi: 'M8 4H6a2 2 0 0 0-2 2v4l-2 2 2 2v4a2 2 0 0 0 2 2h2M16 4h2a2 2 0 0 1 2 2v4l2 2-2 2v4a2 2 0 0 1-2 2h-2',
  architecture: 'M9 3h6v5H9zM3 16h6v5H3zM15 16h6v5h-6zM12 8v4M6 16v-4h12v4',
  lifetime: 'M12 3a9 9 0 1 0 9 9M12 7v5l3 3M17 3h4v4',
  activity: 'M3 12h4l3-8 4 16 3-8h4',
  cookbook: 'M5 4h11a3 3 0 0 1 3 3v13H8a3 3 0 0 1-3-3zM5 17a3 3 0 0 1 3-3h11',
  rules: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6zM9 12l2 2 4-4',
  tools: 'M14 6a4 4 0 0 0-5 5l-6 6 3 3 6-6a4 4 0 0 0 5-5l-2 2-3-3z',
  check: 'M4 4h16v16H4zM8 12l3 3 5-6',
  review: 'M4 5h7v14H4zM13 5h7v14h-7zM6 9h3M15 9h3M6 13h3M15 13h3',
  docs: 'M10.5 4a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM20 20l-4.8-4.8',
  copy: 'M9 9h11v11H9zM5 15H4V4h11v1',
  download: 'M12 4v11M7 10l5 5 5-5M5 20h14',
  upload: 'M12 20V9M7 14l5-5 5 5M5 4h14',
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
