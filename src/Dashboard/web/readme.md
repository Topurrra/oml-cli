# oml dashboard web front end

Plain JavaScript, ES modules, no build step, no CDN, no dependency. Served by `oml dashboard`
from embedded resources (`src/Dashboard/Dashboard.cs`, `src/OmlCli.csproj`); the server refuses anything
deeper than one folder level. CSP is `script-src 'self'`: no inline scripts, no eval. A Debug build of oml
serves these files from this folder instead, so a change shows on reload (`oml-dashboard-dev` in `.claude/launch.json`).

The dashboard is for looking; it changes very little. The only actions that make something are the Developer
tools (fonts and images), Architecture, and Config (`oml config set`, one project setting at a time); Get modules
copies or downloads modules into the work folder.

## Structure

- `main.js`: the shell: starts the router, the sidebar (from `nav.js`) and the global controls.
- `nav.js`: the sidebar: ordered groups and page ids. The only place to change it; remove an id to hide a page.
- `core/`: shared machinery, no page logic: `dom.js` (h/add/icon), `api.js`, `store.js`, `state.js`,
  `router.js` (also sends old addresses of removed pages to where the thing is now), `workset.js`, `ui.js`,
  `viewer.js`, `markdown.js`, `lifecycle.js`, `chart.js`, and:
  - `table.js`: the widgets every page uses: `dataTable` (search, filters, click-to-sort, pages, fixed column
    widths, a detail row per row), `section` (a card that folds and remembers it), `tabs`, `iconButton` and
    `actionButton`. A new table or card uses these, so every page looks and works the same.
  - `server-modules.js`: the modules and versions on the platform server, shared by Platform server and Get modules.
- `pages/<id>.js`: one file per page, each exports `{ id, group, title, description, icon, render(main, ctx) }`.
  Pages are registered in `pages/index.js`. Work with agents (`agents`) shows the `prompt`, `recipes` and `dif`
  pages as tabs; those three stay registered and reachable by URL (`#/prompt`, `#/recipes`, `#/dif`, also with
  `?selftest=1`). The Check, Review and OpenAPI pages are gone: those are CLI commands.
- `data/`: fixed lists: `cookbook.js` (command examples; only its setup-check names are shown now),
  `prompts.js` (the two agent prompt templates).
- Developer tools: `tools-assets.js` (Images and Fonts to WOFF2: they upload to the server), `tools.js` and
  `tools-more.js` (text tools, browser only), `tools-ui.js` (their small form helpers and the size limit),
  `regex-worker.js` (the Regex tester runs here and is stopped after 1.5 s).

## Rules

- No new dependency, no framework, no inline scripts. Everything a page needs comes through imports.
- Keep each file under 400 lines; most pages under 250. Plain technical English in labels and hints.
- Buttons are icons when the icon is clear (refresh, search, open, download, copy, delete); the label goes in
  `aria-label` and `title`. Form actions with no clear icon keep a word.
- A tool that takes text limits its size (`tooLong`); a drop checks types and sizes (`readFiles`), and the server
  checks the content of fonts and images again.
- `tests/dashboard-check.ps1` verifies the sidebar matches `nav.js`, every web file is served, traversal is
  refused, the removed endpoints answer 404, and the self-tests run in the page (prompt, dif, and on Developer
  tools: every tool opens and the shared table pages, searches, filters and sorts).
