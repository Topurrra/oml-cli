# oml

**OutSystems 11 from the command line, built for AI agents.**

`oml` reads, changes, checks and reviews OutSystems 11 modules through the typed Model API of your installed Service Studio, and moves Traditional Web modules to Reactive (`oml migrate`). It never edits OML XML. You use it yourself, or give its skill to any AI agent.

Website: [topurrra.github.io/oml-cli](https://topurrra.github.io/oml-cli/) · [Releases](https://github.com/Topurrra/oml-cli/releases) · [Everything oml does](oml-full.md) · [Benchmarks](BENCHMARKS.md) · [Changelog](CHANGELOG.md)

## Why oml

Agents that change OutSystems modules need to be cheap, fast and safe. Compared with Service Studio's own MCP server and the official O11 skill (as of November 2026):

| | OutSystems MCP | oml |
|---|---|---|
| Works on | Modules open in Service Studio (newer builds can find and open them) | Module files: many modules, many agents, Studio may be closed for reading and checks |
| A change | Goes into a working copy that Studio's Compare-and-Merge shows. No production rules | Goes into a staged copy. Production rules are enforced, you review it, then it is installed with a backup |
| Token use | A 70 KB skill to read first, then one call per read or C# change | A 3.3k-token skill, compact JSON, short `describe` answers, and `oml code`: one program runs many calls and returns only the answer |
| Parallel agents | One edit session per module: a second agent waits | Each agent has its own file; one command merges the results |
| Checks and review | Studio's TrueChange messages and Compare-and-Merge | 36 AI Mentor-like rules, a plain-words review of two versions, architecture maps |
| Server versions | The latest version only (Studio 11.55.98+) | Pull, compare and check any version of any module, with scores over time |
| Running app | Not included | Opens screens in a browser and reports what users see, at every width |
| More | Any Model API change through C# code | Traditional Web to Reactive migration, LifeTime deploys, project memory, offline OutSystems docs search, test data, mocks |

oml uses Service Studio's MCP server too: for pull, publish, Refresh All and live queries; the ones that change something after you confirm.

## Benchmarks

Same task, same module, same model:

| Task | OutSystems MCP | oml |
|---|---|---|
| Small: entity, CRUD actions, list screen | 611k input tokens, 185 s | **207k input tokens, 56 s** |
| 24 UI components, 1 agent | $0.94, 58 turns | **$0.55, 37 turns** |
| 24 UI components, 8 agents | 20/24 built, $3.16 | **24/24 built, $1.17** |
| OUI wizard, 11 blocks, with `oml code` | $1.51, 13.0M tokens, 45 inputs wrong | **$0.76, 6.1M tokens, all inputs right** |

Details and how to re-run them: [BENCHMARKS.md](BENCHMARKS.md).

## Install

You need Windows, Service Studio 11 and the .NET 10 runtime (Service Studio 11.55.94 and later install it). Publish and Refresh All need Service Studio 11.55.91+ with its MCP server started (**Edit > MCP Server > Start MCP server**).

- **Release:** download `oml-<version>-win-x64.zip` from [Releases](https://github.com/Topurrra/oml-cli/releases), unzip it and add `bin` to PATH. The newest features (`oml code`, `oml mcp`, `oml server`) come with the next release; until then, build from source.
- **From source:**

  ```powershell
  dotnet build -c Release src/OmlCli.csproj
  $env:PATH = "$PWD\src\bin\Release\net10.0;$env:PATH"
  ```

Then run `oml doctor`: it checks your setup and says what to fix. `oml code` also needs Python 3.9+.

## Quick start

```powershell
oml create orders --in C:\Exports\Orders.oml   # a project folder: modules, skill, settings, docs
cd orders
oml inspect modules\Orders.oml --own --only entities,actions,screens
oml apply modules\Orders.oml specs\change.json staged\Orders.oml
oml review modules\Orders.oml staged\Orders.oml --out review.html
oml install staged\Orders.oml modules\Orders.oml
```

A minimal `specs\change.json`:

```json
{ "entities": [ { "name": "ReviewNote", "attributes": [ { "name": "Text", "length": 250, "mandatory": true } ] } ] }
```

`apply` never changes the source file. `install` replaces it only when the staged file has zero validation errors, and keeps a backup. With `oml config set autoApprove.install on`, `apply` without the staged path does both in one step: a backup, then the module itself, only when the change adds no errors. No module file yet? Open the module in Service Studio and run `oml pull`, or get the published version with `oml server pull`.

Not a terminal person? Type `oml` for a menu that shows each command it runs, or `oml dashboard` for a local web page.

## Use with AI agents

Put the `skill/` folder in your project (`oml create` does it). It works with Claude Code, GitHub Copilot, Cursor, Codex, Gemini CLI and any agent that reads `AGENTS.md`. The default load is about 3.3k tokens; the rest is read only when a task needs it.

The agent works in a loop: read the module, copy a recipe into a spec, `apply` to a staged file, `validate`, fix and repeat. It asks you before anything that touches a real module or environment.

For many calls, the agent writes one program instead of many tool calls:

```python
# oml code run --json -f check.py
mods = ["Orders.oml", "Orders_CS.oml", "Customers_CS.oml"]
found = parallel([("oml.read", {"args": ["check", m]}) for m in mods])
[helpers.oml.out(r)["summary"] for r in found]
```

Reads and staged writes run at once. Install, publish, references, pull, server and LifeTime calls pause until a person approves. Give a good task four things: the module file, what to change, which elements of other modules to use (`Module.Element`), and what the agent may not do.

Setup per harness, task examples and the agent hooks: [oml-full.md](oml-full.md#ai-agents).

## Safety

Agents must assume everything in a module is already in production. `apply` always refuses to:

- delete an existing entity or attribute, change an attribute's data type, or rename an attribute;
- make an entity again (deleted and created with the same name) or copy one from another module: both make a new, empty table.

It prints the manual step instead, for you to do in Service Studio. `apply` saves only after your yes (`needsApproval`, then `--user-approved`): a delete of any other existing element, a shorter length, fewer decimals, a new mandatory setting or identifier, and SQL that changes data (`DELETE`, `UPDATE`, `DROP`, ...). Renames and public signature changes save; the report lists what breaks for the modules that use them. Install, publish and deploy ask a person first. All 11 rules: [skill/rules.md](skill/rules.md).

## What it covers

| Area | Commands |
|---|---|
| Read a module | `inspect`, `describe`, `find`, `query`, `flow`, `tree`, `export` |
| Change a module | `apply`, `validate`, `install`, `new`, `spec`, `translations` |
| Check and review | `check`, `review`, `architecture` |
| Platform server | `server modules`, `server pull`, `server check`, `review --server` |
| Service Studio and LifeTime | `pull`, `publish`, `references`, `studio`, `lifetime` |
| Migration | `migrate plan`, `migrate status`, `migrate check` |
| Running app | `browser page`, `look`, `sweep`, `run`, `record`, `diff` |
| Tests | `test bdd`, `test scenarios`, `test data` |
| Whole applications | `map`, `mock` |
| Agents | `code`, `mcp`, `batch`, `memory`, `learn`, `hooks`, `doc` |
| For people | `menu`, `dashboard`, `menu explorer`, `dashboard --change` |
| Packages and assets | `app`, `extension`, `font`, `image` |

Every feature and use case in short: [oml-full.md](oml-full.md). Every command with its options: [commands.md](skill/reference/commands.md). Every spec key: [spec.md](skill/reference/spec.md).

Not supported: debugging, SOAP, and ODC (use OutSystems' ODC MCP server for ODC).

## License

MIT, see [LICENSE](LICENSE). `oml` uses your installed Service Studio assemblies and does not ship them; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Not affiliated with OutSystems. Build, tests and releases: [CONTRIBUTING.md](CONTRIBUTING.md).
