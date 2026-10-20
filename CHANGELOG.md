# Changelog

Keep a Changelog style: one section per release, newest first.

## 1.2.0

- The skill has one fixed "Standard loop" for agents: read in one `oml run` call, `spec check` and `apply --describe-created` in one call, one preview call per screen, a 4-8 call budget, and no unexplained new warnings. Measured on a 24-component task (Haiku 5.5, 3 runs each): 55% lower cost and 42% fewer turns than 1.1.0.
- Add 5 recipes: form with validation, dropdown from a static entity, list with search and paging, exposed REST API, role-protected screen.
- Add `tests/bench/`: a harness to re-run the oml vs OutSystems MCP benchmarks (single agent or swarm, any model), with `verify.ps1` to check results the same way every time.
- `skill/reference/spec.md` lists known Reactive model behaviour found while building the recipes.
- Use Service Studio's menu name "Edit > MCP Server" everywhere.

- Add `oml run <steps.json>` (or `-` for stdin): runs several read and change commands in one process, so Service Studio loads once and a module that was only read stays open. The output is one JSON with each step's own output, exit code and time.
- Add the opt-in usage log: `OML_LOG=1` (or a file path) appends one local JSON line per command with the command, exit code, time, output size, module file name and the first failure message. `oml log summary` shows count, failures, median time and top failures per command.
- `tests/regress.ps1` runs its checks in parallel (4 at a time, each in its own folder and process); the checks that need a quiet machine run alone after them. `src/Core/Cli.cs` is split into `Commands.*.cs` files.
- Add `apply --describe A,B,...` and `--describe-created`: the report holds `describe` output for the named or created elements, so one call builds and shows the result. Unknown names go to `describeErrors`.
- `describe` takes several element names in one call and returns an array (one name keeps the single-object output).
- `inspect --only` takes several comma-separated groups in one call; documented with an example.
- Spec errors teach: a misspelled key or node kind gets a `did you mean` hint, and a wrong value type gets a short correct example. Unknown keys at typed spec levels are now refused instead of silently ignored.
- `apply` takes several specs before the output (`oml apply src.oml a.json b.json out.oml`): merged in order, conflicts refused naming both files, report lists `specs`.
- Add `oml spec [<key>]`: prints only the reference for one spec key or flow node kind (embedded in `oml.exe`, no Service Studio needed), so agents read 15-60 lines instead of the whole `spec.md`.
- Add `oml spec check <spec.json>`: checks keys, value types, required fields, node kinds, edit operations and flow targets in milliseconds, with the path of each problem; the key list comes from the `Spec` records, so it follows `apply`.
- Add `oml spec schema [--out spec.schema.json]`: JSON Schema (draft 2020-12) for spec files, for autocomplete and live errors in VS Code.
- Add `oml pull`: save the module open in Service Studio (11.55.93+) to an `.oml` file, without changing the open module.
- Add `oml doctor`: checks Service Studio, .NET, the model API, the MCP server, LifeTime settings, the browser and PATH, and says what to fix.

## 1.1.0

- Add `oml preview`: render one block or screen to a static HTML file, with optional headless Edge/Chrome screenshots, script error capture and six automatic UI checks.
- Add `skill/reference/ui-checklist.md`, the checklist and preview loop every agent must follow for UI work.
- Rewrite the README: shorter, with benchmarks, agent use, rules, and what agents and people each do.

## 1.0.0

- Build and change modules from JSON specs using the installed Service Studio model API.
- Read module contents and logic with `inspect`, `describe`, and `find`.
- Enforce production rules for existing elements, entities, attributes, and data-changing SQL.
- List, extract, and pack application packages; inspect and create extensions.
- Publish modules and refresh references through Service Studio.
- Use LifeTime API v2 for application tags and deployments.
- Export exposed REST APIs as OpenAPI 3.0.3 JSON.
- Report module architecture, dependencies, layers, and findings in HTML and JSON.
- Convert fonts to WOFF2 and convert, resize, and optimize images.
- Strip CSS and JavaScript comments while preserving license notices.
- Include an agent skill with rules, command references, and recipes.
