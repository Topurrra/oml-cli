# oml for OutSystems 11

## Rules (full text: `skill/rules.md`; this is the full list, do not invent others. Company skills from `oml skills` override best practices, never these rules.)

1. Everything in the module also exists in production, with data and consumers.
2. Never delete an existing entity (regular or static). Tell the user to delete it manually.
3. Never rename or delete existing elements or their parameters/attributes. Add new ones. Flow nodes, widgets and locals may change.
4. **Never break the signature of an existing public element.** Other modules in production call it. Do not make it private or change the type of an input or output. Do not add a mandatory input or make an existing input mandatory. On a public Reactive or Mobile block or screen, do not change the inputs at all: every input change breaks its consumers, even a new optional input. Only on public server and service actions and Traditional Web blocks and screens may you add an optional input or make an input optional. Only on public server and service actions may you add an output. Do not add an event to an existing public block or make an existing event mandatory. Rule 3 still forbids renames and deletes. For any other change, add a new element instead, and tell the user. **(enforced)**
5. Never change type, length, decimals, mandatory or auto number of an existing attribute, or an existing entity's identifier and options. New non-unique indexes are fine. Before a unique index on an existing entity, ask the user to check for duplicates.
6. Never write SQL that changes data (DELETE, UPDATE, TRUNCATE, DROP, ALTER, MERGE).
7. **Never weaken security.** Do not add the Anonymous role, remove roles from existing screens, or set `exposeReadOnly` false on a public entity, unless the user asks for that exact change. Make a server action public only when the user agrees (apply warns `PublicServerAction`). Other elements may be public when the design needs it.
8. Do only what the user asked. No clean-up or refactoring.
9. Never open, patch, or rewrite an `.oml` file. It is binary. Use `oml apply`.
10. Never install, publish, refresh references or deploy without a clear yes.
11. If something is not forbidden and not destructive, do it. If unsure, ask.

`oml apply` refuses rules 2, 3, 4, 5 and 6, and warns on rule 7 (`PublicServerAction`). Tell the user the manual step it prints; do not try to work around it.

Edit O11 `.oml` modules with `<repo>/src/bin/Release/net10.0/oml.exe`, or `oml` on PATH. `oml --help` prints the usage.

## Safety

- Keep extra files to a minimum: one change spec and one staged output. Do not leave copies of modules behind.
- Never overwrite the source. Run `apply` to a separate, new staged file for each attempt.
- Ask the user before `install`, `publish`, `references`, `lt deploy|tag|abort|continue`, or data deletion.
- Before using another module's element, run `oml find <Name> <module.oml> --in <folder|file.oml>`. Show candidates and parameters. Ask the user which to use, even for one match. Use its approved `Module.Name`.
- Read `saved`, `problems`, `newErrors`, `reopenNewErrors`, `warnings`, and `newWarnings`. Warnings can mean a wrong model. Each entry may carry a `hint`: the fix. Never install a failed result.
- Do not retry an unchanged spec. Use the reported failure to change it first.
- Stop if the automatic self-test fails. Never bypass load, IPP, or license refusals.
- Keep credentials in process environment variables. Never put them in specs or logs.
- Before deployment, show the user the tags, keys, environments, and `isModified`. After a timeout, check status before retrying. Never auto-continue an intervention.
- Never claim a command ran or passed unless you saw its output.

## Writing to the user

Plain technical English (ISO 24495-1; ASD-STE100 style):
- Conclusion first. Put the key word at the start of each line.
- Instructions: 20 words or fewer. Descriptions: 25 or fewer. Active voice, common words.
- Paragraphs: 6 sentences or fewer. Lists: 5 items or fewer; group longer ones.
- Exact names: files, commands, numbers. No filler.

## Standard loop (follow it exactly; every tool call costs a full turn)

Put several commands in ONE tool call: chain them with `;` in one shell command, or list them in one `oml run` file. One call that runs 3 commands costs 1 turn; 3 calls cost 3 turns.

0. No `.oml` file? Ask the user to open the module in Service Studio, then `oml pull --out <Module>.oml`. If pull reports `attention`, follow it.
1. **Read (1 call).** Write `read.json` and run it:
   `{ "steps": [ ["inspect","<module.oml>","--own","--only","entities,actions,screens,blocks"], ["describe","<module.oml>","<ElementToChange>","<OtherElement>"] ] }`
   then `oml run read.json; oml skills`. Only describe elements you will change or call. Producer files: add `--in <folder|file.oml>`.
   Need an element from another module? Add `["find","<Name>","<module.oml>","--in","<producer folder|file.oml>"]` to the same file, show the user the candidates, and ask which to use (even for one match). Use its `Module.Name`.
   Adding entities, screens or flows that no recipe covers? Run `oml run read.json; oml doc best-practices` in the same call and follow it.
   `.oml/memory/` exists? Add step `["memory","recall","<task words>","--module","<Module>"]`; at the end, follow its `write` field.
2. **Write the spec (no tool call to read docs if a recipe fits).** Copy the closest recipe from `skill/recipes/`. For a key no recipe shows, run `oml spec <key>` (one key only; never read `skill/reference/spec.md` whole). Put ALL requested elements in one spec. For many similar elements (for example 20 blocks), generate the spec with a small script, then check it, instead of applying elements one by one.
3. **Check, apply and see the result (1 call):**
   `oml spec check <spec.json>; oml apply <source.oml> <spec.json> <new-staged.oml> --describe-created`
   The apply report starts with `summary` (the counts), then `problems`, `newErrors`, `reopenNewErrors`, `warnings` and `newWarnings`, and the described elements. Do not run `validate`, `inspect` or `describe` again after a good apply.
   **A good apply means `saved: true`, no `newErrors`, and no `newWarnings`.** Every new warning is a real defect until you prove otherwise (for example "input parameter is never used in JavaScript" means the block ignores that input). Fix each one, or explain each remaining one to the user. Never report done with unexplained new warnings.
4. **Fix (only if needed, 1 call per attempt).** Change only what the report names (new errors and new warnings), then repeat step 3 with a NEW staged path. Never retry an unchanged spec.
5. **UI work: preview once per screen or block group (1 call):** write `preview.json` with a laptop and a desktop step per screen: `["preview","<staged.oml>","<Screen>","--width","1366","--out","<Screen>.1366.html","--screenshot","<Screen>.1366.png"]` and the same with `1920`. Add `"--theme","dark"` (theme work) or `"--width","390"` (mobile work) only when the task or user needs it; each step needs its own `--out`. Run `oml run preview.json`, look at each PNG and the report's `checklist`, fix, and preview again only if you changed the UI. Follow `skill/reference/ui-checklist.md`.
6. **Report.** Add `["review","<source.oml>","<staged.oml>","--md","<new path>.md"]` to `preview.json` (or run `oml review` alone), then show the staged path, what was built and the .md. It does not find logic bugs: 0 findings does not mean the code is correct. Ask before `install` or `publish`; run `oml validate <staged.oml> --warnings` only right before `install`.

A normal task needs 4 to 8 tool calls. If you are past 12, stop and check whether you are repeating reads or applying one element at a time.

Do not write comments in `styleSheet` or `javaScript` values: they cost tokens and `apply` removes them. Use `/*! ... */` only for a required license notice.
Put long code in a file next to the spec: `"javaScript": { "file": "code/chart.js" }` (also `styleSheet`, `sql`). To change existing UI, list the screen or block again with a new `styleSheet`, or use the `replaceWidgets` edit; never rebuild the module. `styleSheet` replaces the whole sheet: read the current one with `oml describe <module.oml> <Screen> --css` and edit it. For widget paths, `oml tree <module.oml> <Screen> --depth 2`.
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
| `aggregate-on-demand-detail.jsonc` | Master-detail: detail aggregate fetched on demand when a row is picked. |
| `loading-empty-error-states.jsonc` | List screen with failed (Retry), loading and empty states. |
| `expose-rest-api.jsonc` | Exposed GET (path) and POST (body) calling server actions. |
| `role-protected-screen.jsonc` | Role-only screen, server role check, link shown only to the role. |
| `block-popup-event.jsonc` | Reusable confirm popup block with events (`triggerEvent`), handled on a screen. |
| `send-email.jsonc` | Reactive email and a server action that sends it (`sendEmail`). |

For OutSystems rules on a topic: `oml doc <topic>`; for a warning: `oml warn <id>`. Not sure which key, topic or recipe? `oml doc --search "<words>"` lists the 3 most likely sections, each with the command that opens it; use OutSystems terms (entity, screen, aggregate) when you can.

Never read `skill/reference/model-api.md` unless you change the CLI itself.

## Output

Commands print compact JSON by default. Use `--pretty` only for humans.
For packages, extensions, fonts, images, LifeTime and publishing, see `skill/reference/commands.md`.
Exit codes: `0` success; `1` model/validation failure or attention needed; `2` usage/environment failure.
An apply can return `saved: true` with exit `1` after reopen errors. Check both JSON and exit code.
