# oml

`oml` is a C# CLI for OutSystems 11 (O11). It reads and changes modules through the installed Service Studio's typed model API.
It never edits OML XML.

Describe changes in a spec. Apply the spec to a staged file, review it, then install it over the source file.
You can use the CLI directly or give [skill/SKILL.md](skill/SKILL.md) to an AI agent.

## Terms

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

## Prerequisites

- Windows and an installed Service Studio 11.
- .NET 8 SDK for the build.
- An exported source file from your environment.
- For OutSystems UI layouts, a Reactive source file with the required OutSystems UI references and theme.

The default Service Studio folder is:

```text
C:\Program Files\OutSystems\Service Studio 11\Service Studio
```

For publishing or refreshing references, use Service Studio 11.55.91 or later. Start its MCP server:
**Edit > MCP Configuration > Start MCP Server**. The default port is 41820.
Set `OML_STUDIO_PORT` for another port.
The first call shows an approval dialog for `oml-cli`.
Publishing also needs **allow automatic merge and publish** in that dialog.
These Service Studio MCP write tools are Beta.

## Build and use

Run these commands from the repository root:

```powershell
winget install Microsoft.DotNet.SDK.8
dotnet build -c Release src/OmlCli.csproj
$env:PATH = "$PWD\src\bin\Release\net8.0;$env:PATH"
oml version
```

The executable is `src/bin/Release/net8.0/oml.exe`. Keep its build output folder together.
For another Service Studio folder, set both the runtime path and build property:

```powershell
$env:OML_SERVICE_STUDIO = 'D:\OutSystems\Service Studio'
dotnet build -c Release src/OmlCli.csproj '-p:SS=D:\OutSystems\Service Studio'
```

Build once. Service Studio updates do not normally require a rebuild.
The launcher loads the installed model assemblies and uses the newest installed .NET runtime.
If Service Studio needs a newer runtime, the CLI tells you which runtime to install.

Before selected model commands use a new build, the CLI runs a self-test on a disposable module.
The check takes about three seconds. A pass is cached per build. A failure stops those commands before they load your module.
Run the self-test explicitly after an update when you need to check the build.

## Five-minute start

1. Export your module as `App.oml`.
2. Inspect it and record its validation errors:

   ```powershell
   oml version
   oml inspect App.oml
   oml validate App.oml --warnings
   ```

3. Save this spec as `change.json`. Choose a name that the module does not already use.

   ```json
   {
     "entities": [
       {
         "name": "ReviewNote",
         "attributes": [
           { "name": "Text", "type": "Text", "length": 250, "mandatory": true }
         ]
       }
     ]
   }
   ```

4. Apply it to a new staged file, then inspect the result:

   ```powershell
   oml apply App.oml change.json App.staged.oml
   oml inspect App.staged.oml
   oml describe App.staged.oml ReviewNote
   oml validate App.staged.oml --warnings
   ```

5. Read `problems`, `newErrors`, `reopenNewErrors`, `warnings`, and `newWarnings` when present.
   Fix problems before installation. Use a new output path for each attempt.
   After review, install the staged file:

   ```powershell
   oml install App.staged.oml App.oml
   ```

Installation requires zero validation errors, including errors that existed in the source file.
It creates a timestamped backup, checks hashes, and restores the backup if verification fails.
Publishing is a separate step. See [Publish and deploy](#publish-and-deploy).

## Commands

Angle brackets mark values you must replace. Square brackets mark optional arguments.
This is the CLI usage text:

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

| Command | Result |
|---|---|
| `describe <module.oml> <Element> [--depth N] [--in <folder or file.oml>]...` | Read the logic, data type, or screen outline. |
| `inspect <module.oml> --own` | List this module without references. |
| `inspect <module.oml> --only actions,screens` | Keep only the selected sections. |
| `--pretty` | Show indented JSON, including empty fields, for any command. |
| `font woff2` / `font batch` | Convert TTF, CFF OTF, or WOFF1 to WOFF2; report bytes, ratio and tables. |
| `image` / `image batch` | Resize and encode images, optimize SVG, or rasterize SVG to PNG. |

## Assets

Asset commands operate on files without opening a module or connecting to Service Studio.
Fonts use .NET built-ins in the launcher. Images use the installed SkiaSharp and Svg.Skia libraries through the existing Service Studio load context, without starting the model runtime.
No libraries are downloaded or copied from Service Studio into the build output.

```powershell
oml font woff2 Brand.ttf
oml font batch fonts --out web-fonts
oml image Hero.png --to webp --max-width 1280 --quality 85
oml image Logo.svg --to svg --precision 3 --out Logo.min.svg
oml image Logo.svg --to png --max-width 256
oml image batch images --to webp --max-height 800 --out web-images
```

Inputs are never overwritten, including with `--force`. Existing outputs require `--force`.
Font output defaults to the input name with `.woff2`; image output defaults to `<name>.optimized.<format>`.
Batches read the immediate folder only, report each error, and return exit 1 if any file fails. Output name collisions are reported.
All commands return JSON with byte sizes and output/input ratio; images also report formats and dimensions.

Font collections (`.ttc`) are refused. WOFF2 inputs report `already woff2` without writing a file.
The encoder uses Brotli with null transforms for `glyf`, `loca`, and `hmtx`.
WOFF2 requires removing `DSIG`, setting `head.flags` bit 11, and recalculating `checkSumAdjustment` for the reconstructed sfnt; other table bytes are preserved.
Optional WOFF1 metadata and private blocks are not carried into WOFF2.

Raster inputs include PNG, JPEG, GIF (first frame), BMP, WebP, and ICO if the installed decoder supports it.
Output defaults to the input format. For GIF, BMP, or ICO, select `--to png|jpeg|webp`, because SkiaSharp cannot encode those formats on this build.
Resizing preserves aspect ratio and never enlarges images. JPEG and WebP quality defaults to 85; PNG is lossless.
Re-encoding strips metadata and animation; transparent JPEG areas become white. EXIF orientation is not applied.
SkiaSharp 2.88 has no indexed-colour encoder, so the optional PNG palette pass is skipped.
Image decoding and output are limited to 100 million pixels.

SVG output removes comments, metadata, and editor elements/attributes, keeps `viewBox` and titles, and shortens geometry numbers to `--precision 0-10` (default 3).
Use `--strip-title` to remove titles. Text, CSS, IDs and URLs are preserved; arc path numbers are kept to preserve compact arc flag syntax.
Precision reduction can change geometry slightly. SVG optimization keeps the canvas size; dimension limits apply to raster output.
SVG can be rasterized to PNG. Raster-to-vector tracing is not supported.
Native library errors identify `libSkiaSharp.dll` and the installation path setting.
Re-encoding may produce a larger file; review the reported ratio.

## More command examples

LifeTime usage:

```text
oml lt environments | apps [--env Name] | tag App --version x.y.z [--description Text] [--env Development] | deploy App[,App2] --from Env --to Env [--version x.y.z|latest] [--notes Text] [--wait] | status Key | abort Key | continue Key
```

| Task | Details |
|---|---|
| Inspect | Lists entities, structures, actions, screens, platform elements, integrations, and references. |
| Find | Searches this module, its references, and local producer files. Supports `*` wildcards and repeated `--in`. |
| Tree | Shows element paths, expressions, connectors, event targets, and argument references. |
| Validate | Groups errors. Add `--warnings` to include warnings. |
| New | Creates a blank module, clones an export with `--template`, or uses the application template factory with `--from-template`. |
| App | Lists `.oap`/`.osp`, extracts modules with SHA-256 hashes, or packs `.oml`/`.xif` into `.oap`. Refuses output overwrites. |
| Extension | Inspects `.xif` metadata or creates action and structure signatures. C# implementation is separate. |
| Templates | Finds local samples and bundles. Export a bundle's module as `.oml` before using it. |
| API / widgets | Lists installed model members and registered widget types. |
| Translations | Exports Excel or ResX through the model API. File I/O refuses in the 11.55.92 headless host; direct translation specs work. |

For a blank Reactive module, select `CrossDevice` explicitly:

```powershell
oml new Sandbox.oml --name Sandbox --kind CrossDevice
oml find '*Customer*' App.oml --in exports
oml tree App.oml Orders/Header
oml api 'OutSystems.Model.Logic.Nodes.I*Node'
```

Most command results are JSON on stdout. Usage and launcher diagnostics can appear on stderr.
Exit `0` means success. Exit `1` means failure or a result that needs attention.
Exit `2` means a usage or environment problem.
A failed apply can leave a staged file if validation fails after reopening it. Read the JSON and exit code together.

`--from-template` uses `IApplication.CreateESpaceFromTemplate`. It creates a new module key and keeps template content, theme, layouts, and references.
It defaults to the template kind. An incompatible `--kind` is refused. `--template` keeps its existing clone behavior.
Both use the installed model's permission checks. Package list/extract leave the input unchanged.
Pack includes the supplied modules; it does not fetch dependencies or create application dependency metadata.
See [package and extension details](skill/reference/commands.md#packages-templates-and-extensions) for the extension JSON shape.

## Spec overview

All top-level keys are optional. JSON comments are allowed; trailing commas are not.
Use Service Studio type names, such as `Text`, `Long Integer`, `Decimal`, `Date Time`, and `Email`.
Types also include `<Entity> Identifier`, `List of <type>`, and entity or structure names.
Expression values use OutSystems syntax. A text expression needs escaped quotes in JSON.

| Group | Keys |
|---|---|
| Module | `module`: description, JavaScript, PNG icon, tenancy flags, default theme |
| Data | `entities`, `structures`, `exceptions`, `staticEntities` |
| Dependencies | `references`, `referenceAliases`, `removeUnusedDependencies` (removes unused producer references only; never elements of this module) |
| Logic | `serverActions`, `serviceActions`, `clientActions`, action `flow` |
| State and events | `clientVariables`, `sessionVariables`, `systemEvents`, `onException`, `globalExceptionHandler` |
| Integrations | `restClients`, `restServices` |
| Platform | `timers`, `roles`, `processes`, `siteProperties` |
| UI | `screens`, `blocks`, layouts, list/detail templates |
| Changes | `edits`: insert nodes, add widgets or variables, set properties; rename/delete within production-safety rules |
| Content | `copy`, `images`, `scripts`, `themes`, `resources`, `folders`, `locales`, `translate`, `translations` |

Flows support assignment, branches, loops, switches, aggregates, SQL, action calls, navigation, exception handlers, messages, comments, Excel conversion, downloads, existing Web Email screens, and JavaScript.
See [skill/reference/spec.md](skill/reference/spec.md) for every spec key and examples.
See [Falcon](examples/falcon/README.md) for a complete Reactive example.

Use `Module.Name` to select an element from a producer module.
A plain name selects this module's element first, then a unique referenced element.
Ambiguous names fail with qualified candidates. Use the local name shown by the module, such as `Customer2`.

Import public elements from local producer files with `references`.
The producer file must match the version deployed in the target environment.
Imports add missing elements; they do not refresh existing signatures.
Refresh them in Service Studio after publishing the producer module.
Load and IPP refusals require an authorized export or infrastructure connection. Never bypass them.

## Rules and limits

- Apply always uses a separate, new staged file. It rejects an existing output path.
- Problems or new errors before saving prevent the final staged save.
- Apply compares validation errors with the source baseline. Existing errors do not count as new errors.
- Warnings do not block saving. Read them before accepting the result.
- Creation lists reuse existing elements. Existing attributes are never retyped.
- Use explicit edits to change existing actions, screens, or blocks. Edits run again if you repeat the spec.
- The CLI rejects model renames caused by name clashes.
- Generated CRUD actions can be called in the same spec that creates their entity.
- New entities need an attribute besides their auto-number `Id`.
- Elements present in the source are assumed to have production data and external consumers. Existing entities, static records, structures, actions and their parameters, screens, blocks, roles, exceptions, site properties, timers, REST integrations/methods, and processes cannot be renamed or deleted. Their attributes cannot be renamed/deleted or change type, length, decimals, or mandatory setting. There is no override.
- Elements created in the same spec remain editable and deletable. Entity/attribute deletion still needs `allowDataLoss: true` and no module usages; identifier attributes cannot be deleted alone.
- Flow nodes, widgets, and local variables in existing elements remain editable and removable. New attributes and parameters can be added.
- New or changed SQL containing DELETE, UPDATE, TRUNCATE, DROP, ALTER, or MERGE keywords is refused. Strings and comments are ignored; SELECT and INSERT are allowed. Unchanged SQL already in the module is left alone.

The CLI does not support debugging, SOAP specs, downloading OutSystems UI, or Traditional Web screen templates.
Use explicit screens and widgets for Traditional Web UI.
Content copy uses the clipboard API and keeps IPP checks active. New dependencies must be listed explicitly.
Images, script requirements, themes, resources, folders, locales, and direct module translations are supported.
Translation file export/import is exposed, but the 11.55.92 headless runtime refuses it. See the spec reference for details.
Model validation does not execute SQL, JavaScript, REST calls, timers, or browser behavior.
Test compilation, permissions, database changes, and UI behavior in your environment.

## Publish and deploy

Check the Service Studio connection before publishing:

```powershell
oml studio
oml publish App.oml
oml references App.oml
```

File-based publish refuses when Service Studio already has a module with the same name open.
Close that module first. To publish its open copy, use its module name:

```powershell
oml publish App
```

Reference refresh changes Service Studio's open module. Save or publish that copy to keep the result.
It does not update the file passed to the command.

LifeTime commands use O11 LifeTime API v2. They do not require Service Studio to be installed or running.
Use a service account with read, version tagging, deployment, and plan control permissions for the selected environments.
Set `OML_LIFETIME_HOST` to a hostname with an optional port, without a scheme or path.
Supply `OML_LIFETIME_TOKEN` securely through the process environment. Never store it in commands, files, or logs.

```powershell
$env:OML_LIFETIME_HOST = 'lifetime.example.com'
# Supply OML_LIFETIME_TOKEN through your credential manager.
oml lt environments
oml lt apps --env Development
oml lt tag Shop --version 1.2.3 --description 'Release notes'
oml lt deploy Shop --from Development --to Test --version 1.2.3 --wait
oml lt status <DeploymentKey>
```

Names match without case sensitivity; keys also work.
Review running tags, version keys, and `isModified` before deployment.
Omitted `--version` or `latest` selects the source environment's current base tag. It excludes untagged changes.
Create a tag first if you need those changes. An explicit version selects an existing tag.
LifeTime validates the plan. The CLI adds no dependency tagging, rollback protection, custom deployment zones, or environment-order checks.

`--wait` polls every 20 seconds for up to one hour.
Progress and new messages are JSON lines on stderr. Stdout contains one final JSON result.
The deployment key appears as soon as the plan exists, including in later errors.
Only `finished_successful` passes waiting. Failed, aborted, unknown, or `needs_user_intervention` results exit `1`.
Intervention needs an explicit continue or abort. A polling timeout leaves the plan running.
Check status before repeating a mutation after an error or timeout.
Status and control commands exit `1` for `aborted` or `finished_with_errors`.
Usage, authentication, permissions, connection, TLS, and malformed-response failures exit `2`.

HTTPS uses normal certificate validation. Redirects are refused.
Tests alone can use `OML_LIFETIME_INSECURE_LOCALHOST=1` with `127.0.0.1:<port>` or `localhost:<port>` for HTTP.
Other hosts are refused. Never use this setting for a real LifeTime.

LifeTime is verified against a local mock only. Run a smoke test against a real LifeTime before relying on deployments.
Agents must confirm install, publish, reference refresh, deployment control, tagging, and data deletion with the user.
The CLI does not prompt for that confirmation.

## oml and the OutSystems MCP

The remote OutSystems MCP targets ODC. Service Studio's built-in MCP targets O11 publishing and reference refresh.

| | oml | OutSystems MCP |
|---|---|---|
| Platform | O11 | ODC |
| Runs | Locally, against an `.oml` file | In your ODC tenant |
| How changes are made | Typed model API, deterministic | Mentor (an AI model on the server), from a text prompt |
| Speed | 2–4 s per spec | Minutes per Mentor turn |
| Control | Exact: names, joins, connectors, widgets | Whatever Mentor builds from the prompt |
| Reading an app | `inspect`, `tree` on the file | Context lookups on the tenant |
| Validation | Before and after save, per change | Reported by Mentor after the turn |
| Publish | Yes, through Service Studio's built-in MCP | Yes |
| Deploy between environments | LifeTime API v2 (mock verified; real smoke test required) | Yes |
| Needs | Service Studio 11, .NET 8 | ODC tenant, sign-in |

### Benchmark: oml vs Service Studio's built-in MCP

Two agents did the same task on the same O11 module. Agent A used only Service Studio's built-in MCP (`applyModelApiCode` and its read tools). Agent B used only `oml` with `skill/copilot-instructions.md` and the recipes.

**Task:** add an entity `Product` (Name, Price, IsActive), the server actions `Product_CreateOrUpdate` and `Product_Delete`, and a Reactive screen `ProductList` with a table, using the existing layout.

**Setup:** Codex `exec`, model `gpt-6-luna`, medium reasoning effort, one run per agent, October 2026. Service Studio 11.55.92.

| | Service Studio MCP | oml | Difference |
|---|---:|---:|---|
| Result | 0 errors | 0 errors | Same module content |
| Input tokens | 1,004,361 | **291,201** | **3.4× fewer** |
| Input tokens, not cached | 66,889 | **25,217** | **2.7× fewer** |
| Output tokens | 8,128 | **3,017** | **2.7× fewer** |
| Time | 161 s | **50 s** | **3.2× faster** |
| Tool calls | 20 | **9** | 2.2× fewer |
| Failed tool calls | 5 | **0** | |

Both results were checked with `oml validate` and `oml describe`. They have the same entity (Name Text 50 mandatory, Price Decimal 37,8, IsActive default True), the same calls to the generated entity actions, and the same `ProductList` table inside `LayoutTopMenu`.

Why the MCP agent needed more: three parallel reads failed and were repeated, and two `applyModelApiCode` attempts did not compile before the third one worked. It also read five screens and two actions to learn the patterns that `oml` gives in a recipe.

This is one run of one simple task. Results change with the model, the task and the module.

## Tests

Build first, then run:

```powershell
pwsh -NoProfile -File tests/regress.ps1
```

The suite applies fixtures to new modules, checks saved models, tests LifeTime against a mock, and runs the self-test.
Exit `0` means pass. See [CONTRIBUTING.md](CONTRIBUTING.md) for development and bug reports.

## Project layout

| Folder or file | Contents |
|---|---|
| `src/` | C# launcher, LifeTime client, project files, and shared version settings. |
| `src/Core/` | Typed model host, spec builders, inspection, validation, self-test, and CLI commands. |
| `tests/` | JSON fixtures, PowerShell regression checks, and the LifeTime mock. |
| `examples/falcon/` | Reactive example, spec generator, assets, verifier, and component license files. |
| `licenses/` | License text for adapted OutSystems examples and pipeline code. |
| `README.md` | Setup and use for developers. |
| `skill/SKILL.md` | The agent skill: rules, commands, spec key index, workflow, troubleshooting. |
| `skill/reference/` | Read on demand by agents: full spec reference, Model API reference, ODC. |
| `CONTRIBUTING.md` | Feature development, tests, and bug reports. |
| `THIRD-PARTY-NOTICES.md` | Third-party sources, licenses, and adapted files. |

## Install the agent skill

The skill is the `skill` folder: instructions, recipes, and references. Copy the whole folder.

For GitHub Copilot, copy `skill/copilot-instructions.md` to `.github/copilot-instructions.md`
in the repository where your team works. Keep `skill/` at that repository's root so the agent can open
`skill/recipes/` and `skill/reference/`; if you put it elsewhere, update those paths in the copied instructions.
Copilot adds these instructions to every request, so the short version saves tokens on each request.
Tell the agent where the built CLI and source module are located.

For Claude Code:

```powershell
New-Item -ItemType Directory -Force "$env:USERPROFILE\.claude\skills\outsystems-oml" | Out-Null
Copy-Item -Recurse -Force skill\* "$env:USERPROFILE\.claude\skills\outsystems-oml\"
```

The result is `%USERPROFILE%\.claude\skills\outsystems-oml\SKILL.md` with `reference\` next to it.
For other agents, give `skill/SKILL.md` to the agent as instructions, and keep the `reference` files where the agent can read them.
Tell the agent where the built CLI and source file are located.

## Versioning and notices

`src/Directory.Build.props` sets one version for the launcher and Core. Raise it for each shared release.
Check the CLI version, Service Studio build, runtime, and self-test status with:

```powershell
oml version
```

See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for adapted sources and licenses.
`oml` uses locally installed Service Studio assemblies. It does not ship them.
