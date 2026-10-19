# oml commands

Full usage and details for every command. [SKILL.md](../SKILL.md) has the short table.

#### CLI usage

The following block matches the usage constant in `src/Core/Cli.cs`:

```text
oml version
oml new <out.oml> [--name N] [--kind WebResponsive|Mobile|Service|ReactiveLibrary|...]
oml new <out.oml> --template <name-or-exported.oml> [--name N]
oml new <out.oml> --from-template <template.oml> [--name N] [--kind K]
oml app list <file.oap|file.osp>
oml app extract <file.oap|file.osp> <outdir>
oml app pack <out.oap> --name <AppName> <module.oml|extension.xif>...
oml extension inspect <file.xif>
oml extension new <out.xif> --name <Name> [--spec <spec.json>]
oml templates           (local template/sample modules and bundles)
oml inspect <module.oml> [--own] [--only entities,structures,actions,screens,blocks,references,timers,roles,processes,rest,static,siteProperties]
oml describe <module.oml> <Element> [--depth N] [--in <folder|file.oml>]...
oml find <Name|Pat*> [module.oml] [--in <folder|file.oml>]...  (which module has it: own, referenced, producer files)
oml census <module.oml>
oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>]
oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...
oml preview <module.oml> <Block|Screen> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>]
oml tree <module.oml> <ElementName>   (object tree of one action, screen, entity, ...)
oml validate <module.oml> [--warnings]
oml apply <source.oml> <spec.json> <staged-out.oml>
oml translations export <module.oml> <file> [--format Excel|xlsx|ResX]
oml install <staged.oml> <target.oml>

oml studio                          (Service Studio's MCP server: reachable? open modules)
oml publish <Module | file.oml>     (1-Click Publish through Service Studio)
oml references <Module | file.oml>  (Refresh All references through Service Studio)
oml lt environments|apps|tag|deploy|status|abort|continue (LifeTime API v2; run 'oml lt' for usage)
oml font woff2 <input> [output.woff2] [--force]
oml font batch <folder> [--out folder] [--force]
oml image <input> [--to png|jpeg|webp|svg] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force]
oml image batch <folder> [same options] [--out folder]

oml api <TypeName...>   (members of model/plugin types on the installed build)
oml widgets             (registered widget descriptors)
oml selftest   (build check; also runs by itself on a Service Studio build not seen before)
Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
```

All commands use compact JSON by default. Null fields and empty arrays or objects are omitted.
Use `--pretty` at any position for indented JSON with empty fields. This also works with `oml lt`.
`inspect --own` omits references. `--only` keeps the named sections.
`actions` includes server, service, and client actions. `rest` includes clients and services. `static` means static entities.
Public flags use `public` where the model supports them. Action and exception entries are objects with names.

Use `describe` to understand an element before you change it. Use `tree` when you need exact object paths.
`describe` follows saved connectors and marks repeated nodes with `-> continue at Name`.
It shows signatures, locals, calls, exceptions, data sources, and a widget outline.
The widget outline stops after 80 lines and reports the remaining count. SQL text stops after 200 characters.
`--depth` defaults to `0`: called actions have signatures only. A positive depth adds available bodies.
Use repeated `--in` options for producer files or folders. Missing bodies stay as signatures.
Unknown node kinds keep their kind and name. The output describes the model; it does not run the logic.

#### Font and image assets

These commands edit standalone assets. They do not open modules, initialize the model, run model self-tests, or contact Service Studio.
Font conversion runs in the launcher using .NET built-ins. Images run in Core only to resolve the installed SkiaSharp/Svg.Skia libraries through ServiceStudioContext.
No extra packages or downloads are required.

```text
oml font woff2 Brand.ttf Brand.woff2
oml font batch fonts --out web-fonts
oml image Hero.png --to webp --max-width 1280 --quality 85
oml image Logo.svg --to svg --precision 3 --out Logo.min.svg
oml image Logo.svg --to png --max-width 256
oml image batch images --to webp --max-height 800 --out web-images
```

Never overwrite an input. Existing outputs require `--force`, which replaces the output file only.
Default font output changes the extension to `.woff2`. Default image output is `<name>.optimized.<format>`.
Batches scan one folder without recursion and report per-file errors; any failure gives exit 1. Name collisions fail rather than silently replacing another result.
JSON reports input/output bytes and ratio (output divided by input); fonts include table tags and sizes, images include formats and dimensions.
Global `--pretty` works for both command groups.

Fonts accept TTF, OTF with CFF, and WOFF1 with zlib tables. TTC collections are refused; WOFF2 reports `already woff2` without writing.
WOFF2 uses a single Brotli stream and null transforms (version 3 for glyf/loca; version 0 for hmtx).
Only reconstructed sfnt tables have four-byte padding, not the Brotli stream.
W3C requires removal of DSIG, head.flags bit 11, and a recomputed head.checkSumAdjustment; all other table bytes are preserved.
Tests independently decode WOFF2, compare all table bytes after those required changes, and verify sfnt checksums.
WOFF1 metadata/private blocks are omitted.

Images decode PNG, JPEG, GIF first frame, BMP, WebP, and ICO when supported by the installed SkiaSharp.
Default output uses the same format. GIF/BMP/ICO need explicit `--to png|jpeg|webp` because the installed encoder does not support them.
Raster output strips metadata and animation. EXIF orientation is not applied. JPEG transparency becomes white.
Resize bounds are positive integers; images shrink with aspect ratio preserved and never grow. Decode and output allocations are capped at 100 million pixels.
JPEG/WebP quality is 1-100 (default 85); PNG stays lossless. Indexed PNG is skipped because SkiaSharp 2.88 has no indexed encoder.
SVG can produce optimized SVG or PNG. Raster-to-SVG tracing is refused.
SVG optimization removes comments, metadata/editor elements and attributes, preserves viewBox and title by default, and collapses indentation outside text.
`--strip-title` removes title; `--precision 0-10` defaults to 3 for numeric geometry attributes. Text, CSS, IDs, URLs, and arc path syntax are preserved.
Precision may slightly change geometry. SVG optimization does not resize the canvas; max dimensions apply to rasterization.
File size can grow; inspect the ratio before using an output.

#### More command examples

LifeTime usage from `src/LifeTime.cs`:

```text
oml lt environments | apps [--env Name] | tag App --version x.y.z [--description Text] [--env Development] | deploy App[,App2] --from Env --to Env [--version x.y.z|latest] [--notes Text] [--wait] | status Key | abort Key | continue Key
```

| Command group | Result or limit |
|---|---|
| version | CLI version, Service Studio build, self-test status, and .NET runtime. |
| openapi | OpenAPI 3.0.3 for own exposed REST APIs. Read only; one JSON file per API. |
| architecture | Application and module layers, dependencies and findings in HTML and optional schema version 1 JSON. |
| describe | Short pseudo-code for an action, screen, block, data type, or REST method. `--depth N` adds called bodies. `--in` supplies producer files. |
| inspect | Entities, structures, action parameters, screens, timers, processes, roles, exceptions, integrations, and references. |
| find | `qualified`, `kind`, `isPublic`, `inputs`, `outputs`, `source`, and ambiguity `hint`. Sources: `this module`, `referenced`, `producer file`. Supports `*` and repeated `--in`. |
| tree | Element `path`, `kind`, expressions, `ref` targets, `widgetOrder`, and connector `source`/`target`. |
| validate | Errors grouped by message ID. Add `--warnings` for warnings. |
| new | Defaults to `WebResponsive`. Use `CrossDevice` for Reactive; also supports `Mobile`, `Service`, `ReactiveLibrary`, and installed kinds. |
| templates | Local samples and bundles, kind, theme, references, blocks, and load refusals. Export bundle modules as `.oml` first. |
| apply | Baseline comparison, model construction, staged save, reopen, and validation. |
| install | Both files must exist. Requires zero staged errors. Backs up, hashes, copies, reopens, validates, and restores on failure. |
| api / widgets | Installed type members with `*` wildcards; registered widget descriptor types. |
| translations export | Exports Excel to a file, or ResX to a folder. The 11.55.92 headless host refuses file export/import; `translate` stores module values. |

Most results are JSON on stdout. Usage and launcher diagnostics can appear on stderr.
Exit `0` means success. Exit `1` means failure or attention is needed. Exit `2` means usage or environment failure.
Pre-save problems prevent the final staged save. Post-reopen errors can return exit `1` with `saved: true`.
Read `reopenNewErrors` and the exit code before accepting a staged file.
The shorthand `oml validate --warnings` omits the required module path. Use the full form in the usage block.

#### Packages, templates, and extensions

`app list` reads application metadata, modules (name, key, kind, byte size), and application dependencies.
For `.osp`, it includes every application and loose module. `app extract` writes `.oml` and `.xif` files with SHA-256 hashes.
It checks all filenames before writing and refuses existing files and duplicate names. Input packages are read only.
`app pack` creates `.oap` through `CreateApplication`, `Add`, and `Save`. Supply `.oml` and optional `.xif` files.
It refuses duplicate names or keys. It does not fetch missing modules or create application dependency metadata.
Model load, activation, and IPP refusals are returned in `problems`; they are never bypassed.

`new --from-template` uses `IApplication.CreateESpaceFromTemplate` with OML bytes.
It creates a new module key and keeps template content, theme, layout blocks, and references.
The kind defaults to the template kind. A requested incompatible kind is refused before saving.
The existing `--template` option still uses `CloneESpace`. These operations have separate model factories.
The template factory can reset module metadata such as the icon. Inspect the result before use.

`extension inspect` reports name, key, actions with parameter types and directions, entities, and structures.
`extension new` creates metadata only. Use `--spec` for action and structure signatures:

```json
{
  "structures": [{ "name": "Item", "attributes": [{ "name": "Label", "type": "Text" }] }],
  "actions": [{ "name": "ReadItems", "inputs": [{ "name": "Limit", "type": "Integer" }],
    "outputs": [{ "name": "Items", "type": "List of Item" }] }]
}
```

Types include basic types, named structures, and `List of <type>`.
Structures support `description` and attributes with `length`, `decimals`, `mandatory`, `default`, and `description`.
Actions support `description`, `function`, `inputs`, and `outputs`. Parameters support `mandatory` (inputs), `default`, and `description`.
The CLI saves to bytes, reloads, and compares signatures before writing the output.
It does not create entity definitions, C# code, projects, or compiled extension binaries. Action `flow` and `locals` are refused.
The C# implementation belongs in the extension's .NET project.

The `module` spec object runs in the data phase. It supports `description`, `javaScript`, `icon`, `isUserProvider`, `isMultiTenant`, and `defaultTheme`.
`icon` is a PNG file path, relative to the CLI working directory. `defaultTheme` names one existing theme in the module or its references.
Unknown keys and refused writes become problems and prevent saving. Name, key, and activation code cannot be changed by this object.
`inspect.module` reports these settings, the module key, DBMS, and icon byte size.
See [module settings](spec.md#module-settings) for an example.

#### Service Studio publishing and reference refresh

Requires Service Studio 11.55.91 or later, running with its MCP server started.
Use **Edit > MCP Configuration > Start MCP Server**. The default port is 41820; override it with `OML_STUDIO_PORT`.

| Command | Behavior |
|---|---|
| studio | Checks the connection and lists open modules. |
| publish | Calls `omlPublish`. Opens a file first. Refuses file-based publish if a same-named module is already open. |
| references | Calls `omlRefreshReferences` for Refresh All. Does not add references or write the passed file. Save or publish the open module. |

Publish exits `0` only when `published` is true.
Compare `status` without case sensitivity: `success`, `blocked`, `failure`, or `timeout`.
For `Blocked`, ask the user to enable automatic merge and publish in the MCP Server approval dialog.
The authorization token is cached in the temp folder. The CLI requests authorization once more after a server restart.
Open-module publishing, studio status, and reference refresh were verified on Service Studio 11.55.92; publishing took 9 seconds.

Publish the reviewed file, or explicitly select the open copy by name:

```powershell
oml publish App.oml
oml publish App
```

#### LifeTime API v2

LifeTime commands need O11 API v2 and a service account with read, version, deployment, and plan control permissions.
They work without Service Studio installed or running.

| Setting | Meaning |
|---|---|
| `OML_LIFETIME_HOST` | Hostname with optional port. No scheme or path. |
| `OML_LIFETIME_TOKEN` | Bearer token from the process environment only. Never printed or saved by the CLI. |
| `OML_LIFETIME_INSECURE_LOCALHOST=1` | Tests only: HTTP for exact `127.0.0.1` or `localhost`, with an optional port. Does not disable TLS validation. |

Requests use normal HTTPS certificate checks and refuse redirects.
Names match without case sensitivity; environment and application keys also work.

| Operation | Behavior |
|---|---|
| environments | Lists environment names and keys. |
| apps | Lists applications, running tags, version keys, and `isModified`; optional environment filter. |
| tag | Tags the application running in the selected environment; default environment is Development. |
| deploy | Resolves all applications first, creates an `ApplicationOperations` plan, then starts it. |
| status | Returns execution status and all returned messages. |
| abort / continue | Sends the command, then reads status. Requires user confirmation. |

Source and target environments must differ.
Omitted `--version` or `--version latest` selects the source environment's current base tag, identified by `BaseApplicationVersionKey`.
It does not select the greatest version number. Untagged changes need a new tag before deployment.
An explicit version selects an existing application tag.
The CLI provides no dependency tagging, custom deployment zone selection, rollback protection, or environment-order enforcement.
LifeTime validates routes, dependencies, and plan conflicts.

With `--wait`, polling runs every 20 seconds for up to one hour.
Progress and new step messages use JSON lines on stderr. Stdout holds one final JSON result.
The deployment key appears immediately after plan creation and remains in subsequent errors.

| Status or failure | CLI behavior |
|---|---|
| `saved`, `running`, `aborting` | Keep polling. |
| `finished_successful` | Waiting succeeds, exit `0`. |
| `finished_with_errors`, `aborted`, unknown status | Waiting stops, exit `1`, with status and messages. |
| `needs_user_intervention`, including `Info: deployment_prepared` | Stop waiting, exit `1`. Read messages and confirm before continuing or aborting. |
| Polling timeout | Exit `1`. The plan keeps running. Check status before another mutation. |
| Status/control response | Exit `1` for `aborted` or `finished_with_errors`; otherwise `0` when accepted. |
| Usage, authentication, permissions, TLS, connection, malformed response | Exit `2`. |

Check the reported key after a start or request failure:

```text
oml lt apps [--env Name]
oml lt status Key
```

LifeTime is mock verified only. Run a real LifeTime smoke test before relying on deployments.
`tests/lifetime-check.ps1` checks contracts, polling, intervention, failures, and credential redaction using a local HTTP mock.
The API adaptation derives from OutSystems/outsystems-pipeline. Its license is `licenses/outsystems-pipeline-LICENSE.txt`.

## Architecture

`oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>]`

Read modules and packages without changing them. Repeat `--in`; folders are scanned recursively for `.oml` and `.oap`. Package membership sets each module's application, including loose copies listed in a package. Other loose modules belong to `(no application)`. Repeat `--app` to keep selected applications and their referenced external producers. Package extraction uses a temporary folder that is removed afterwards. Unreadable inputs are skipped with report warnings. Duplicate module names use the first loose file, otherwise the first package copy.

Default output: `architecture.html` in the current folder. `--json` writes schema version 1 data, also embedded in the HTML. Both outputs refuse existing paths. Stdout reports modules, applications, edges, findings by severity, warnings, output paths and elapsed seconds. Modules load and dispose one at a time.

Layers: `EndUser`, `Core`, `Foundation`. `--layers` accepts a JSON object such as `{ "Shop*": "Core", "Shop_CW": "EndUser" }`. Matching is case insensitive. Exact names win; wildcard patterns use descending non-wildcard length, then alphabetical order. Overrides precede library/extension/platform classification, then known suffixes, then content. Screens suggest EndUser; entities, integrations or business logic suggest Core; utility actions, blocks, themes and scripts suggest Foundation. `layerReason` explains the guess. Review these guesses before changing a module.

JSON includes module kind, description, own element counts, public contracts, entity attributes and identifier references, screens, exposed and consumed REST method counts and consumed base URLs, timers and processes. Imported entities, server/service actions, structures and blocks make a dependency strong. Theme/script/image/role-only dependencies are weak. Edges represent imported signatures rather than runtime call counts. External modules have only their name, application `(external)`, external flag, layer and reason.

Finding ids: `upward-reference`, `side-reference-enduser`, `cycle` (high); `core-ui`, `foundation-business` (medium); `unused-public`, `large-module`, `missing-description` (low). Cycles include a closed path. Large modules have more than 150 actions, screens and entities. Unused public contracts are checked only with at least two input modules and may have consumers outside the input. Missing descriptions are counted once per module. Static file analysis cannot recover dynamic or runtime dependencies.

The launcher resolves `--team` through LifeTime before calling Core. The internal `--team-label <Name>` option only records `input.team`. Local exports need no connection. When LifeTime is unavailable, export the team's applications as `.oap` files and use `--in <folder>`.

## OpenAPI

`oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...`

Reads exposed REST APIs owned by the module, including unpublished staged files. Without `--api`, exports every API. An unknown name reports available APIs. Default output: `<Module>.<Api>.openapi.json` in the current folder. A `.json` output path requires one selected API; otherwise `--out` is a folder. Refuses existing files. JSON files are indented UTF-8 without BOM, with LF endings. Stdout is a compact report: `files`, each with `file`, `operationCount`, `schemaCount`, and `placeholders`.

Uses typed API paths, verbs, descriptions, Receive In / Send In, wire names, mandatory inputs, authentication, and recursive structure/entity attributes. Lists have `items`; schemas include mandatory attributes, text lengths, and simple literal defaults. Type names gain a module prefix only on clash. Basic authentication emits HTTP Basic. None emits `x-oml-authentication: None`. Custom emits `x-oml-authentication: Custom` and an apiKey scheme with a placeholder header name.

Repeat `--server` for environment base URLs. Otherwise the server uses `https://<FILL: hostname>/<Module>/rest/<Api>` (the model base path when available). Fill version, contact, license, terms of service, documentation, missing descriptions, and Custom authentication details. Search `<FILL:`; every example leaf is also in `placeholders`, including numbers, booleans, and dates. Report paths use dots and array indexes, for example `paths./orders/{Id}.get.responses.200.content.application/json.example.Total`.

Exposed REST has no typed request/response format setting on this build; Text bodies use `text/plain`, other bodies use `application/json`. Success defaults to `200`, without content for no body output. Runtime status overrides and callbacks are not inferred. Error responses share `OutSystemsError`, assumed to be `{ "Errors": [string], "StatusCode": integer }`; installed documentation did not confirm the shape. `401` appears only for authenticated APIs. Static entity IDs are omitted from enums because the typed static record contract exposes symbolic identifiers and expressions, not runtime numeric IDs. Unknown types have an unconstrained schema with an OutSystems type description. Recursive example cycles use a placeholder string that needs a valid example supplied by the user.

## Preview

`oml preview <module.oml> <Block|Screen> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>]`

Renders one block or screen to one self-contained static HTML file, so you can see it before you publish. It reads the widget tree with the typed model and never changes the module. Default output: `<Element>.preview.html` in the current folder. Existing `--out` and `--screenshot` files are refused. Stdout is the compact JSON report.

Use it for layout, texts, CSS and JavaScript. It is not a pixel copy of the published app: OutSystems UI is not available, so its classes (`card`, `btn`, layouts, icons) look plain. Reactive and Mobile modules are the target. Traditional Web screens go through the same reader; widgets it does not know are listed in `unsupported`.

How widgets become HTML. The widget Name is the `id` and the Style Classes are the `class`.

| Widget | HTML |
|---|---|
| Container, Form, Label, List, ListItem | `div`, `form`, `label` |
| Text | The text. |
| Expression | A string literal shows its text. An input parameter shows the `--arg` value, else its default value, else `{InputName}`. A `+` join of those also works. Anything else shows as `{expression}` in a muted span. |
| Link, Button | `a`, `button` with the child widgets. |
| Image | `img` for an external URL. A resource image is a grey box with its name. |
| Icon | `i` with the Style Classes and the icon class. |
| AdvancedHtml | Its Tag (`div` if it is missing or unsafe). |
| Input, TextArea, Dropdown, Checkbox, Switch, RadioButton | The matching form element. A Dropdown has 3 sample options. |
| If | The true branch only. The report notes it. |
| List, TableRecords | The row template repeated 3 times. A table has its header row. |
| Placeholder | A dashed box with the placeholder name and sample text. When a block instance gives content, that content is rendered instead. |
| Block instance, same module | The block is rendered inline and recursively, up to 5 levels. Arguments and placeholder content are passed. Cycles are not expanded. |
| Block instance, other module | A `div` whose class is the block name, with its placeholder content inside. Layouts and OutSystems UI blocks are not drawn. |
| Any other widget | A labelled box with its children. The kind and its count are listed in `unsupported`. |

A widget with Visible set to the literal `False` gets the `hidden` attribute. Extended properties with a literal value become HTML attributes (`class` is merged). Names that start with `on` and `id` are skipped.

CSS in the file, in this order: a small neutral base stylesheet (system font, spacing, plain buttons and tables), the module default theme (only if it is local to the module), then the stylesheet of the element and of each same-module block that was rendered. External CSS is not loaded.

`--theme light|dark` (default `light`) sets `data-theme` on `<html>` and a matching base colour. Static HTML cannot emulate `prefers-color-scheme`, so only CSS that reads `[data-theme]` or CSS variables changes with the theme. `--width <px>` (default 1200) is the width of the frame around the content. Use 390 for a phone. `--height <px>` (default 900) is the screenshot height.

`--arg Name=<expression>` sets an input parameter of the element. Use an OutSystems literal, for example `--arg Count=3`, `--arg Active=True` or a quoted text. Other text is used as plain text and the report says so. An unknown name reports the inputs.

JavaScript. For the element and every rendered same-module block, the JavaScript nodes of the OnInitialize, OnReady and OnRender actions are found by following the connectors from Start. Each node body is emitted as `function($parameters, $actions, $roles, $public){ ... }` and called after DOMContentLoaded, once for each rendered instance of the block. `$parameters` is built from the node arguments:

- `<WidgetName>.Id` becomes the id of the rendered element.
- An input parameter becomes its `--arg` or default value.
- A simple literal (text, number, True, False) becomes that value. Any other expression becomes `null`.

`$actions`, `$roles` and `$public` are empty objects behind a Proxy. An unknown call is logged to the console and does not throw. Each call is wrapped in try/catch. Errors, window errors and unhandled promise rejections are written to a red panel at the bottom of the page and to `window.__omlPreviewErrors` (an array). The page also sets `data-oml-errors="<count>"` on `<html>`. A headless browser with `--dump-dom` prints the DOM after the scripts ran, so a script can read that attribute. The real OutSystems id differs (for example `b2-Root`). The preview uses the widget Name, so code that tests `!element.id` behaves differently. Client actions that an OnRender action calls are not followed.

`--screenshot <file.png>` runs Edge or Chrome headless (`--headless=new`) if one is installed in the usual Program Files or LocalAppData folders. Set `OML_BROWSER` to a browser `.exe` to choose another. The page is captured about one second after load, so animations are near their start. The browser window has a frame and a minimum width of about 500 px, so the command first reads the real page size and then sizes the window to match. The PNG can be about 25 px wider and 90 px taller than `--width` and `--height`; the page area has the exact size. A frame narrower than 500 px is laid out at about 500 px for media queries. With a screenshot, the report also has `javaScriptErrors` (`count` and `messages`) and `horizontalOverflow` (the content is wider than the frame). If no browser is found or it fails, `screenshot` is `null` (omitted in compact output) and `screenshotNote` says why. The exit code stays 0.

Report fields: `html`, `screenshot`, `screenshotNote`, `element`, `kind` (Block or Screen), `theme`, `width`, `widgets` (distinct widgets rendered), `blocksRendered` (names, one per instance), `unsupported` (`kind` and `count`), `javaScriptNodes` (distinct nodes), `javaScriptErrors`, `horizontalOverflow`, `notes`, `checklist`.

`checklist` has six automatic checks. Each has `id`, `name`, `ok` and `detail`. A failed check never fails the command; fix what `detail` names.

| Id | Fails when |
|---|---|
| a | A Link or Button text is `link` or `Button`, or starts with it and has more text glued on (`linkCopy spec`). The toolbox seeds that text and the spec must replace it. |
| b | The JavaScript calls `getContext(` and the root Container has no `height`, `min-height` or `aspect-ratio` rule on its class. |
| c | A Text or literal Expression says "Demo content" or "Lorem ipsum". |
| d | The CSS uses `var(--...)` somewhere and also has colour values (hex, `rgb()`, `hsl()`) in declarations without `var()`. `detail` has the count. |
| e | A JavaScript node adds a `window` or `document` listener with no matching `removeEventListener` for the same event in the same node. |
| f | A rule targets a global element (`body`, `html`, `*`, a bare `button`, `a`, `input`, ...) with no class, id or attribute. `:root` is allowed. |

The checks read the strings of one render. They help, but they do not replace looking at the screenshot. See [ui-checklist.md](ui-checklist.md).

```powershell
oml preview Staged.oml FxCard --theme light --screenshot light.png
oml preview Staged.oml FxCard --theme dark --width 390 --screenshot dark-390.png --arg Title=Hello
```

Exit 0 when the file was written. Exit 2 for an unknown element, a bad option or an existing output.
