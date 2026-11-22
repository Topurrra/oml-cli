# Benchmark: OutSystems MCP side against the oml sides

This folder holds the scripts behind the benchmark numbers in [BENCHMARKS.md](../../BENCHMARKS.md). Anyone can run them and get the same kind of result. Each run uses real Claude API usage, so it costs money.

## What it measures

The same task is given to two or three setups:

- **mcp**: Claude Code with the OutSystems MCP server (Service Studio's MCP server) and the official `servicestudio-mcp-oml` skill from [OutSystems/outsystems11-mcp](https://github.com/OutSystems/outsystems11-mcp). The agent works on the module that is open in Service Studio.
- **oml**: Claude Code with the `oml` command line tool and the skill in this repository's `skill/` folder. The agent works on a copy of the module file.
- **omlcode**: the same as oml (same CLI, same skill, same task), plus one line in the preamble: do multi-step oml work as Python programs run with `oml code run --json -f <program.py>` (oml's code mode, `oml doc commands#code-mode`). It needs Python 3.9 or newer; check with `oml code doctor` in an empty folder. The skill also mentions code mode, so the oml side may use it too; the omlcode side is told to.

For each run the script records cost, time, turns, input tokens (including cache reads and writes), output tokens, tool calls and failed tool calls, agent errors and the final text of each agent. A second script, `verify.ps1`, checks the module with `oml validate` and counts its blocks and screens.

Two modes:

- **single**: one agent does the whole task.
- **swarm**: the numbered components of the task are split over several agents that run at the same time. Then one assembly agent builds the screen and the rest of the task.

## Cost warning

Every run calls the Claude API and is billed by Claude Code. A swarm run starts several agents at once. The published large-task runs cost between $0.55 and $3.16 each (see [BENCHMARKS.md](../../BENCHMARKS.md)). Start with `-DryRun` to check the setup. A real run asks you to type `yes` first; use `-Yes` only when you have already checked the plan.

## Prerequisites

- Windows with PowerShell 7 (`pwsh`). The scripts use no other language.
- Claude Code CLI installed and signed in. Check with `claude auth status`.
- A built `oml.exe`. Build it from source as described in the main `CONTRIBUTING.md`, or use a release zip.
- For the mcp side: Service Studio 11.55.93 or later, with the MCP server started (**Edit > MCP Server > Start MCP server**) and the target module open. The default port is 41820. Set the `OML_STUDIO_PORT` environment variable if your port is different.
- For the mcp side, either a local copy of the `servicestudio-mcp-oml` skill folder (`-SkillPath`), or permission to clone the OutSystems repository (`-AllowDownload`). The script prints the URL before it clones anything.
- Git, only if you use `-AllowDownload`.

## Before you start: fairness rules

A result is only useful if both sides start from the same place.

1. **Start both sides from the same module.** Save the module to an `.oml` file. Give that file to `-Module`. The script copies it to `source.oml` at the start of the run. Open the same module in Service Studio for the mcp side, and do not change it before the run.
2. **The module must not already contain the target elements.** For the showcase task, no `Fx*` blocks and no `Showcase` screen. For the CRUD task, no `Supplier` entity and no `SupplierList` screen. For the OUI task, no `Oui*` element and nothing copied from OUI (no `Acme*` kit blocks either). Remove them first. A run on a module that already has them is not valid.
3. **Give each side its own official skill.** The oml and omlcode sides get `skill/` from this repository automatically. The mcp side needs `-SkillPath` or `-AllowDownload`.
4. **Use the same model** on both sides (`-Model`, default `claude-haiku-5-5`) and the same task file.
5. **Run one thing at a time on the machine.** Do not run other heavy jobs while a benchmark runs. Sides run one after another, not in parallel.
6. **Use a new `-Out` folder for every run.** The script never overwrites results.
7. **Repeat the runs** and report every run, not only the best one. Each row in a result table is one run.

## Step-by-step

Run every command from the repository root. Use a results folder outside the repository. A real run refuses an `-Out` folder inside a project (a dry run only warns), because Claude Code would load that project's instructions and skills into the run.

### 1. Dry run (no cost, no network)

Creates the run folders and prompts and prints the exact commands. It runs nothing and downloads nothing.

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/showcase-24.md -Side both -Mode single -Module 'C:\bench\start\OML-parallel.oml' -OmlExe 'C:\tools\oml\oml.exe' -Out 'C:\bench\dry-showcase-single' -DryRun
```

### 2. Single mode

Both sides, one agent each:

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/showcase-24.md -Side both -Mode single -Module 'C:\bench\start\OML-parallel.oml' -OmlExe 'C:\tools\oml\oml.exe' -SkillPath 'C:\skills\servicestudio-mcp-oml' -Out 'C:\bench\run-01-single'
```

Only the oml side (no Service Studio needed):

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/showcase-24.md -Side oml -Mode single -Module 'C:\bench\start\OML-parallel.oml' -OmlExe 'C:\tools\oml\oml.exe' -Out 'C:\bench\run-01-oml-single'
```

Only the mcp side (Service Studio open with the module, MCP server started). Use `-AllowDownload` instead of `-SkillPath` to clone the skill:

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/showcase-24.md -Side mcp -Mode single -SkillPath 'C:\skills\servicestudio-mcp-oml' -Out 'C:\bench\run-01-mcp-single'
```

### 3. Swarm mode

The components are split over 8 agents that run in parallel. Then one assembly agent runs. Use the same `-Module` for the oml side.

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/showcase-24.md -Side both -Mode swarm -Agents 8 -Module 'C:\bench\start\OML-parallel.oml' -OmlExe 'C:\tools\oml\oml.exe' -SkillPath 'C:\skills\servicestudio-mcp-oml' -Out 'C:\bench\run-01-swarm'
```

The task file `crud-small.md` has no numbered component list, so use `-Mode single` for it.

### OUI subset: oml code against the MCP

`tasks/oui-subset.md` asks for 11 OUI blocks and the page template `OuiTplWizard`, described in words. The agents never see the OUI kit: run folders are outside the repository and nothing in the prompt points at `oui/`. The result is compared with the kit's own build of the same subset (step 4).

The start module is a blank Reactive module that both sides can use:

```powershell
oml new C:\bench\oui-20261123\start\OuiBench.oml --name OuiBench --kind CrossDevice
```

The reference is the kit's build of the subset on a copy of that module. `oui-reference.py` runs `oui/oui-kit/ouikit.py` with one change: the chosen template comes alone, without the other templates its menu links to. The module has no OutSystems UI, so the theme is made with `--theme` and an empty current style sheet, and the spec gets `"module": {"defaultTheme": "BenchTheme"}` (the task asks the agents for the same theme):

```powershell
python tests/bench/oui-reference.py --screens OuiTplWizard --theme BenchTheme --current empty.css --out <spec folder>
oml apply C:\bench\oui-20261123\start\OuiBench.oml <spec folder>\spec.json C:\bench\oui-20261123\reference.oml
```

Keep the spec folder away from the run folders, so an agent cannot find it.

The omlcode side:

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/oui-subset.md -Side omlcode -Mode single -Module 'C:\bench\oui-20261123\start\OuiBench.oml' -OmlExe 'C:\tools\oml\oml.exe' -Out 'C:\bench\oui-20261123\run-omlcode'
```

The mcp side: copy the start file to a working file (`C:\bench\oui-20261123\mcp-work\OuiBench.oml`), open that copy in Service Studio (**File > Open**), start the MCP server, then:

```powershell
pwsh -NoProfile -File tests/bench/bench.ps1 -Task tests/bench/tasks/oui-subset.md -Side mcp -Mode single -SkillPath 'C:\skills\servicestudio-mcp-oml' -StudioModule OuiBench -Out 'C:\bench\oui-20261123\run-mcp'
```

`-StudioModule` names the target module in the prompt and tells the agent never to change another open module; `-KeepModules OUI` names those modules too. An agent can list files outside its folder: keep the reference, the spec folder and the other side's run folder where it cannot find them (the omlcode agent of the first run listed `C:\bench` and saw `reference.oml`, without opening it).

When it ends, save the module in Service Studio (Ctrl+S writes the working file, so the start file stays as it was), then run `verify.ps1` and `compare.ps1` on the working file. `-Side code` runs both sides, mcp first.

### 4. Check the result

`bench.ps1` runs `verify.ps1` on the oml result for you. To check any module again, or to check the mcp result after you save the module from Service Studio, run:

```powershell
pwsh -NoProfile -File tests/bench/verify.ps1 -Oml 'C:\tools\oml\oml.exe' -Module 'C:\bench\run-01-single\oml\result.oml' -Expect 'Fx*:24','screens:Showcase:1'
```

`-Expect` rules: `Pattern:N` counts blocks. `section:Pattern:N` counts another section: `blocks`, `screens`, `entities`, `structures`, `serverActions`, `clientActions` or `actions` (server and client actions). Patterns may use `*`. The exit code is `0` when there are no validation errors and every rule matches, `1` when not, and `2` when the check cannot run.

Each task file has its own `Expect:` line, so `bench.ps1` checks the same rules every time.

To compare a result with a reference module element by element (the OUI task), run `compare.ps1`. It is read only:

```powershell
pwsh -NoProfile -File tests/bench/compare.ps1 -Oml 'C:\tools\oml\oml.exe' -Reference 'C:\bench\oui-20261123\reference.oml' -Result 'C:\bench\oui-20261123\run-omlcode\omlcode\result.oml' -OutFile 'C:\bench\oui-20261123\run-omlcode\compare.json'
```

It prints the validation counts of both modules, then per reference block (`-Pattern`, default `Oui*`): present, inputs with the same name and type, events, placeholders, and the differences (type, mandatory, default, event parameters, extra inputs). Per screen: present, access, and which of those blocks it uses. The score is the share of reference items the result has.

## Output

Each run writes to the folder you pass in `-Out`:

```
results.md          tables for the README: summary, phases, agents, skills
results.json        the same data, for scripts
source.oml          copy of -Module at the start of the run
skill-download/     only when the skill was cloned
single/ or swarm:   one folder per side (mcp, oml or omlcode)
  prompt.md         the exact prompt the agent got
  mcp.json          the MCP server setting for that side (empty on the oml side)
  .claude/skills/   the skill that side used
  result.json       the full output of claude -p (cost, usage, final text)
  stderr.txt        errors printed by claude -p
  result.oml        the module the agent wrote (oml and omlcode sides)
  verify.txt, verify.json   the verify.ps1 output (oml and omlcode sides)
  .oml/codemode/    the code mode config and program records (omlcode side, when the agent used it)
  a1 ... a8, asm/   one folder per agent in swarm mode
```

The summary has cost, time, turns, input tokens, output tokens, tool calls and failed calls, agent errors and the verify result. "Input tokens" is the sum of uncached input, cache reads and cache writes. The per-agent table shows each part.

## Task files

Files are in `tests/bench/tasks/`:

- `crud-small.md`: an entity with two server actions and a list screen. Single mode only.
- `showcase-24.md`: 24 UI components as blocks, plus one screen. Single or swarm mode.
- `oui-subset.md`: 11 OUI blocks and the page template `OuiTplWizard`, described in words; compare the result with the kit's build (`compare.ps1`). Single or swarm mode.

The script reads a task file like this:

- Text before the first numbered line is shared. Every agent gets it.
- Lines `1.` to `N.` are the components. Swarm mode splits them over the agents. The numbers must run 1 to N with no other text between them.
- Text after the last numbered line is for the assembly agent only (in swarm mode) or is part of the one prompt (in single mode).
- A line that starts with `Expect:` is not sent to the agents. It lists the rules that `verify.ps1` checks.

## Limits and notes

- **Tool calls are counted from the session transcript.** After each agent, the script reads the agent's Claude Code transcript (`<config dir>/projects/.../<session id>.jsonl`; the config dir is `CLAUDE_CONFIG_DIR` or `~/.claude`) and counts tool calls, failed tool calls (results marked as errors, which includes a shell command that exits non-zero) and `oml code run` calls. When the transcript is not found the columns say n/a. The agents also report failed calls in their final text, saved in `results.json`.
- **The mcp side needs a person.** Keep Service Studio open, and approve the connection dialog if one appears. The script cannot do this for you.
- **The mcp result is not checked by the script.** Save the module from Service Studio to a file (`oml pull <Module> --out <file>`), and run `verify.ps1` (and `compare.ps1` for the OUI task) on that file.
- **User-level files may still load.** `--setting-sources project` leaves out your user settings. Your user-level memory or instructions may still load. Both sides see the same setup, so the comparison is still fair. Note this when you publish a result.
- **Secrets.** The agents are told never to write a session token into a file or a reply. The script also removes values after token-like keys before it saves `results.json` and `results.md`. This is best effort. Check a run folder before you share it, because `result.json` holds the full agent output.
- **Downloads.** `-AllowDownload` clones the public OutSystems repository into `-Out`. The script runs nothing from it. It copies only the skill folder.
- **Timeouts.** If `MCP_TOOL_TIMEOUT` is not set, the script sets it to 300000 (5 minutes) for the run and restores it afterwards.
- **Stopping a run.** Press Ctrl+C. The script stops its own background jobs. Check Task Manager for any `claude` process that is still running.

## Files

| File | Purpose |
|---|---|
| `bench.ps1` | Runs the benchmark. Writes `results.md` and `results.json`. |
| `verify.ps1` | Checks an `.oml` file with `oml validate` and `oml inspect`. Read only. |
| `compare.ps1` | Compares a result module with a reference module, block by block. Read only. |
| `oui-reference.py` | Builds the OUI kit spec for one page template alone, for the OUI reference. |
| `tasks/crud-small.md` | Small task: entity, two server actions, list screen. |
| `tasks/showcase-24.md` | Large task: 24 components, one screen, theme and font picker. |
| `tasks/oui-subset.md` | OUI task: 11 blocks and one page template, compared with the kit's build. |
