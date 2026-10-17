---
name: outsystems-oml
description: Use oml to inspect, change, and validate OutSystems 11 modules through the installed Service Studio. Use the OutSystems MCP for ODC tasks.
---

# OutSystems module work

## 1. What this skill is and when to use it

Use this skill for O11 module inspection, data models, logic, UI, integrations, publishing, and LifeTime deployment.
Use the `oml` CLI for local O11 work. Extend its typed model code when the spec cannot express a change.
[reference/model-api.md](reference/model-api.md) covers the model API for extensions.

| Platform | Model | Read | Change and release |
|---|---|---|---|
| O11: Service Studio 11 | V12 | Local CLI inspection | Local spec; Service Studio publish; LifeTime deployment. |
| ODC: ODC Studio | V13 | OutSystems MCP context lookups | Mentor editing and publishing; MCP deployment tools. See [reference/odc.md](reference/odc.md). |

Never load ODC modules with the O11 V12 loader. Never send O11 module work to the remote ODC MCP.
Service Studio has a separate built-in MCP for O11 publishing and reference refresh.

### Terms

| Term | Meaning |
|---|---|
| module | An OutSystems application module stored in an `.oml` file. |
| spec | A JSON file that describes changes for `oml apply`. |
| source file | The input module that you want to change. |
| staged file | A separate output module that you review before installation. |
| element | A model object, such as an entity, action, screen, or widget. |
| reference | A local signature of an element from another module. |
| producer module | The module that owns a referenced element. |
| Service Studio | The installed OutSystems 11 development tool and its model API. |
| self-test | A disposable model check for the installed Service Studio build. |

## 2. Rules for agents

Read [rules.md](rules.md) first. Its 11 production rules come before everything below.

0. Do not write comments in `styleSheet` or `javaScript` values. They cost tokens every time the module is read, and `apply` removes them. Keep only a required license notice as one `/*! ... */` line.
1. Never edit OML XML. Use `OutSystems.Model.V1` and `ServiceStudio.PluginAPI` for every model change.
   Raw XML is read-only diagnostic material at most.
2. Never overwrite the source file during development. Apply to a new staged file.
   Keep the source hash and verify it after development.
3. Inspect before designing. Use `describe` to understand logic before changing it; use `tree` for exact paths. Reuse existing elements and preserve unrelated changes.
4. Confirm with the user before install, publish, reference refresh, LifeTime mutations, or data deletion.
   Restate the exact operation and wait for approval before these commands:

   ```text
   oml install <staged.oml> <target.oml>
   oml publish <Module or file.oml>
   oml references <Module or file.oml>
   oml lt tag App --version x.y.z [--description Text] [--env Development]
   oml lt deploy App[,App2] --from Env --to Env [--version x.y.z|latest] [--notes Text] [--wait]
   oml lt abort Key
   oml lt continue Key
   ```

5. Before using an element from another module, run find and show the candidates. Ask the user which element to use.
   Show its qualified name and parameters, even when the search returns one candidate.
   If none exists, ask for a producer export or a Manage Dependencies update.

   ```text
   oml find <Name> <module.oml> --in <folder of producer .oml files>
   ```

6. Use the approved qualified name, `Module.Name`, in the spec. Never guess a producer module or opaque key.
7. Read `problems`, `newErrors`, `reopenNewErrors`, `warnings`, and `newWarnings` when present.
   Warnings do not block saving. A type warning can still mean an incorrect model.
8. Validate before saving and after reopening. Verify connectors, event targets, arguments, access, and requested behavior.
9. Run a self-test for each installed build before model changes. Stop if it fails.
   The CLI runs this automatically for selected model commands on an unseen build.
10. Service Studio MCP write tools are Beta. Tell the user before the first publish or reference refresh.
    The first call shows an `oml-cli` approval dialog. Do not repeat a blocked publish without changing its approval settings.
11. Keep credentials out of commands, files, logs, and messages. Use process environment variables for LifeTime credentials.
12. Before deployment, list applications and review tags, keys, and `isModified`.
    Never auto-continue an intervention. Check status after a timeout before repeating a mutation.
13. Do not bypass IPP, licensing, protected-module checks, or load refusals. Request an authorized export or connection.
14. Run one model-edit job per process. Providers and plugin registries are global singletons.
15. Use generic typed interfaces through `Reflect` and `Refs`. Never silently swallow model or reflection errors.
16. Do not redistribute Service Studio assemblies. Reference the installed copies.

## 3. Commands

Build and locate the CLI from the repository root:

```powershell
dotnet build -c Release src/OmlCli.csproj
$env:PATH = "$PWD\src\bin\Release\net8.0;$env:PATH"
oml version
```

The executable is `src/bin/Release/net8.0/oml.exe`.
The default installation path is `C:\Program Files\OutSystems\Service Studio 11\Service Studio`.
Set `OML_SERVICE_STUDIO` for another path. Also pass this build property:

```text
-p:SS="<that folder>"
```

The CLI runs a cached self-test on a disposable module for each installed build. Stop if it fails.

| Command | Use it for |
|---|---|
| `oml new` | New module: by kind, `--template`, or `--from-template`. |
| `oml inspect`, `census` | Module overview. Use `--own` and `--only` to cut output. |
| `oml describe` | What an existing action, screen or block does. Read it before you change one. |
| `oml find` | Which module owns an element. |
| `oml tree` | Exact object paths for `edits`. |
| `oml validate` | Errors, and warnings with `--warnings`. |
| `oml apply` | Run a spec into a staged copy. |
| `oml install` | Copy a staged file over the target. Confirm first. |
| `oml translations export` | Export module translations. |
| `oml app`, `oml extension`, `oml templates` | `.oap`/`.osp` packages, `.xif` extensions, local templates. |
| `oml studio`, `publish`, `references` | Service Studio MCP: open modules, 1-Click Publish, Refresh All. Confirm first. |
| `oml lt` | LifeTime API v2: environments, apps, tag, deploy, status. Confirm every change. |
| `oml font`, `oml image` | Convert fonts to WOFF2; convert, resize and shrink images. No module needed. |
| `oml api`, `widgets`, `selftest` | Model types, widget descriptors, build check. |

Output is compact JSON. Add `--pretty` only for humans. Exit 0 ok, 1 model or validation failure, 2 usage or environment.
Read [reference/commands.md](reference/commands.md) for full usage, options and examples.

## 4. Spec keys

A spec is one JSON file. Every key is optional. Start with a recipe below. Read [reference/spec.md](reference/spec.md) for keys the recipe does not show.

| Key | What it creates or changes | Section in reference/spec.md |
|---|---|---|
| `module` | Description, JavaScript, PNG icon, user provider and tenancy flags, existing default theme. | Module settings |
| `entities`, `structures`, `exceptions` | Data model. New entities get an `Id`. | Data model |
| `staticEntities` | Static entities with records. | Static entities |
| `references`, `referenceAliases`, `removeUnusedDependencies` | Elements from producer modules, local names for them, and removal of unused ones. | References and aliases |
| `serverActions`, `serviceActions`, `clientActions` | Actions with inputs, outputs, locals and a `flow`. | Logic and flows |
| `clientVariables`, `sessionVariables`, `systemEvents` | Module state and calls from platform lifecycle events. Module kind limits apply. | Module variables and system events |
| `onException`, `globalExceptionHandler`, flow `exceptionHandler` | Separate exception paths and a Reactive global handler. | Exception handling |
| `flow` nodes `aggregate` | Sources, joins, filters, sorts, group by, calculated attributes. | Queries |
| `flow` node `sql` | SQL statements with typed parameters and outputs. | SQL |
| `restClients`, `restServices` | Consumed and exposed REST APIs. | REST |
| `timers`, `roles`, `processes`, `siteProperties` | Platform elements, screen `access`. | Platform elements |
| `screens`, `blocks` (Reactive, Mobile) | Screens, blocks, aggregates, actions, events, widgets. | Reactive UI and Mobile UI |
| `screens`, `blocks` (Traditional Web) | Web screens with `preparation` and web widgets. | Traditional Web UI |
| `layout`, `placeholders`, `template` | Layout blocks and generated list or detail screens. | Layouts and scaffolding |
| `edits` | Changes to existing elements. | Edits |
| `edits` with `delete` | Deletion of elements created earlier in the same spec. Existing elements are refused (see rules.md). | Deletion |
| `copy` | Clipboard copy from read-only modules; explicit dependencies; IPP checks. | Copy, translations, resources, and folders |
| `locales`, `translate`, `translations` | Locales and module resource translations; file import where the host supports it. | Copy, translations, resources, and folders |
| `keepComments` | Keep comments in `styleSheet` and `javaScript` (removed by default). | Comments in styleSheet and javaScript |
| `images`, `scripts`, `themes`, `resources`, `folders`, `requiredScripts` | Content files, styles, folders, and screen/block script requirements. | Copy, translations, resources, and folders |

### Recipes

[copilot-instructions.md](copilot-instructions.md) is the short version for small budgets.
Copy a recipe and read its base-module notes. See [recipes/README.md](recipes/README.md) for order and checks.

| Recipe in `recipes/` | Use it for |
|---|---|
| [entity-with-crud.jsonc](recipes/entity-with-crud.jsonc) | Entity, static values, checked save/delete. |
| [list-detail-screens.jsonc](recipes/list-detail-screens.jsonc) | Reactive list/detail with layout and access. |
| [aggregate-join-groupby.jsonc](recipes/aggregate-join-groupby.jsonc) | Join, filter, sort, Sum/Count by group. |
| [edit-existing-flow.jsonc](recipes/edit-existing-flow.jsonc) | Insert node, add widget, set, rename a flow node. |
| [rest-client.jsonc](recipes/rest-client.jsonc) | Consumed GET/POST and a server call. |
| [references-from-producer.jsonc](recipes/references-from-producer.jsonc) | Approved public producer elements. |
| [sql-query.jsonc](recipes/sql-query.jsonc) | SQL parameter, entity output, loop. |
| [web-screen.jsonc](recipes/web-screen.jsonc) | Web Preparation, table, save action. |
| [timer-role-site-property.jsonc](recipes/timer-role-site-property.jsonc) | Timer, role access, site property. |

## 5. Workflow

Content keys and the `translations export` command use the typed API. Copy runs before logic; missing dependencies and name clashes prevent saving.
`translate.text` is a module resource ID, such as `MandatoryInput`. Image widgets can reference new images in the same spec.
On 11.55.92 direct translation storage works, but file export/import refuses in the headless host. See the spec reference for format limits.

1. Resolve source and staged absolute paths. Record the source hash. Check the version, inspect the module, and record baseline errors.

   ```text
   oml version
   oml inspect <module.oml> --own
   oml describe <module.oml> <Element>
   oml validate <module.oml> --warnings
   oml census <module.oml>
   ```

2. Find and confirm any element needed from another module. Use its approved qualified name.
3. Write one spec for the requested data, actions, UI, and edits. Follow [rules.md](rules.md): add, do not rename or delete existing elements.
4. Apply to a new staged path:

   ```text
   oml apply <source.oml> <spec.json> <staged.oml>
   ```

5. Read saved, problems, newErrors, reopenNewErrors, warnings, and newWarnings.
   If `"saved": false`, fix the spec before retrying. Never retry unchanged.
   If a staged file remains after failure, inspect it but do not install it.
6. Inspect the staged file and verify wiring:

   ```text
   oml inspect <staged.oml>
   oml tree <staged.oml> <path>
   oml validate <staged.oml> --warnings
   ```

7. Run a semantic verifier for the requested behavior and boundary fixtures. Check that the source hash is unchanged.
8. Show changes, remaining issues, staged path, installed build, and required runtime tests to the user.
9. After confirmation, install. Report the backup path and hash. Installation requires zero total validation errors.
10. After confirmation, publish or refresh references through the Beta Service Studio MCP tools.
    If unavailable, report the required manual Service Studio step.
11. For deployment, list applications and show selected tags, keys, modifications, and environments.
    Obtain confirmation for the exact mutation. Retain the deployment key and report its terminal status.
12. Test compilation, database persistence, permissions, timers, REST calls, and browser behavior in the target environment as applicable.

## 6. Troubleshooting

Use this table for CLI work. reference/model-api.md §17 covers typed host implementation failures.

| Symptom | Cause | Fix |
|---|---|---|
| Image reports it cannot load libSkiaSharp.dll | Native library is missing, has the wrong architecture, or a native dependency cannot load. | Check OML_SERVICE_STUDIO; use the installed library in the root or runtimes/win-x64/native. Do not download or redistribute libraries. |
| PNG palette optimization is absent | SkiaSharp 2.88 does not expose an indexed-colour encoder. | Use normal lossless PNG or select WebP. |
| Font tables differ in head or DSIG | WOFF2 requires head.flags bit 11, checksum recalculation, and removal of invalidated signatures. | Compare other bytes exactly; validate rebuilt sfnt checksums. |
| Chrome reports Failed to convert WOFF 2.0 font to SFNT | Chromium expects the final compressed block to end at a four-byte boundary. | Add up to three zero bytes after the compressed stream; include padding in file length but not totalCompressedSize. Do not pad individual tables in the Brotli stream. |
| SVG path changes after rounding | Implicit numeric separators and compact arc flags have special grammar. | Preserve separators when signs disappear; keep arc path data unchanged. |
| Two layouts or headers appear on a new themed screen | Factory starter widgets remain. | Current CLI clears starter widgets for explicit layout/widgets. Existing screens are not cleared by creation lists. |
| Apply returns saved false | Spec problems or new validation errors. | Read problems and newErrors, including detail and owner paths. Fix the spec and use a new staged path. |
| Apply returns saved true with exit 1 | New errors appeared after reopening. | Read reopenNewErrors. Do not install. Fix the spec/model and apply to another path. |
| "Output must differ from source; install with 'oml install' after review." | Source and output are the same path. | Choose a separate staged file. |
| "Staged output already exists:" followed by a path | Output path exists. | Use a new staged path. |
| Install refuses validation errors | Installation checks all errors, including baseline errors. | Fix all staged errors before installing. |
| Self-test or version mismatch | Installed API behavior differs. | Stop, inspect the installed API, and rerun self-test after fixing the host. |
| FileLoadException for "System.Diagnostics.DiagnosticSource, Version=10.0.0.0" | Host loaded incompatible shared libraries. | Use the CLI launcher and installed libraries. See reference/model-api.md §3. |
| "InvalidEntity_OnlyAuto"; entity "cannot be exclusively composed of an attribute with Is AutoNumber" | Entity has only auto-number Id. | Add another attribute, including after attribute deletion. |
| Static record warning "'Integer Identifier' data type expected instead of 'Integer'" | Static Id has an Identifier type. | Use Integer for static Id. Read validation warnings and newWarnings. |
| Element resolves ambiguously or appears as X2 | Name collision, often with generated CRUD. | Inspect/find; use a qualified or free name. Never accept silent renaming. |
| "RequiredPropertyValue_PropertyIn" on execute Action | Missing callee, often a producer reference. | Find and confirm the element; import its deployed export or use Manage Dependencies. |
| "IPPErrorNotConnected" on paste | Cross-infrastructure protected content. | Connect to the source infrastructure, use authorized IPP cleanup, or rebuild owned content. Never bypass the check. |
| "System modules cannot be loaded" for Templates.dat | Protected shipped bundle. | Use a loadable exported module. |
| "unknown placeholder" or missing LayoutTopMenu | Layout contract or dependency is absent. | Inspect placeholder names and export the required OutSystemsUI dependencies. |
| "ExpressionInvalidVariableType" on a Reactive Input Identifier | Input accepts primitive variables only. | Use Dropdown with lookup aggregate. Labels/Values use Customer.Name and Customer.Id, not FetchCustomers.List.Current.Customer.Name. |
| "Can't identify 'GetOrders' element in expression" | Aggregate name changed after source/filter assignment. | Check tree. The host must restore the spec name before expression binding. |
| "Unexpected Link" for a Web screen | Destination is not a frequent destination or flow connector target. | Review the warning; destination and arguments still round-trip. |
| NoErrorHandlingInFlow on a blank UI module | The spec cannot create a UI flow OnException handler. | Use an exported base with a handler; a local layout alone is not enough. |
| Optional column crashes while hidden | CSS visibility does not prevent expression evaluation. | Guard indexed access with If. |
| Custom timer saves but does not run | Headless API does not validate schedule grammar. | Verify custom schedule in Service Studio's editor. |
| Extra roles after broad access | Save/reopen normalizes Registered and Anonymous. | Set both flags false for a custom-role-only gate. |
| Data deletion reports SampleRecord or anonymous CRUD records | Generated model artifacts need filtering. | Inspect consumers; retain real CRUD usages. See reference/model-api.md §17 before changing the guard. |
| Publish fails with "Invalid compiler output. Object reference not set to an instance of an object" | On 11.55.92, generic Identifier types on Id caused this failure. | Use LongIntegerType for entity Id and IntegerType for static Id. Current CLI uses these types. |
| Link/Button displays "linkOverview" | Seeded Text "link" remains beside explicit children. | Current CLI removes seeded children when children is supplied. Verify the staged tree. |
| File-based publish refuses an open module | Open copy would be published instead of the file. | Close that copy, or explicitly select the open module by name. |
| Publish status Blocked | Automatic merge and publish approval is missing. | Ask the user to enable it in Edit > MCP Configuration. |
| Save passes but publish fails | Compilation and runtime validation are separate. | Fix the typed model or dependency; test in the target environment. |
| LifeTime credentials missing or HTTP 401/403 | Environment values or service-account permissions are wrong. | Set credentials securely and check permissions. Server error bodies are not echoed because they may reflect the token. |
| LifeTime HTTPS or redirect failure | Certificate, connection, or redirect refusal. | Fix the endpoint or certificate. Never use test HTTP settings for real LifeTime. |
| Start/request failure after plan creation | Operation may already have happened. | Use the reported key with status before creating another plan. |
| needs_user_intervention, including deployment_prepared | Human decision required. | Read messages and confirm before continue or abort. |
| latest excludes untagged changes | BaseApplicationVersionKey points to a tag. | Review isModified and create a confirmed tag first. |
| Deployment creation returns a bare text key | Pipeline v2 accepts non-JSON POST responses. | Preserve that key before starting; report it even if start fails. |
| LifeTime polling timeout | Plan still runs. | Read status before any new mutation. |

## 7. Reference files

Read these files only when the task needs them.

| File | Read it when |
|---|---|
| [rules.md](rules.md) | Always, before you change a module. |
| [reference/commands.md](reference/commands.md) | You need full usage or options of a command. |
| [reference/spec.md](reference/spec.md) | Your recipe does not show a needed spec key. |
| [reference/model-api.md](reference/model-api.md) | You extend `oml` itself or work directly with the Model API. |
| [reference/odc.md](reference/odc.md) | The module is in ODC, not O11. |
