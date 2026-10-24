import { h, add } from './dom.js';

function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]); }

// Code spans are set aside first, so **bold text with `code` inside** still works.
function inline(text) {
  const codes = [];
  let s = esc(String(text).replace(/`([^`]+)`/g, (m, c) => { codes.push(c); return `\u0000${codes.length - 1}\u0000`; }));
  s = s.replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>');
  s = s.replace(/(^|[\s(])\*([^*\s][^*]*?)\*(?=[\s).,:;]|$)/g, '$1<em>$2</em>');
  s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, label, href) =>
    /^https?:\/\//.test(href.replace(/&amp;/g, '&')) ? `<a href="${href}" target="_blank" rel="noopener noreferrer">${label}</a>` : label);
  return s.replace(/\u0000(\d+)\u0000/g, (m, n) => '<code>' + esc(codes[+n]) + '</code>');
}

export function markdown(text) {
  const lines = String(text).replace(/\r/g, '').split('\n');
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (/^```/.test(line)) {
      const code = []; i++;
      while (i < lines.length && !/^```/.test(lines[i])) code.push(lines[i++]);
      i++; out.push('<pre><code>' + esc(code.join('\n')) + '</code></pre>'); continue;
    }
    const head = /^(#{1,4})\s+(.*)$/.exec(line);
    if (head) { const n = Math.min(head[1].length + 1, 4); out.push(`<h${n}>${inline(head[2])}</h${n}>`); i++; continue; }
    if (/^\s*\|/.test(line)) {
      const rows = [];
      while (i < lines.length && /^\s*\|/.test(lines[i])) rows.push(lines[i++]);
      const cells = r => r.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim());
      const body = rows.filter((r, n) => !(n === 1 && /^[\s|:-]+$/.test(r)));
      out.push('<div class="table-wrap"><table><thead><tr>' + cells(body[0]).map(c => '<th>' + inline(c) + '</th>').join('') + '</tr></thead><tbody>' +
        body.slice(1).map(r => '<tr>' + cells(r).map(c => '<td>' + inline(c) + '</td>').join('') + '</tr>').join('') + '</tbody></table></div>');
      continue;
    }
    const item = /^(\s*)([-*]|\d+\.)\s+(.*)$/.exec(line);
    if (item) {
      const ordered = /\d/.test(item[2]);
      const items = [];
      while (i < lines.length) {
        const m = /^(\s*)([-*]|\d+\.)\s+(.*)$/.exec(lines[i]);
        if (m) { items.push(m[3]); i++; continue; }
        if (/^\s{2,}\S/.test(lines[i]) && items.length) { items[items.length - 1] += ' ' + lines[i].trim(); i++; continue; }
        break;
      }
      out.push((ordered ? '<ol>' : '<ul>') + items.map(t => '<li>' + inline(t) + '</li>').join('') + (ordered ? '</ol>' : '</ul>'));
      continue;
    }
    if (!line.trim()) { i++; continue; }
    const para = [];
    while (i < lines.length && lines[i].trim() && !/^(#{1,4}\s|```|\s*\||\s*([-*]|\d+\.)\s)/.test(lines[i])) para.push(lines[i++].trim());
    out.push('<p>' + inline(para.join(' ')) + '</p>');
  }
  const div = h('div', { class: 'doc' });
  div.innerHTML = out.join('\n');
  return div;
}

// Light colouring for describe text and JSON(C): keywords, strings, comments, numbers.
export function colorCode(text, kind) {
  const pre = h('pre', { class: 'code-view' });
  const code = h('code');
  const pattern = kind === 'json'
    ? /(\/\/[^\n]*|\/\*[\s\S]*?\*\/)|("(?:[^"\\\n]|\\.)*")(\s*:)?|\b(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|true|false|null)\b/g
    : /(^\s*(?:Block|Screen|ScreenAction|ServerAction|ClientAction|ServiceAction|Entity|Structure|locals|inputs|outputs|if|else|for each|end|set|call|javascript|aggregate|sql|navigate|raise|exception|OnRender|OnReady|OnInitialize|Container|Expression|Link|Button|Text|Input|List|If)\b)|("(?:[^"\\\n]|\\.)*")|\b(\d+(?:\.\d+)?|True|False|NullIdentifier\(\))\b/gm;
  let last = 0, m;
  while ((m = pattern.exec(text))) {
    if (m.index > last) add(code, text.slice(last, m.index));
    if (kind === 'json') {
      if (m[1]) add(code, h('span', { class: 'c', text: m[1] }));
      else if (m[2]) { add(code, h('span', { class: m[3] ? 'k' : 's', text: m[2] })); if (m[3]) add(code, m[3]); }
      else add(code, h('span', { class: 'n', text: m[4] }));
    } else {
      if (m[1]) add(code, h('span', { class: 'k', text: m[1] }));
      else if (m[2]) add(code, h('span', { class: 's', text: m[2] }));
      else add(code, h('span', { class: 'n', text: m[3] }));
    }
    last = pattern.lastIndex;
    if (m[0].length === 0) pattern.lastIndex++;
  }
  add(code, text.slice(last));
  add(pre, code);
  return pre;
}
