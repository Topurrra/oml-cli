# codemode-runtime

**One tool for any agent: `execute(code)`.** The agent writes a short Python
program. The program calls your tools. Only its last value comes back.

An agent that calls tools one at a time pays a round trip per call and reads
every result. With code mode it writes one program per step instead: the
program chains calls, runs them in parallel, keeps the intermediate data to
itself and returns a few lines. This is a small runtime for that.

- **Any agent, any model.** It is a command line and an MCP server.
- **Any project.** It uses the MCP servers a project already has (stdio,
  streamable HTTP, SSE), or the project's own command lines.
- **A gateway you control.** The program runs in a separate process with no
  credentials. Every tool call is checked on the host against a
  deny / ask / allow policy and written to a log.
- **Approvals that do not block.** A call that needs a person pauses the
  run. After approval the program is replayed from the log, so nothing runs
  twice.
- **Nothing to install.** Python 3.9 or newer, standard library only.

## Try it

```text
python main.py          # a demo: no configuration, no network, no model
```

After `pip install .` the same program is the `codemode` command, which the
examples below use. `python main.py ...` does the same without installing.

```text
codemode run "notes = tools.demo.list_notes(); len(notes)"        # result: 10
codemode run "tools.demo.create_note(title='Hi', body='There')"   # a write: it pauses, exit 3
codemode approve last                                             # you approve, it runs
codemode show last                                                # the audit log
codemode verify last                                              # replay from the log and compare
```

## Use it in your project

```text
cd your-project
codemode init            # writes codemode.json from .mcp.json, checks each server, updates .gitignore
codemode doctor          # checks everything a program needs
codemode instructions    # prints the block to paste into AGENTS.md or CLAUDE.md
```

`init` starts from the MCP server list the project already has. Reads are
allowed, everything else asks first, deletes never run, and no credential is
copied into the file. A project with no MCP server declares its commands as
tools instead. [`docs/ADOPTING.md`](docs/ADOPTING.md) is the full guide.

The agent reaches the runtime in one of two ways:

- **As an MCP server**, which is the better one. Register it like any stdio
  server and the agent sees a single tool, `execute`:

  ```json
  {"mcpServers": {"codemode": {"command": "codemode", "args": ["serve"]}}}
  ```

- **From the command line.** `codemode run --json -f program.py` prints one
  JSON object and exits with a code that says what happened.

## What a program looks like

Ordinary Python. The value of the last expression is the result.

```python
notes = tools.demo.list_notes()
bodies = parallel([("demo.read_note", {"id": n["id"]}) for n in notes])
state["open_actions"] = [
    line[7:].strip() for b in bodies for line in b["body"].splitlines() if line.startswith("ACTION:")
]
len(state["open_actions"])          # the only thing the model reads
```

Eleven tool calls, one round trip, and about 4.7 kB of note text that never
reaches the model's context.

| Name | What it does |
|---|---|
| `tools.<namespace>.<tool>(**arguments)` | Call a tool. Returns a plain Python value. |
| `parallel([("namespace.tool", {...}), ...])` | Run independent calls at the same time. |
| `search("words")`, `describe("namespace.tool")` | Find a tool; read its signature, schema and policy. |
| `grep`, `tail`, `summarize`, `shape` | Cut long output down inside the program. |
| `state`, `args`, `project_root` | A dict that persists between programs; the arguments this one was started with; the project's folder. |
| `helpers.<module>.<function>()` | The project's own helper functions. |
| `ToolError` | Raised when a call is denied, fails or is unknown. |

The whole list is in [`docs/INTERFACE.md`](docs/INTERFACE.md).

## Configuration

One JSON file, `codemode.json`, in the project root:

```json
{
  "connectors": {
    "files":   {"type": "mcp-stdio", "command": ["npx", "-y", "@modelcontextprotocol/server-filesystem", "."]},
    "tracker": {"type": "mcp-http", "url": "https://tracker.example.com/mcp",
                "headers": {"Authorization": "Bearer ${TRACKER_TOKEN}"}},
    "ci":      {"type": "commands", "tools": {"test": {"command": ["pytest", "-q"]}}}
  },
  "policy": {
    "default": "ask",
    "allow": ["files.read_*", "files.list_*", "tracker.get_*", "ci.test"],
    "deny":  ["*.delete_*", {"tool": "files.write_file", "args": {"path": "*.env"}}]
  },
  "limits": {"timeout_s": 600, "call_timeouts": {"ci.test": 900}}
}
```

- **`connectors`** are where tools come from. Each name is a namespace in
  programs and in policy rules.
- **`policy`** decides each call: deny wins over ask, ask over allow, and
  what no rule names is asked about.
- **Secrets** are written as `${NAME}` and read from the environment when a
  server starts. They never reach the program.

Every key is described in [`docs/ADOPTING.md`](docs/ADOPTING.md#the-config).

## The safety model, and its limit

What the runtime enforces:

- The program starts with a scrubbed environment and no credentials. It can
  use a tool; it cannot read the token behind it.
- Every tool call is decided on the host, at the moment it is made, with
  its real arguments.
- A call the policy asks about does not run until a person approves it from
  the command line, at a terminal. A program and the MCP tool cannot
  approve, change the policy or write the log. A deny rule beats any
  approval.
- A person may approve several calls of one execution in advance
  (`approve --same-tool`, `approve --all-remaining`). That is the one case
  where a call the policy asks about runs without having been shown to
  anyone. A project can narrow it or turn it off with `approval.standing`.
- Every execution has a time limit, a call limit and size limits, and the
  log names each side effect before it happens.

**What it does not do: the default sandbox is a process, not a jail.** It
runs as your user. A hostile program can open files and sockets by itself,
without going through a tool. For code you do not trust, run the sandbox in
a container, where the tools are then the only way out:

```json
{"executor": {"command": ["docker", "run", "--rm", "-i", "--network", "none", "--read-only",
                           "python:3.12-alpine", "python", "-I", "-u", "-c", "{bootstrap}"]}}
```

The Docker line is an example and was not run in this project's tests; the
mechanism behind it was. "At a terminal" stops an agent's shell tool, not a
tool that types into a real terminal. Both limits are spelled out in
[`docs/ADOPTING.md`](docs/ADOPTING.md#policy).

## Outcomes and exit codes

Every surface returns the same object:

```text
{"status": "completed", "executionId": "...", "result": ...}
{"status": "paused",    "executionId": "...", "pending": [{"seq": 2, "tool": "...", "args": {...}}]}
{"status": "error",     "executionId": "...", "error": {"type": "...", "message": "...", "hint": "..."}}
{"status": "running",   "executionId": "...", "progress": {...}}
```

Exit codes: `0` completed, `1` failed or rejected, `2` usage or config
error, `3` paused, `4` verify mismatch, `5` not found, `6` still running.
Every error says what to do next in `error.hint`.

## Documentation

| Document | For |
|---|---|
| [`docs/ADOPTING.md`](docs/ADOPTING.md) | Setting it up in a project: the config, the policy, long programs, many agents. |
| [`docs/INTERFACE.md`](docs/INTERFACE.md) | The reference: commands, JSON fields, exit codes, the `execute` tool. |
| [`docs/SERVER_AUTHORS.md`](docs/SERVER_AUTHORS.md) | Writing MCP tools that are good to program against. |
| [`docs/DESIGN.md`](docs/DESIGN.md) | Why each part exists. |
| [`docs/VERIFICATION.md`](docs/VERIFICATION.md) | What was tested, on which platform, and what was not. |
| [`docs/FEEDBACK_TRIAGE.md`](docs/FEEDBACK_TRIAGE.md) | Two field reports, point by point, and what was done about each. |
| [`AGENTS.md`](AGENTS.md) | Changing this code: the rules, the commands, where things live. |

## Tests

```text
python tests/run_all.py
```

It runs the demo, the unit cases, the end-to-end suite (every check starts
the real program as a subprocess) and a mutation check that plants bugs and
makes sure the suites catch them. Tested on Windows and Linux; macOS shares
the Linux code path and has not been run. The numbers are in
[`docs/VERIFICATION.md`](docs/VERIFICATION.md).

## Not built

Rollback of calls that already ran, wake-ups when a background program
ends, OAuth for remote MCP servers, and coordination between agents. File
tools are left to a filesystem MCP server. The reasons are in the "Deferred"
table of [`docs/VERIFICATION.md`](docs/VERIFICATION.md).

## Where the design comes from

Six sources; [`docs/DESIGN.md`](docs/DESIGN.md) lists what was taken from
each and where they disagree.

- Claude Code documentation, [Code Mode](https://code.claude.com/docs/en/code-mode)
- Cloudflare Agents documentation, [Code Mode](https://developers.cloudflare.com/agents/tools/codemode/)
- [Using a chaos loop to harden the Claude Docs launch](https://claude.dev/blog/chaos-loop-claude-docs-launch/)
- [How the Claude Code team thinks about verification](https://claude.dev/blog/how-the-claude-code-team-thinks-about-verification/)
- [Safely scaling multi-agent teams with Claude Fable 5.2](https://claude.dev/blog/safely-scaling-multi-agent-teams-with-fable-5-2/)
- [Unharnessed](https://github.com/unharnessed-agent/Unharnessed)

## License

MIT. See [`LICENSE`](LICENSE).
