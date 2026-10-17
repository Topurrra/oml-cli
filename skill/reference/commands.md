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
