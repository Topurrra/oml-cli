# oml dashboard web front end

Plain JavaScript, ES modules, no build step, no CDN, no dependency. Served by `oml dashboard`
from embedded resources (`src/Dashboard/Dashboard.cs`, `src/OmlCli.csproj`); the server refuses anything
deeper than one folder level. CSP is `script-src 'self'`: no inline scripts, no eval.

## Structure

- `main.js`: the shell: starts the router, the sidebar (from `nav.js`) and the global controls.
- `nav.js`: the sidebar: ordered groups and page ids. The only place to change it; remove an id to hide a page.
- `core/`: shared machinery, no page logic: `dom.js` (h/add/icon), `api.js`, `store.js`, `state.js`,
  `router.js`, `workset.js`, `ui.js` (shared widgets), `viewer.js`, `markdown.js`, `lifecycle.js`,
  `chart.js` (the score chart and sparklines, SVG built with the DOM; colours from CSS classes).
- `pages/<id>.js`: one file per page, each exports `{ id, group, title, description, icon, render(main, ctx) }`.
  Pages are registered in `pages/index.js`. The sidebar lists `agents` ("Work with agents"), which shows the
  `prompt`, `recipes` and `dif` pages as sections; those three stay registered and reachable by URL (`#/prompt`,
  `#/recipes`, `#/dif`, also with `?selftest=1`). The docs, cookbook and rules pages are removed.
- `data/`: fixed lists: `cookbook.js` (must cover every `oml --help` command), `prompts.js` (the two agent
  prompt templates). The prompt builder and the recipes page share `prompts.js`.
- `tools.js` / `tools-more.js`: the Developer tools (browser only); `tools-ui.js` has their small form helpers.

## Rules

- No new dependency, no framework, no inline scripts. Everything a page needs comes through imports.
- Keep each file under 400 lines; most pages under 250. Plain technical English in labels and hints.
- `tests/dashboard-check.ps1` verifies the sidebar matches `nav.js`, every web file is served, traversal is
  refused, and the prompt self-test runs in the page.
