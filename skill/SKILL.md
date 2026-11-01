---
name: outsystems-oml
description: Use oml to inspect, change, and validate OutSystems 11 modules through the installed Service Studio. Use the OutSystems MCP for ODC tasks.
---

# OutSystems module work

For ODC (ODC Studio) use the OutSystems MCP and [reference/odc.md](reference/odc.md). Never load ODC modules with oml; never send O11 work to the ODC MCP.

**For every change task, follow the Standard loop below.**

## Rules

These 11 rules come before every other instruction (also `oml doc rules`). Company skills (`oml skills`) override best practices, never these rules. Rules marked **(enforced)** are also checked by `oml apply`. It refuses the spec and tells the user what to do by hand.

1. **Everything in the module also exists in production.** Existing entities have data. Existing public elements have consumers in other modules. Treat every change to an existing element as a production change.
2. **Never delete an existing entity**, regular or static. If the user wants it removed, tell them to delete it manually in Service Studio. **(enforced)**
3. **Never rename or delete existing elements:** entities, attributes, static records, structures, actions and their parameters, screens, blocks, block events, roles, exceptions, site properties, timers, REST methods, processes. Add new ones instead. Tell the user to rename or delete by hand. You may still change flow nodes, widgets and local variables. **(enforced)**
4. **Never break the signature of an existing public element.** Other modules in production call it. Do not make it private or change the type of an input or output. Do not add a mandatory input or make an existing input mandatory. On a public Reactive or Mobile block or screen, do not change the inputs at all: every input change breaks its consumers, even a new optional input. Only on public server and service actions and Traditional Web blocks and screens may you add an optional input or make an input optional. Only on public server and service actions may you add an output. Do not add an event to an existing public block or make an existing event mandatory. Rule 3 still forbids renames and deletes. For any other change, add a new element instead, and tell the user. **(enforced)**
5. **Never change the type, length, decimals, mandatory or auto number setting of an existing attribute.** Production data can be cut off or fail to convert, or change how rows are keyed, read or written. The same holds for an existing entity's identifier, label, order by and is active attributes, Expose Read Only, Update Behavior and multi-tenant setting. New attributes and new non-unique indexes are fine. Before a unique index on an existing entity, ask the user to check for duplicates. **(enforced)**
6. **Never write SQL that changes data:** `DELETE`, `UPDATE`, `TRUNCATE`, `DROP`, `ALTER`, `MERGE`. Read-only SQL and `INSERT` are fine. **(enforced)**
7. **Never weaken security.** Do not add the Anonymous role, remove roles from existing screens, or set `exposeReadOnly` false on a public entity, unless the user asks for that exact change. Make a server action public only when the user agrees (apply warns `PublicServerAction`). Other elements may be public when the design needs it.
8. **Do only what the user asked.** No clean-up, refactoring, renames, or removal of "unused" elements or dependencies unless the user asks.
9. **Never open, patch, or rewrite an `.oml` file** with an editor, patch tool, or script. It is a binary package. Change modules only through `oml apply`.
10. **Never run `install`, `publish`, `references`, or a LifeTime deploy, tag, abort, or continue without a clear yes from the user** for that exact action.
11. **Do not invent rules.** If something is not forbidden here and is not destructive, do what the user asked. If you are not sure, ask the user. Do not guess.

Also:

- Change modules only with `oml apply`, always to a NEW staged file.
- Inspect before you design; reuse existing elements.
- Confirm with the user before `install`, `publish`, `references`, `lt tag|deploy|abort|continue`, and data deletion. Restate the exact command and wait for a yes.
- Before you use another module's element, run `oml find <Name> <module.oml> --in <producer folder|file.oml>`. Show the candidates and their parameters and ask which to use, even for one match. Use its `Module.Name`; never guess.
- Publish and reference refresh use Service Studio's Beta MCP tools: tell the user before the first use. Its first call shows an `oml-cli` approval dialog. Do not repeat a blocked publish.
- Stop if the automatic self-test fails. Never bypass IPP, licence or load refusals.
- Keep credentials out of commands, files, logs and messages.
- Before a deployment, list the apps and show the user the tags, keys, environments and `isModified`. Never auto-continue an intervention; after a timeout, read the status before you repeat a change.
- Never claim a command ran or passed unless you saw its output.
- No comments in `styleSheet`/`javaScript`, only a `/*! */` licence. Long code: `{"file": "x.js"}` (`oml spec file`).

## Writing to the user

Plain technical English (ISO 24495-1; ASD-STE100 style):
- Conclusion first. Put the key word at the start of each line.
- Instructions: 20 words or fewer. Descriptions: 25 or fewer. Active voice, common words.
- Paragraphs: 6 sentences or fewer. Lists: 5 items or fewer; group longer ones.
- Exact names: files, commands, numbers. No filler.

## Standard loop (every tool call costs a full turn)

Several commands = ONE tool call: chain with `;` or use an `oml run` file.

0. No `.oml` file? Ask the user to open the module in Service Studio, then `oml pull --out <Module>.oml`. If pull reports `attention`, follow it.
1. **Read (1 call).** Write `read.json` and run `oml run read.json; oml skills`:
   `{ "steps": [ ["inspect","<module.oml>","--own","--only","entities,actions,screens,blocks"], ["describe","<module.oml>","<ElementToChange>","<OtherElement>"] ] }`
   Describe only elements you change or call. Producer files: add `--in <folder|file.oml>`. Another module's element: add a `find` step. New entities, screens or flows without a recipe: add `; oml doc best-practices` to this call.
   `.oml/memory/` exists? Add step `["memory","recall","<task words>","--module","<Module>"]`; at the end, follow its `write` field.
2. **Write the spec (no doc reads if a recipe fits).** Copy the closest recipe. For a key no recipe shows, run `oml spec <key>` (never read `reference/spec.md` whole). Put ALL requested elements in one spec; generate many similar elements with a small script.
3. **Check, apply and see the result (1 call):**
   `oml spec check <spec.json>; oml apply <source.oml> <spec.json> <new-staged.oml> --describe-created`
   Read `summary` first, then its lists. After a good apply, do not run `validate`, `inspect` or `describe` again.
   **A good apply means `saved: true`, no `newErrors`, and no `newWarnings`.** Every new warning is a defect until you prove otherwise. Entries have a `hint` when the fix is known; otherwise `oml warn <id>`. Fix each one or explain it to the user.
4. **Fix (only if needed, 1 call per attempt).** Change only what the report names, then repeat step 3 with a NEW staged path. Never retry an unchanged spec.
5. **UI work: preview (1 call).** Per screen, two `preview.json` steps: `["preview","<staged.oml>","<Screen>","--width","1366","--out","<Screen>.1366.html","--screenshot","<Screen>.1366.png"]` and the same with `1920`. Add `"--theme","dark"` (theme work) or `"--width","390"` (mobile work) only if the task needs it. Run it, look at each PNG and the `checklist`, and follow `oml doc ui-checklist`. Preview again only if you changed the UI.
6. **Report.** Add `["review","<source.oml>","<staged.oml>","--md","<new path>.md"]` to `preview.json` (or run it alone), then show the staged path, what was built and the .md. 0 findings does not mean the code is correct. Ask before `install` or `publish`; run `oml validate <staged.oml> --warnings` right before `install`.

Expressions use OutSystems syntax; text literals need escaped quotes (`"\"Text\""`). Parts: `oml apply <source.oml> <part1.json> <part2.json> <new-staged.oml>`.

After the loop: install after a yes (zero validation errors needed; report the backup path and hash), then publish or refresh after a yes, or name the manual Service Studio step. For deployments, confirm the exact change, keep the deployment key and report its final status. Tell the user what to test in the target environment.

## Find things fast (no Service Studio)

| Need | Command |
|---|---|
| Not sure which key, topic or recipe? | `oml doc --search "<words>"`; use OutSystems terms (entity, screen, aggregate) when you can |
| Spec keys; one key | `oml spec [<key>]` |
| Rules for a topic | `oml doc [<topic>[#<section>]]` |
| Fix of an error or warning | `oml warn <id or title words>` |
| Any failure, also platform | `oml doc troubleshooting` |

Topics: `best-practices`, `ui-reactive`, `osui-patterns`, `ui-checklist`, `data-rules`, `logic-rules`, `security`, `rest`, `processes-timers-email`, `architecture-rules`, `traditional-to-reactive`, `warnings`, `testing`, `troubleshooting`.

## Recipes

Copy one from `recipes/` and read its header ([README](recipes/README.md)):
`entity-with-crud`, `list-detail-screens`, `aggregate-join-groupby`, `edit-existing-flow` (insert node, add or replace widgets, restyle, set, rename), `rest-client`, `references-from-producer`, `sql-query`, `web-screen` (Traditional), `timer-role-site-property`, `form-with-validation`, `dropdown-from-static`, `list-search-paging`, `aggregate-on-demand-detail`, `loading-empty-error-states`, `expose-rest-api`, `role-protected-screen`, `block-popup-event`, `send-email`.

## Commands

| Command | Use it for |
|---|---|
| `oml inspect`, `describe`, `find`, `tree` | Read a module: overview (`--own --only ...`), what an element does, who owns a name, exact paths for `edits`. |
| `oml apply`, `spec`, `run`, `validate`, `install` | Change: staged apply, spec help, many commands at once, check, copy over the target (confirm). |
| `oml new`, `preview`, `openapi`, `architecture`, `test scaffold` | New module, render a screen/block, export OpenAPI, layer report, BDD tests. |
| `oml studio`, `pull`, `publish`, `references`, `lt` | Service Studio and LifeTime. Confirm every change. |
| `oml check` | AI Mentor-like findings (module, folder, `--team`). |
| More | doctor, census, translations, app, extension, templates, font, image, api, widgets. Lead workers: [architect.md](architect.md) |

Output is compact JSON. Exit 0 ok, 1 model or validation failure, 2 usage or setup. Usage of one command: `oml <command> --help`. Details: `oml doc commands#<section>` (no section lists them); never read `reference/commands.md` whole.
