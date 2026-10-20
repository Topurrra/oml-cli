# oml

`oml` is a command-line tool for OutSystems 11. It reads and changes `.oml` modules through the typed Model API of your installed Service Studio. It never edits OML XML.

You describe a change in a JSON spec, apply it to a new staged file, check it, and then install it. You can use `oml` yourself, or give its skill to an AI agent.

Website: [topurrra.github.io/oml-cli](https://topurrra.github.io/oml-cli/) · [Releases](https://github.com/Topurrra/oml-cli/releases) · [Changelog](CHANGELOG.md)

## Dashboard

Not a terminal person? `oml dashboard` opens a local web page with the same tools.

![The oml dashboard home page: setup check, Service Studio status and links to every page](docs/dashboard.png)

*Screenshot: `docs/dashboard.png`, the Home page in the dark theme.*

Start it with `oml dashboard`, or double-click `oml-dashboard.cmd` next to `oml.exe`. It opens your browser; keep its window open, and press Ctrl+C there to stop it. `--port N` picks the port, `--no-open` does not open the browser.

What it can do: check your setup (`doctor`), browse a module and read what each element does (`inspect`, `describe`, `find`), pull the module open in Service Studio, show previews of every screen and block in light and dark, build a clear task for an AI agent, browse the recipes, convert fonts and images, export and fill in OpenAPI files, make architecture pages, read LifeTime, show what agents ran (usage log), and a set of small developer tools (JSON, Base64, JWT, regex, colour contrast, ...). Every action shows the CLI command, with a copy button.

Security: it listens on 127.0.0.1 only, with a random key in its address that every request must send; other sites cannot call it.
It only reads: it never installs, publishes, refreshes references, or tags or deploys in LifeTime, and the LifeTime token stays in the environment, never in the page.

## What it covers

| Area | What `oml` does |
|---|---|
| Build and change | One JSON spec for entities, static entities, CRUD actions, server/service/client actions, flows, aggregates with joins, SQL, REST, screens and blocks (Reactive, Mobile, Traditional Web), timers, roles, processes, translations and resources. New flows are laid out the way Service Studio does it. |
| Read cheaply | `inspect`, `describe` (what an action or screen does, in a few lines), `find` (which module owns an element). Compact JSON output keeps agent token use low. |
| Check UI | `preview` renders a block or screen to HTML, takes light/dark and phone-width screenshots, and runs automatic UI checks. |
| Architecture | `architecture` makes one zoomable HTML page from a folder of `.oap`/`.oml` files or a LifeTime team: applications, modules, End-user/Core/Foundation layers, dependencies and findings. |
| REST | `openapi` exports exposed REST APIs as OpenAPI 3.0.3, with `<FILL:` placeholders for contact, servers and examples. |
| Service Studio and deploy | `pull` saves the module open in Service Studio to a file (no manual export); 1-Click Publish and Refresh All through Service Studio's MCP server; LifeTime API v2 tags and deployments. |
| Packages | List, extract and pack `.oap`/`.osp`; inspect and create `.xif` extensions. |
| Assets | Fonts to WOFF2; convert, resize and shrink images. `apply` removes comments from CSS and JavaScript (license notices stay). |

Full command list: [skill/reference/commands.md](skill/reference/commands.md). All spec keys: [skill/reference/spec.md](skill/reference/spec.md).

## Commands by audience

Developers work in Service Studio. Most commands are for AI agents, so agents can work faster and with fewer tokens.

| Group | Commands | Used by |
|---|---|---|
| Dashboard | `dashboard` | Developers |
| Converters | `font woff2`, `font batch`, `image`, `image batch` | Developers |
| REST | `openapi` | Developers |
| Architecture | `architecture` | Developers, architects |
| Read a module | `inspect`, `describe`, `find`, `census`, `tree` | Agents |
| Change a module | `new`, `apply`, `spec`, `run`, `install`, `validate`, `translations export` | Agents |
| UI | `preview` | Agents, sometimes developers |
| Packages | `app list`, `app extract`, `app pack`, `extension inspect`, `extension new`, `templates` | Agents, sometimes developers |
| Service Studio and deploy | `studio`, `pull`, `publish`, `references`, `lt` | Agents; publish and deploy after you confirm |
| Diagnostics | `version`, `selftest`, `doctor`, `log`, `api`, `widgets` | Agents, maintainers |

## Benchmarks

Same task, same module, same model on both sides. The OutSystems MCP side used Service Studio's MCP server with the official OutSystems skill for O11. Each row is one run, so treat the numbers as a direction, not a guarantee.

**Small task** (an entity, two CRUD actions, a list screen; model `gpt-6-luna`):

| | OutSystems MCP + O11 skill | oml |
|---|---:|---:|
| Result | 0 errors | 0 errors, same content |
| Input tokens | 611,714 | **207,210** (3.0× fewer) |
| Output tokens | 4,474 | **2,780** |
| Time | 185 s | **56 s** (3.3× faster) |
| Tool calls (failed) | 19 (2) | **10 (0)** |

**Large task** (24 UI components, a gallery screen, theme toggle and font picker; model Claude Haiku 5.5; cost as reported by Claude Code):

| | Result | Cost | Time | Turns |
|---|---|---:|---:|---:|
| MCP, 1 agent | 24/24, 0 errors | $0.94 | 787 s | 58 |
| **oml, 1 agent** | 24/24, 0 errors | **$0.55** | **706 s** | **37** |
| MCP, 8 agents + 1 assembly | **20/24**: one agent was blocked, one block was lost in a merge | $3.16 | 968 s | 340 |
| **oml, 8 agents + 1 assembly** | 24/24, 0 errors, 0 warnings | **$1.17** | **700 s** | 267 |

Why: output is similar, because both sides write the same CSS and JavaScript. MCP needs more turns, and every turn reads the context again. Parallel MCP agents all edit the one module open in Service Studio; `oml` agents each work on their own files.

**oml 1.1.0 vs 1.2.0** (the same large task, 1 agent, same module, Haiku 5.5, 3 runs each, averages):

| | Cost | Time | Turns | Result |
|---|---:|---:|---:|---|
| oml 1.1.0 | $0.80 | 864 s | 64 | 24/24, 0 errors |
| **oml 1.2.0** | **$0.36** | **615 s** | **37** | 24/24, 0 errors |

1.2.0 gives agents a fixed loop that batches commands into fewer tool calls. One 1.2.0 run left unexplained warnings, so the skill now requires fixing or explaining every new warning; one run with that rule cost $0.81 with 0 warnings, so the cost with the rule still needs more runs. Re-run any of these with [tests/bench](tests/bench/README.md).

## Install

**Prerequisites:** Windows, Service Studio 11, and the .NET 8 runtime (the SDK to build from source). Publishing and reference refresh need Service Studio 11.55.91 or later with its MCP server started (**Edit > MCP Server > Start MCP server**).

**Release:** download `oml-<version>-win-x64.zip` from [Releases](https://github.com/Topurrra/oml-cli/releases), unzip it, keep its folders together, and add `bin` to PATH. Unsigned executables may be blocked by company policy; then build from source.

**From source:**

```powershell
dotnet build -c Release src/OmlCli.csproj
$env:PATH = "$PWD\src\bin\Release\net8.0;$env:PATH"
oml version
```

For a Service Studio folder other than the default, set `OML_SERVICE_STUDIO` and build with `-p:SS="<that folder>"`. On a new Service Studio build, `oml` first runs a short self-test and stops if it fails.

Run `oml doctor` to check your setup.

## Quick start

```powershell
oml inspect App.oml --own --only entities,actions,screens
oml apply App.oml change.json App.staged.oml
oml validate App.staged.oml --warnings
oml describe App.staged.oml ReviewNote
oml install App.staged.oml App.oml
```

`apply` never changes the source file. `install` replaces it only when the staged file has zero validation errors, and keeps a backup. A minimal `change.json`:

```json
{ "entities": [ { "name": "ReviewNote", "attributes": [ { "name": "Text", "length": 250, "mandatory": true } ] } ] }
```

## Use with AI agents

The skill is the `skill/` folder: instructions, rules, recipes and references.

- **GitHub Copilot:** copy `skill/copilot-instructions.md` to `.github/copilot-instructions.md` and keep `skill/` at the repository root. It is short on purpose, so it costs few tokens per request.
- **Claude Code:** copy the `skill/` folder to `%USERPROFILE%\.claude\skills\outsystems-oml\`.
- **Other agents:** give them `skill/SKILL.md` and keep `skill/reference/` readable.

Tell the agent where `oml` and the module file are. No file yet? Open the module in Service Studio and let the agent run `oml pull`. The agent then works in a loop: read the module (`inspect`, `describe`, `find`) → copy a recipe into a spec → `apply` to a new staged file → `validate` and `describe` → for UI, `preview` in light and dark → fix and repeat. It asks you before anything that changes a real module or an environment.

To keep agents from reading the whole spec reference, `oml spec <key>` prints only the section for one key (15-60 lines) and `oml spec check <spec.json>` finds typos, wrong types and bad flow targets in milliseconds, before `apply`. `oml spec schema` writes a JSON Schema for autocomplete in VS Code.

### How to give tasks to an agent

A good task says four things: **which module file**, **what to build or change**, **which elements from other modules to use** (as `Module.Element`), and **what the agent may not do**. The agent cannot guess which action or block of another module you mean, so name it.

```text
Module: C:\Work\Orders.oml. Add an entity Invoice (Number Text 20 mandatory,
Amount Decimal, IssuedOn Date) with Invoice_CreateOrUpdate and Invoice_Delete
server actions, and a Reactive list screen InvoiceList using the module's layout.
Stage the result and validate it. Do not install or publish.
```

```text
In C:\Work\Orders.oml, change the server action Order_Save: before saving,
call Customers_CS.Customer_Validate with the order's CustomerId. The producer
module file is C:\Work\Customers_CS.oml. Show me the changed flow with
oml describe before you install anything.
```

```text
Explain what the screen OrderDetail in C:\Work\Orders.oml does: data it
loads, actions it calls, and who can open it. Do not change anything.
```

```text
Build a reusable block StatusBadge (input Status Text) in C:\Work\Orders.oml:
green for "Paid", orange for "Pending", grey otherwise. Use theme variables.
Preview it in light and dark and at phone width, follow the UI checklist,
and send me the screenshots.
```

```text
The changes in C:\Work\Orders.staged.oml are ready. Compare them with
C:\Work\Orders.oml, summarise what changed, then install after I say yes,
and publish it through Service Studio.
```

```text
Export the exposed REST API OrdersApi of C:\Work\Orders.oml as OpenAPI.
Then list the placeholders I must fill in.
```

```text
Make an architecture report for all .oap files in C:\Exports\Payments-team
and explain the three most important findings.
```

Tips:
- One task per message works best. Big features: split them (data model first, then logic, then screens).
- Say "do not install or publish" when you only want a staged result to review.
- If the agent reports a refusal from the production rules (for example deleting an existing entity), do that step yourself in Service Studio.

## Production rules

Agents must assume that everything already in a module also exists in production, with data and with other modules using it. [skill/rules.md](skill/rules.md) has 11 short rules. `apply` enforces four of them and prints the manual step instead:

- never delete an existing entity (regular or static);
- never rename or delete existing elements, their parameters or attributes;
- never change the type, length, decimals or mandatory setting of an existing attribute;
- never add SQL that changes data (`DELETE`, `UPDATE`, `TRUNCATE`, `DROP`, `ALTER`, `MERGE`).

Elements created in the same spec stay fully editable. Flow nodes, widgets and local variables in existing elements can still change.

## Agents and people

Agents do the work; people make the decisions that touch production.

| Step | Who |
|---|---|
| Read modules, write specs, `apply`, `validate`, `preview`, `architecture`, `openapi` | Agent |
| `install` a staged file over a module | Agent, after you confirm |
| Publish and refresh references | Agent, after you confirm; you approve Service Studio's connection dialog once per session |
| LifeTime tags and deployments | Agent, after you confirm; the token stays in environment variables |
| Delete or rename existing elements, change existing attributes | You, in Service Studio (`apply` refuses) |
| Final design touches, logic review | You |

Not supported: debugging, SOAP, downloading OutSystems UI, Traditional Web screen templates, and ODC (see the roadmap in [Issues](https://github.com/Topurrra/oml-cli/issues)).

## More

- [skill/reference/commands.md](skill/reference/commands.md): every command with options and examples.
- [skill/reference/spec.md](skill/reference/spec.md): every spec key.
- [skill/reference/ui-checklist.md](skill/reference/ui-checklist.md): what agents check before UI work is done.
- [CONTRIBUTING.md](CONTRIBUTING.md): build, tests, project layout, releasing.

`oml` is released under the [MIT License](LICENSE). It uses your locally installed Service Studio assemblies and does not ship them. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Not affiliated with OutSystems.
