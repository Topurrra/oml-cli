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

## Small-budget workflow

1. Read only what you need: `oml inspect <module.oml> --own --only entities,actions,screens`. Choose relevant categories: entities, structures, actions, screens, blocks, references, timers, roles, processes, rest, static, siteProperties.
2. Understand existing logic with `oml describe <module.oml> <Element> --depth 2`. Add `--in <folder|file.oml>` for producer files. Prefer `describe` to `tree`.
3. Open the closest recipe in `skill/recipes/`. Copy it to a change spec. Read its base-module notes. Rename names, types, paths, and expressions together. Preserve unrelated elements.
4. Do not write comments in `styleSheet` or `javaScript` values: they cost tokens and `apply` removes them. Use `/*! ... */` only for a required license notice.
5. Run `oml apply <source.oml> <spec.jsonc> <new-staged.oml>`. JSONC comments are allowed; trailing commas are not. Expressions use OutSystems syntax. Text expressions need escaped quotes.
6. On failure, fix only what `problems` or `newErrors` reports, including `reopenNewErrors`. Read new warnings too. Use another new staged path.
7. Run `oml describe <new-staged.oml> <Element>` to check logic, arguments, and access. Run `oml validate <new-staged.oml> --warnings`. Model validation does not run SQL, HTTP, timers, or browser behavior; test these in the target environment.
8. Show the result and staged path. Ask before `install` or `publish`.

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

Read `skill/reference/spec.md` only for keys that no recipe shows. Never read `skill/reference/model-api.md` unless you change the CLI itself.

## Output

Commands print compact JSON by default. Use `--pretty` only for humans.
For packages, extensions, fonts, images, LifeTime and publishing, see `skill/SKILL.md`, Commands.
Exit codes: `0` success; `1` model/validation failure or attention needed; `2` usage/environment failure.
An apply can return `saved: true` with exit `1` after reopen errors. Check both JSON and exit code.
