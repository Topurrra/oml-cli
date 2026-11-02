import { h, add } from './dom.js';
import { cli, copyButton, downloadLink, severityBadge } from './ui.js';

// The result card of oml review: risks, changes and new findings. The Review page and the Dif page's review fence share it.
export function reviewResult(data, options = {}) {
  const r = data.output || {};
  const s = r.summary || {};
  const table = (heads, rows) => h('div', { class: 'table-wrap' }, h('table', {}, h('thead', {}, h('tr', {}, heads.map(t => h('th', {}, t)))), h('tbody', {}, rows)));
  const title = options.title || r.module || 'Review';
  return h('section', { class: 'card', 'aria-label': options.label || 'Review results' },
    h('div', { class: 'card-head' }, h(options.heading || 'h2', {}, title),
      h('div', { class: 'row' }, h('span', { class: 'badge ' + (s.risks ? 'fail' : 'ok') }, `${s.risks || 0} risks`), s.info ? h('span', { class: 'badge' }, `${s.info} to look at`) : null,
        h('span', { class: 'badge ' + (s.newFindings ? 'warn' : 'ok') }, `${s.newFindings || 0} new findings`),
        h('span', { class: 'badge' }, `${s.added || 0} added, ${s.changed || 0} changed, ${s.removed || 0} removed, ${s.renamed || 0} renamed, ${s.recreated || 0} recreated`))),
    options.compact ? null : h('div', { class: 'row' }, data.markdown ? copyButton(data.markdown, 'Copy Markdown', 'Markdown') : null, data.notes ? copyButton(data.notes, 'Copy release notes', 'Release notes') : null,
      data.markdownDownload ? downloadLink(data.markdownDownload, 'Download Markdown', 'review.md') : null,
      data.download ? downloadLink(data.download, 'Download HTML', 'review.html') : null),
    h('h3', {}, `Risks (${(r.risks || []).length})`),
    (r.risks || []).length ? table(['Rule', 'Element', 'What'], r.risks.map(x => h('tr', {},
      h('td', {}, h('span', { class: 'badge ' + (x.info ? '' : 'fail') }, x.info ? 'Look' : 'Rule ' + x.rule), h('div', { class: 'tiny muted' }, x.ruleName)),
      h('td', { class: 'mono small' }, x.element), h('td', {}, x.text)))) : h('p', { class: 'muted' }, 'No change breaks a production rule.'),
    h('h3', {}, `Changes (${(r.changes || []).length})`),
    (r.changes || []).length ? table(['Area', 'Element', 'Change'], r.changes.map(c => h('tr', {},
      h('td', {}, h('span', { class: 'badge' }, c.area)), h('td', {}, h('strong', {}, c.name), h('div', { class: 'tiny muted' }, c.kind)),
      h('td', {}, h('span', { class: 'badge ' + ({ added: 'ok', removed: 'fail', recreated: 'fail' }[c.change] || 'warn') }, c.change), (c.details || []).length ? h('ul', { class: 'checklist' }, c.details.map(d => h('li', {}, d)), c.moreDetails ? h('li', { class: 'muted' }, `${c.moreDetails} more in the Markdown`) : null) : null)))) : h('p', { class: 'muted' }, 'No change found.'),
    h('h3', {}, `New findings (${(r.newFindings || []).length})`),
    (r.newFindings || []).length ? table(['Severity', 'Finding', 'Element'], r.newFindings.map(f => h('tr', {}, h('td', {}, severityBadge(f.severity)),
      h('td', {}, h('strong', {}, f.title), h('div', { class: 'small' }, f.fix)), h('td', { class: 'mono small' }, f.element || '(module)')))) : h('p', { class: 'muted' }, 'No new check finding.'),
    r.truncated ? h('p', { class: 'hint' }, 'The lists here are cut. The Markdown and the report page have everything.') : null,
    h('p', { class: 'hint' }, r.note || 'Logic bugs are not covered; 0 findings does not mean the code is correct.'),
    !options.compact && data.folder ? h('p', { class: 'hint' }, 'Markdown, release notes and the page are saved in ', h('code', { text: data.folder })) : null,
    cli(data.cli));
}
