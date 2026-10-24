# Contributing

## Build

Use Windows, Service Studio 11, and the .NET 8 SDK.
Run commands from the repository root:

```powershell
dotnet build -c Release src/OmlCli.csproj
$env:PATH = "$PWD\src\bin\Release\net8.0;$env:PATH"
oml version
```

The executable is `src/bin/Release/net8.0/oml.exe`. Keep the build output folder together.
For a different Service Studio folder, set the runtime path and build property:

```powershell
$env:OML_SERVICE_STUDIO = 'D:\OutSystems\Service Studio'
dotnet build -c Release src/OmlCli.csproj '-p:SS=D:\OutSystems\Service Studio'
```

The launcher in `src/Program.cs` loads the installed assemblies.
Core uses typed interfaces and reflection to handle build differences.
The self-test checks behavior instead of relying on a supported-build list.

## Run tests

Build first, then run:

```powershell
pwsh -NoProfile -File tests/regress.ps1
```

Exit `0` means pass. The exit code is the number of failed checks. The suite needs PowerShell 7 and clears `tmp/` when it starts.
`-Throttle N` sets how many checks run at once (default 4).
It applies JSON fixtures, checks saved models, tests LifeTime against a local mock, and runs the self-test.
LifeTime tests do not contact a real server.
Run a real LifeTime smoke test before relying on deployment behavior.

### How the suite runs

`tests/regress.ps1` splits the work into units. A unit is one fixture apply or one `*-check.ps1` script.

1. Setup, one at a time: it builds the blank modules (`r0`, `s0`, `w0`) and the producer module in `tmp/base`.
2. Parallel lane: up to `-Throttle` units at once. Each unit runs in its own `pwsh` process with its own work folder `tmp/<unit>` (a copy of `tmp/base`) and its own `TEMP`. A unit can wait for other units (`-After`). A unit that makes `out-*.oml` files that others read publishes them into `tmp/base` (`-Publish`).
3. Alone lane, one at a time, after the parallel lane: `lifetime-check` (local port, mock server, `OML_LIFETIME_*` variables), `doctor-check` (sets `OML_*` variables, reads machine-wide state), `log-check` (writes the default usage log), `preview-check`, `test-scaffold-check`, `architecture-page-check`, `check-page-check`, `review-page-check`, `image-check` and `dashboard-check` (headless browser with timeouts, flaky under load; `image-check` also times a 1024 px trace; the dashboard check also binds a local port), `setup-check` (writes to HKCU under a throwaway key and compares real `HKCU\Software\Classes` keys before and after) and `selftest` (long, heavy, records the build as checked; its `selftest-check.ps1` removes the build from its own record file (`OML_SELFTEST_RECORD`, so other `oml` processes are not affected) and starts `oml dashboard` on a free port).

Output is printed per unit in the order of the list in the script, with the unit's time. Read the exit code and every `FAIL` line.

### Add a regression check

1. Write `tests/<name>-check.ps1`. Take `$Oml` and `$Tmp` as parameters. Put every file in `$Tmp`. It is your own folder, a copy of the shared base modules. Throw (or call `Write-Error`) on failure.
2. In `tests/regress.ps1` add a `Unit '<name>' { ... }` where the script is listed, in the order you want its output. Copy the pattern of a neighbour: `try { & (Join-Path $tests '<name>-check.ps1') $oml $tmp } catch { $global:fail++; Write-Host "FAIL ...: $_" }`.
3. The body sees `$oml`, `$tests`, `$tmp`, `$reactive`, `$service`, `$web`. Count failures with `$global:fail++`.
4. Parallel is the default. Add `-After 'other-unit'` if you read a file another unit makes. Add `-Alone` only if the check changes machine-wide state, binds a fixed port, needs a quiet machine or is long and heavy. Put the reason in the comment at the top of `regress.ps1`.
5. Do not write outside `$Tmp`, and do not rely on files made by another unit unless you declare it with `-After`.

To run the build capability check alone:

```powershell
oml selftest
```

## Benchmarks

`tests/bench/` holds the reproducible benchmark behind the README's benchmark section: the OutSystems MCP side against `oml`, run through Claude Code. A run uses real API usage and needs Service Studio for the MCP side, so it is not part of the regression suite. Read [tests/bench/README.md](tests/bench/README.md) before you run it, and use `-DryRun` first to check the plan.

### Memory speed

`oml memory bench <new folder> --records 100000` writes a synthetic store and prints recall times without the index cache, with a rebuild of it, and with it. `tests/memory-check.ps1` runs it at 1k, 10k and 100k records and prints the numbers; the table in `skill/reference/commands.md` ("Memory", "Speed") comes from such a run. When you change the record layout, the hash or the search fields, update the format table there and the layout constants at the top of `memory-check.ps1`, which recomputes the hash chain outside the C# code.

## Releasing

Build releases locally on Windows with PowerShell 7, Service Studio 11, and the .NET SDK.
Update `src/Directory.Build.props` and add a matching `## <version>` section to `CHANGELOG.md`,
then commit all changes. Configure `origin`; the script checks both local and remote tags.
Set `OML_SERVICE_STUDIO` for a non-default installation and ensure `dotnet` is on PATH.

```powershell
pwsh -NoProfile -File ./release.ps1
```

The script requires a clean working tree on a branch. It cleans and builds Release,
runs the full regression suite, publishes a framework-dependent win-x64 CLI, checks that
no Service Studio files are included, and smoke-tests the extracted archive.
It writes the zip, SHA-256 checksum, and `release-notes.md` to ignored `dist/` and prints
the exact tag, push, and GitHub CLI release commands for you to run.
`-Version` overrides the shared version for the build and package; `-OutDir` changes the output folder.
Use an ignored folder or a folder outside the repository for output.
With `-Publish`, it shows the repository, tag, and files and requires interactive confirmation
before running those commands. This requires an authenticated `gh` CLI. Never use `-Publish` in tests.
If publishing stops partway through, inspect the local and remote tag and release state before retrying.

## Add a feature

1. Add a focused file in `src/Core/`. Extend the shared builder when needed.
2. Use `Reflect` for interface members, setters, and deferred expressions.
   Use `Refs` for element names and references. Do not create separate lookup rules.
3. Add the spec key to its typed record or raw-object handler.
   Define defaults, supported module kinds, and behavior for existing elements.
4. Add a fixture in `tests/`. Check saved behavior after reopening, including targets, types, and arguments.
   Add refusal cases for unsafe or unsupported input.
5. Add a self-test step for the installed API behavior that the feature needs.
6. Add the fixture or checks to the regression suite. Build and run the suite.
7. Document the key, limits, and an example in `skill/reference/spec.md`. Add it to the key index (the "Spec keys" section of `spec.md`) and to `README.md` when users need to know it.
8. Raise the shared release version in `src/Directory.Build.props` when preparing a release.

See [the Model API reference](skill/reference/model-api.md) before changing model construction.

## Dashboard

`oml dashboard` lives in the launcher, in `src/Dashboard/`:

| File | Contents |
|---|---|
| `Dashboard.cs` | The `HttpListener` server on 127.0.0.1, the key, `Host` and `Origin` checks, static files (the web root and one folder level; `..`, backslashes and deeper paths give 404), sandboxed views, and `Oml(...)`: the one way the dashboard runs a command. |
| `DashboardApi.cs` | One handler per action of the page. Each checks its input and builds the arguments of a normal `oml` command. |
| `web/` | The page, in plain JavaScript ES modules (no build step, no framework). `web/readme.md` describes it. Embedded into `oml.exe` at build time, so rebuild after a change. |

The `web/` folder:

| File or folder | Contents |
|---|---|
| `index.html`, `theme.js`, `app.css` | The page, the theme before first paint (and the script error count), the styles. |
| `main.js` | The shell: router, sidebar and global controls. |
| `nav.js` | The sidebar: groups and page ids in order. The only place to change it. |
| `pages/<id>.js` | One page each: `{ id, group, title, description, icon, render(main, ctx) }`. Registered in `pages/index.js`. |
| `core/` | Shared code, no page logic: `dom.js` (`h`, `add`, `icon`, the `ICONS` list), `api.js` (`api`, `need`), `ui.js` (`task`, `copyButton`, `cli`, downloads), `viewer.js` (`viewer`), `router.js`, `store.js`, `workset.js`. |
| `data/` | Fixed lists: `cookbook.js` (every `oml --help` command needs an entry) and `prompts.js` (the Small task and Architect templates). |
| `tools.js`, `tools-more.js`, `tools-ui.js` | The browser-only Developer tools. |

Keep every file under 400 lines. Embedded files must be plain ASCII (`encoding-check`). A new subfolder needs its own `EmbeddedResource` line in `src/OmlCli.csproj`.

How a command runs: `Oml(...)` queues it on one worker thread. Launcher commands (`doctor`, `spec`, `font`, `lt`, `log`) run with `Console.Out` captured; model commands go through `Cli.Capture` in Core (`src/Core/Commands.Run.cs`), which is `oml run`'s step runner with modules kept open between calls. Core refuses any command that is not on its `DashboardAllowed` list, as a second lock behind the API.

To add a page:

1. If it needs the server, add a case to the `switch` in `DashboardApi.Api` and a handler that checks its input (`ModuleFile`, `UserPath`, `ElementName`) and calls `Oml(...)`. Only read commands; never add `install`, `publish`, `references`, `apply`, `run` or a LifeTime write. Outputs a person keeps go to `NewWorkFolder(...)`; intermediate files go to a temporary folder that the handler deletes.
2. Add `web/pages/<id>.js` that exports the page object, and register it in `web/pages/index.js`. Build elements with `h()`, run actions with `task(...)`, and show each command with `cli(data.cli)`. Show every image and `/view/` page with `viewer({...})`.
3. Add the id to a group in `web/nav.js`, and an icon path under the same id in `ICONS` (`web/core/dom.js`).
4. Add a check to `tests/dashboard-check.ps1`, build, look at the screenshots in `tmp/dashboard-check/`, and run the suite.

To change the sidebar, edit `web/nav.js` only: rename a group, move or reorder ids, or remove an id to hide a page (it stays reachable by its URL). `dashboard-check` compares the sidebar on screen with `nav.js`.
## Rules

- Add no new dependencies.
- Never edit OML XML. Use the typed model API.
- Use generic interface and descriptor discovery instead of hard-coded concrete classes or descriptor GUIDs.
- Never swallow model errors or reflection failures. Report the failed operation and its context.
- Create elements and references before binding expressions.
- Bind arguments by parameter name, never by position.
- Refuse silent renames. Reuse existing elements without changing unrelated content.
- Validate before saving and after reopening. Preserve the source file during development.
- Run one model-edit job per process: model providers and plugin registries are global singletons.
- Do not redistribute Service Studio assemblies or bypass IPP and licensing checks.
- Keep credentials out of fixtures, logs, and reports.

## Report a bug

Include:

- The command and its exit code.
- The output of the version command below.
- The spec that reproduces the problem.
- The complete JSON output, including validation errors and warnings.
- The expected result and the actual result.
- The module kind, required references, and a shareable source file if company policy permits.

```powershell
oml version
```

Remove credentials and confidential data before sharing a report.
Keep error message texts and element paths unchanged when possible.

## Website

Open `docs/index.html` in a browser to preview the site. It uses plain HTML, CSS and JavaScript. No website build is needed.

To regenerate the architecture demo, remove the existing `docs/architecture-demo.html` first (the renderer refuses overwrites). Run from the repository root:

```powershell
$env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"
dotnet build src -c Release
src\bin\Release\net8.0\oml.exe architecture-render tests\architecture-sample.json docs\architecture-demo.html
```

The demo uses the CLI's own page template and the checked-in sample. It needs no model command or server connection. Keep its license notice. The demo keeps a "Back to oml" link that is added to the generated header, not to the template. Add it again after each regeneration:

```powershell
$f = 'docs\architecture-demo.html'
(Get-Content -Raw $f).Replace('<header><div class="mark"', '<header><a href="index.html" style="color:inherit;text-underline-offset:3px">Back to oml</a><div class="mark"') | Set-Content -NoNewline -Encoding utf8 $f
```

The page and `src/Core/ArchitectureLayout.cs` run the same layout algorithm, one in JavaScript and one in C#. Change them together: `tests/architecture-page-check.ps1` compares their edge crossings for the sample and for a generated 100-module sample. `tests/architecture-baseline.json` stores the crossings the sample and the generated 100-module and 300-module portfolios (`tests/architecture-synthetic.ps1`) may not exceed; if a change lowers them, lower the baseline by hand.

GitHub Pages serves `docs/` from `main`. Configure Settings → Pages → Deploy from a branch → main, /docs.

## Project layout

| Folder or file | Contents |
|---|---|
| `src/` | C# launcher, LifeTime client, project files, and shared version settings. |
| `src/Dashboard/` | `oml dashboard`: the local web server, its API and the embedded page (`web/`). |
| `src/Memory/` | `oml memory` (launcher only): `MemoryFormat.cs` (the 850-byte record and the hash chain), `MemoryStore.cs` (log, lock, repair, the `memory.idx` cache), `MemorySecrets.cs`, `MemoryContradiction.cs` (ports of voli's firewall and classifier), `MemoryCommand.cs`, `MemoryBench.cs`. |
| `src/Core/` | Typed model host, spec builders, inspection, validation, preview, architecture, self-test, and CLI commands. |
| `tests/` | JSON fixtures, PowerShell regression checks, and the LifeTime mock. |
| `skill/` | The agent skill: `SKILL.md`, `rules.md`, the Copilot file, `recipes/`, and `reference/`. |
| `docs/` | The website, served by GitHub Pages. |
| `licenses/` and `THIRD-PARTY-NOTICES.md` | Licenses of adapted third-party code. |
