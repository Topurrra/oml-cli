---
name: outsystems-oml
description: Use oml to inspect, change, and validate OutSystems 11 modules through the installed Service Studio. Use the OutSystems MCP for ODC tasks.
---

# OutSystems module work

For ODC (ODC Studio) use the OutSystems MCP and [reference/odc.md](reference/odc.md). Never load ODC modules with oml; never send O11 work to the ODC MCP.

**For every change task, follow the Standard loop below.**

## Rules

Read [rules.md](rules.md) first: its 11 rules override all below.

- Change modules only with `oml apply`, always to a NEW staged file (rule 9).
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

## Standard loop (follow it exactly; every tool call costs a full turn)

Put several commands in ONE tool call: chain them with `;`, or list them in an `oml run` file.

0. No `.oml` file? Ask the user to open the module in Service Studio, then `oml pull --out <Module>.oml`.
1. **Read (1 call).** Write `read.json` and run `oml run read.json`:
   `{ "steps": [ ["inspect","<module.oml>","--own","--only","entities,actions,screens,blocks"], ["describe","<module.oml>","<ElementToChange>","<OtherElement>"] ] }`
   Describe only elements you change or call. Producer files: add `--in <folder|file.oml>`. Another module's element: add a `find` step. New entities, screens or flows without a recipe: add `; oml doc best-practices` to this call.
2. **Write the spec (no doc reads if a recipe fits).** Copy the closest recipe. For a key no recipe shows, run `oml spec <key>` (never read `reference/spec.md` whole). Put ALL requested elements in one spec; generate many similar elements with a small script.
3. **Check, apply and see the result (1 call):**
   `oml spec check <spec.json>; oml apply <source.oml> <spec.json> <new-staged.oml> --describe-created`
   The report has `problems`, `newErrors`, `reopenNewErrors`, `warnings`, `newWarnings` and the described elements. Do not run `validate`, `inspect` or `describe` again after a good apply.
   **A good apply means `saved: true`, no `newErrors`, and no `newWarnings`.** Every new warning is a defect until you prove otherwise (an input unused in JavaScript means the block ignores it). Entries have a `hint` when the fix is known; otherwise `oml warn <id>`. Fix each one or explain it to the user.
4. **Fix (only if needed, 1 call per attempt).** Change only what the report names, then repeat step 3 with a NEW staged path. Never retry an unchanged spec.
5. **UI work: preview once per screen or block group (1 call).** In `preview.json`, two steps per screen: `["preview","<staged.oml>","<Screen>","--theme","light","--out","<Screen>.light.html","--screenshot","<Screen>.light.png"]` and the same with `"dark"`, `"--width","390"` and its own `--out`. Run `oml run preview.json`, look at both PNGs and the `checklist`, and follow `oml doc ui-checklist`. Preview again only if you changed the UI.
6. **Report.** Add `["review","<source.oml>","<staged.oml>","--md","<new path>.md"]` to `preview.json` (or run it alone), then show the staged path, what was built and the .md. 0 findings does not mean the code is correct. Ask before `install` or `publish`; run `oml validate <staged.oml> --warnings` right before `install`.

Specs allow JSONC comments, not trailing commas. Expressions use OutSystems syntax; text literals need escaped quotes (`"\"Text\""`). Parts: `oml apply <source.oml> <part1.json> <part2.json> <new-staged.oml>`.

After the loop: install after a yes (zero validation errors needed; report the backup path and hash), then publish or refresh after a yes, or name the manual Service Studio step. For deployments, confirm the exact change, keep the deployment key and report its final status. Tell the user what to test in the target environment.

## Find things fast (no Service Studio)

| Need | Command |
|---|---|
| Not sure which key, topic or recipe? | `oml doc --search "<words>"`; use OutSystems terms (entity, screen, aggregate) when you can |
| Spec keys; one key | `oml spec [<key>]` |
| Rules for a topic | `oml doc [<topic>[#<section>]]` |
| Fix of an error or warning | `oml warn <id or title words>` |
| oml or app failures | `oml doc troubleshooting` |

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
| Others | `doctor`, `census`, `translations`, `app`, `extension`, `templates`, `font`, `image`, `api`, `widgets`, `selftest`, `log` |

Output is compact JSON (`--pretty` for humans). Exit 0 ok, 1 model or validation failure, 2 usage or setup. Full usage: [reference/commands.md](reference/commands.md).
