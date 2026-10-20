# oml for OutSystems 11

## Rules (full text: `skill/rules.md`; this is the full list, do not invent others)

1. Everything in the module also exists in production, with data and consumers.
2. Never delete an existing entity (regular or static). Tell the user to delete it manually.
3. Never rename or delete existing elements or their parameters/attributes. Add new ones. Flow nodes, widgets and locals may change.
4. Never change the signature of an existing public element.
5. Never change type, length, decimals or mandatory of an existing attribute.
6. Never write SQL that changes data (DELETE, UPDATE, TRUNCATE, DROP, ALTER, MERGE).
7. Never weaken security: no Anonymous role, no removed roles, no new public elements unless asked.
8. Do only what the user asked. No clean-up or refactoring.
9. Never open, patch, or rewrite an `.oml` file. It is binary. Use `oml apply`.
10. Never install, publish, refresh references or deploy without a clear yes.
11. If something is not forbidden and not destructive, do it. If unsure, ask.

`oml apply` refuses rules 2, 3, 5 and 6. Tell the user the manual step it prints; do not try to work around it.

Edit O11 `.oml` modules with `<repo>/src/bin/Release/net8.0/oml.exe`, or `oml` on PATH. `oml --help` prints the usage.

## Safety

- Keep extra files to a minimum: one change spec and one staged output. Do not leave copies of modules behind.
- Never overwrite the source. Run `apply` to a separate, new staged file for each attempt.
- Ask the user before `install`, `publish`, `references`, `lt deploy|tag|abort|continue`, or data deletion.
- Before using another module's element, run `oml find <Name> <module.oml> --in <folder|file.oml>`. Show candidates and parameters. Ask the user which to use, even for one match. Use its approved `Module.Name`.
- Read `saved`, `problems`, `newErrors`, `reopenNewErrors`, `warnings`, and `newWarnings`. Warnings can mean a wrong model. Never install a failed result.
- Do not retry an unchanged spec. Use the reported failure to change it first.
- Stop if the automatic self-test fails. Never bypass load, IPP, or license refusals.
- Keep credentials in process environment variables. Never put them in specs or logs.
- Before deployment, review tags, keys, and `isModified`. After a timeout, check status before retrying. Never auto-continue an intervention.

## Standard loop (follow it exactly; every tool call costs a full turn)

Put several commands in ONE tool call: chain them with `;` in one shell command, or list them in one `oml run` file. One call that runs 3 commands costs 1 turn; 3 calls cost 3 turns.

0. No `.oml` file? Ask the user to open the module in Service Studio, then `oml pull --out <Module>.oml`.
1. **Read (1 call).** Write `read.json` and run it:
   `{ "steps": [ ["inspect","<module.oml>","--own","--only","entities,actions,screens,blocks"], ["describe","<module.oml>","<ElementToChange>","<OtherElement>"] ] }`
   then `oml run read.json`. Only describe elements you will change or call. Producer files: add `--in <folder|file.oml>`.
   Need an element from another module? Add `["find","<Name>","<module.oml>","--in","<producer folder|file.oml>"]` to the same file, show the user the candidates, and ask which to use (even for one match). Use its `Module.Name`.
2. **Write the spec (no tool call to read docs if a recipe fits).** Copy the closest recipe from `skill/recipes/`. For a key no recipe shows, run `oml spec <key>` (one key only; never read `skill/reference/spec.md` whole). Put ALL requested elements in one spec. For many similar elements (for example 20 blocks), generate the spec with a small script, then check it, instead of applying elements one by one.
3. **Check, apply and see the result (1 call):**
   `oml spec check <spec.json>; oml apply <source.oml> <spec.json> <new-staged.oml> --describe-created`
   The apply report already contains `problems`, `newErrors`, `reopenNewErrors`, `warnings` and `newWarnings`, and the described elements. Do not run `validate`, `inspect` or `describe` again after a good apply.
   **A good apply means `saved: true`, no `newErrors`, and no `newWarnings`.** Every new warning is a real defect until you prove otherwise (for example "input parameter is never used in JavaScript" means the block ignores that input). Fix each one, or explain each remaining one to the user. Never report done with unexplained new warnings.
4. **Fix (only if needed, 1 call per attempt).** Change only what the report names (new errors and new warnings), then repeat step 3 with a NEW staged path. Never retry an unchanged spec.
5. **UI work: preview once per screen or block group (1 call):** write `preview.json` with steps `["preview","<staged.oml>","<Screen>","--theme","light","--out","<Screen>.light.html","--screenshot","<Screen>.light.png"]` and `["preview","<staged.oml>","<Screen>","--theme","dark","--width","390","--out","<Screen>.dark-390.html","--screenshot","<Screen>.dark-390.png"]` (each step needs its own `--out`), run `oml run preview.json`, look at both PNGs and the report's `checklist`, fix, and preview again only if you changed the UI. Follow `skill/reference/ui-checklist.md`.
6. **Report.** Show the staged path and what was built. Ask before `install` or `publish`; run `oml validate <staged.oml> --warnings` only right before `install`.

A normal task needs 4 to 8 tool calls. If you are past 12, stop and check whether you are repeating reads or applying one element at a time.

Do not write comments in `styleSheet` or `javaScript` values: they cost tokens and `apply` removes them. Use `/*! ... */` only for a required license notice.
JSONC comments are allowed in specs; trailing commas are not. Expressions use OutSystems syntax; text literals need escaped quotes. For work split into parts (several agents), pass every part before the output: `oml apply <source.oml> <part1.json> <part2.json> <new-staged.oml>`.

| Recipe in `skill/recipes/` | Use it for |
|---|---|
| `entity-with-crud.jsonc` | Entity, static values, checked save and delete actions. |
| `list-detail-screens.jsonc` | Reactive list/detail templates with a local layout and registered access. |
| `aggregate-join-groupby.jsonc` | Join, filter, sort, group, Sum and Count. |
| `edit-existing-flow.jsonc` | Insert a node, add a widget, set a property, rename a flow node. |
| `rest-client.jsonc` | Consumed GET/POST API and server call. |
| `references-from-producer.jsonc` | Import public elements from an approved producer export. |
| `sql-query.jsonc` | SQL parameter, entity output, For Each. |
| `web-screen.jsonc` | Traditional Web Preparation, table, button and screen action. |
| `timer-role-site-property.jsonc` | Timer action, role-only screen and site property. |
| `form-with-validation.jsonc` | Reactive form, client checks with messages, server save raising a user exception. |
| `dropdown-from-static.jsonc` | Static entity, bound Dropdown, table showing the label. |
| `list-search-paging.jsonc` | Search, sort, Previous/Next paging, empty state. |
| `expose-rest-api.jsonc` | Exposed GET (path) and POST (body) calling server actions. |
| `role-protected-screen.jsonc` | Role-only screen, server role check, link shown only to the role. |

Never read `skill/reference/model-api.md` unless you change the CLI itself.

## Output

Commands print compact JSON by default. Use `--pretty` only for humans.
For packages, extensions, fonts, images, LifeTime and publishing, see `skill/SKILL.md`, Commands.
Exit codes: `0` success; `1` model/validation failure or attention needed; `2` usage/environment failure.
An apply can return `saved: true` with exit `1` after reopen errors. Check both JSON and exit code.
