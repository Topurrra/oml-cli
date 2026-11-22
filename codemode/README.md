# codemode-runtime

**One tool for any agent: `execute(code)`. Every tool call the code makes
crosses a gateway you control.**

Give an agent a list of tools and it makes one call per round trip, and every
result lands in its context. Give it a program instead and it can chain calls,
run them in parallel, keep the intermediate data to itself and hand back two
lines. That is code mode. This project is a small, portable runtime for it:

- **Any agent, any model.** It is an MCP server and a CLI. Nothing in it knows
  which model is on the other side.
- **Any project.** Point it at the MCP servers a project already has (stdio,
  streamable HTTP, SSE), or declare the project's own command lines as tools.
  `codemode init` writes the config from the project's server list.
- **A real boundary.** The program runs in a separate process with no
  credentials. Each tool call is checked on the host against a
  deny / ask / allow policy, bounded by limits, and written to an append-only
  log.
- **Approvals that do not block.** A call that needs a human pauses the
  execution. After approval the same program is replayed from the log, so
  nothing runs twice.
- **Work that is not lost.** A program that fails keeps its variables, and a
  corrected program continues from the calls that already succeeded.
- **Nothing to install.** Python 3 standard library only, on Windows, Linux
  and macOS.

```text
python main.py
```

runs a demo with no configuration, no network and no model.

## Contents

- [Quick start](#quick-start)
- [Use it in your project](#use-it-in-your-project)
- [What a program can do](#what-a-program-can-do)
- [Using it from an agent](#using-it-from-an-agent)
- [Configuration](#configuration)
- [The safety model, and its limit](#the-safety-model-and-its-limit)
- [Outcomes and exit codes](#outcomes-and-exit-codes)
- [Tests](#tests)
- [Extending it](#extending-it)
- [Where the design comes from](#where-the-design-comes-from)
- [Not built](#not-built)

Other documents:

| Document | For |
|---|---|
| [`docs/ADOPTING.md`](docs/ADOPTING.md) | Setting the runtime up in a project, with the instructions block for the project's agents. |
| [`docs/SERVER_AUTHORS.md`](docs/SERVER_AUTHORS.md) | Writing MCP tools that are good to program against. |
| [`docs/INTERFACE.md`](docs/INTERFACE.md) | The reference: commands, JSON fields, exit codes, the `execute` tool. |
| [`docs/FEEDBACK_TRIAGE.md`](docs/FEEDBACK_TRIAGE.md) | The first field report, point by point, and what was done about each. |
| [`docs/DESIGN.md`](docs/DESIGN.md), [`docs/VERIFICATION.md`](docs/VERIFICATION.md) | Why each part exists; what was run and where. |
| [`AGENTS.md`](AGENTS.md) | Rules for changing this code. |

## Quick start

Python 3.9 or newer. There are no dependencies; `requirements.txt` is empty on
purpose.

```text
python main.py                      # the demo: 7 steps, 13 self-checks
python main.py --help
```

Installed (`pip install .`), the same program is the `codemode` command. The
examples below use that name; `python main.py ...` and
`python -m codemode_runtime ...` are the same thing.

Run a program against the built-in notes store:

```text
codemode run "notes = tools.demo.list_notes(); len(notes)"
codemode run -f my_program.py --json
```

A write is not on the demo's allow list, so it pauses:

```text
codemode run "tools.demo.create_note(title='Hi', body='There')"   # exit 3, status paused
codemode approve last                                             # runs it, exit 0
codemode show last                                                # the audit log
codemode verify last                                              # replay from the log, compare
```

## Use it in your project

```text
cd your-project
codemode init          # writes codemode.json from .mcp.json, checks every server, updates .gitignore
codemode doctor        # checks everything a program needs
codemode instructions  # prints the block to paste into AGENTS.md or CLAUDE.md
```

`init` starts from the MCP server list the project already has. Reads are
allowed, everything else asks first, and no credential is copied into the
file. A project with no MCP server declares its commands instead:

```json
{"connectors": {"ci": {"type": "commands", "tools": {
  "test": {"command": ["pytest", "-q", {"if": "only", "then": ["-k", "{only}"]}],
           "params": {"only": {"type": "string"}}, "timeout_s": 900}}}}}
```

[`docs/ADOPTING.md`](docs/ADOPTING.md) is the full guide.

## What a program can do

The program is ordinary Python. These names are defined for it:

| Name | What it does |
|---|---|
| `tools.<namespace>.<tool>(**arguments)` | Call a tool. Returns a plain Python value. |
| `call("namespace.tool", {...})` | The same, by name. `on_error="return"` gives a failure back as a value. |
| `parallel([("namespace.tool", {...}), ...])` | Run independent calls at the same time. Results come back in order. |
| `repeat("namespace.tool", {...}, n=10)` | Run one call n times; get back only the failures. |
| `search("words")` | Find tools, snippets and helpers. One-line signatures; `.more` says how many were left out. |
| `describe("namespace.tool")` | Signature, description, schema and policy for one tool. |
| `shape(value)` | The keys and types of a value, without its content. |
| `grep(text, pattern)`, `tail(text, n)`, `head(text, n)`, `summarize(text)` | Cut long text output down. |
| `state` | A dict that persists between programs in the same session. |
| `args` | The arguments this program was started with (`run --arg name=value`). |
| `step("name", fn)` | Record the value of `fn()`: use it for time, randomness, anything that changes between runs. |
| `run("snippet", **args)` | Run a saved snippet. |
| `helpers.<module>.<function>()` | The project's own helper functions, from a folder it names in its config. |
| `ToolError` | Raised when a call is denied, fails or is unknown. Has `.kind`, `.message` and `.hint`. |

The value of the last expression, at the top level, is the result. `print()`
goes to the logs. Calls are synchronous: there is no `await`.

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

## Using it from an agent

### Over MCP (any MCP client)

Register the server the way your client registers any stdio MCP server:

```json
{
  "mcpServers": {
    "codemode": {"command": "codemode", "args": ["serve"]}
  }
}
```

The agent sees one tool, `execute`. It takes `code` to run a program, or
`executionId` to ask about an earlier one: its outcome after a human approved
it, its progress while it runs in the background, one part of a large result.
There is no approve tool: approval is a CLI command, for a person at a
terminal.

This is the better surface for an agent. A step is one tool call, the program
travels as JSON with no shell in between to change its backslashes, and the
tool servers stay up between programs.

### Over the CLI (any agent that can run a command)

```text
codemode run --json -f program.py
codemode run --json -f program.py --arg app=web        # the program reads args["app"]
codemode get --json <executionId>
```

One JSON object on stdout, and an exit code that says which outcome happened.
A program that outlasts the caller's shell is started with `--detach` and
followed with `wait`:

```text
codemode run --detach --json -f program.py      # returns the id at once
codemode wait <executionId> --timeout 100       # exit 6 while it is still running
codemode cancel <executionId>
```

`codemode prompt` prints the tool description, for a harness that registers
the tool itself.

### With the built-in loop (any OpenAI-compatible endpoint)

```text
CODEMODE_BASE_URL=http://localhost:11434/v1 CODEMODE_MODEL=<model name> codemode agent "Summarise the open actions"
```

`CODEMODE_API_KEY` is sent as a bearer token if set. Hosted APIs, gateways
and local servers that speak the chat-completions format all work; the loop
uses `urllib` and no SDK. It stops at `--max-turns` (12 by default).

## Configuration

One JSON file. The runtime looks at `--config`, then `$CODEMODE_CONFIG`, then
`codemode.json` in the current folder and in the folders above it (up to the
repository root), and falls back to the built-in demo config. Unknown keys
are errors. See [`codemode.example.json`](codemode.example.json);
`codemode config` prints what was resolved, with credentials hidden.

```json
{
  "connectors": {
    "files":   {"type": "mcp-stdio", "command": ["npx", "-y", "@modelcontextprotocol/server-filesystem", "."]},
    "tracker": {"type": "mcp-http", "url": "https://tracker.example.com/mcp",
                "headers": {"Authorization": "Bearer ${TRACKER_TOKEN}"}},
    "ci":      {"type": "commands", "tools": {"test": {"command": ["pytest", "-q"]}}}
  },
  "policy": {
    "mode": "enforce",
    "default": "ask",
    "allow": ["files.read_*", "files.list_*", "tracker.get_*", "ci.test"],
    "deny":  ["*.delete_*", {"tool": "files.write_file", "args": {"path": "*.env"}}]
  },
  "limits": {"timeout_s": 600, "call_timeout_s": 60, "call_timeouts": {"ci.test": 900}}
}
```

- **`connectors`**: a namespace per tool source. `mcp-stdio` starts any MCP
  server; `mcp-http` and `mcp-sse` reach one over the network; `commands`
  turns declared command lines into tools; `demo` is the notes store; `chaos`
  is a fault-injection fixture for tests. `${NAME}` in `env` and `headers` is
  read from the host environment when the server starts, so secrets stay out
  of the file and out of the sandbox.
- **`policy`**: rules over `namespace.tool`, as globs or as objects that also
  constrain arguments. Deny wins over ask, ask over allow. `default` applies
  when nothing matches and is `ask` if you leave it out. `"mode": "observe"`
  logs what would have been asked or denied and lets the call through: use it
  to find false positives before you enforce.
- **`approval`**: `require_terminal` (default `true`: `approve` needs a
  person at a terminal) and `ttl_s` (a pause older than a day needs
  `approve --stale`).
- **`limits`**: `timeout_s` (one pass), `call_timeout_s` (one tool call),
  `call_timeouts` (per tool), `startup_timeout_s`, `max_calls`,
  `max_value_bytes` (one argument object or tool result), `max_result_chars`
  and `max_log_chars` (what is returned), `max_state_bytes`,
  `catalog_cache_s` (how long a server's tool list is used from disk).
- **`helpers`**: a folder of Python files that programs call as
  `helpers.<file>.<function>()`. They run in the sandbox, under the policy.
- **`retention`**: `max_age_days` and `max_executions` for the records.
- **`executor`**: `command` wraps the sandbox in something else (see below);
  `env_passthrough` names host variables the sandbox may see.
- **`llm`**: `base_url`, `model`, `api_key`, `max_turns`, `timeout_s`, for the
  built-in loop only. The `CODEMODE_*` environment variables override it.
- **`agent`**: the identity written into the audit log. `--agent` and
  `$CODEMODE_AGENT` override it. The program cannot set it.

Data lives in `./.codemode` (`--home` or `$CODEMODE_HOME` to move it): one
JSONL file per execution, a JSON file per session, a JSON file per snippet.
[`docs/INTERFACE.md`](docs/INTERFACE.md) lists every file.

## The safety model, and its limit

What the runtime enforces:

- The program's process starts with a scrubbed environment and an empty
  working directory. Host variables, API keys included, are not passed.
- Connector credentials live in the host process. A program can use a tool; it
  cannot read the token behind it, and a token that a server echoes back is
  removed before the result is recorded or reaches the program.
- Every tool call is decided on the host, under the tool's own name (in the
  spelling a program writes and in the server's), at the moment it is made,
  with the real arguments.
- A call the policy asks about does not run until a human approves it from
  the CLI, at a terminal. Neither the sandbox API nor the MCP server can
  approve, edit policy or touch the log. A deny rule beats an approval.
- A project's helper code runs in the same sandbox as the program and gets
  nothing a program does not have.
- Every execution has a wall-clock limit, a call limit and size limits.
- The log names each side effect before it happens (`executing`), then how it
  ended.

**What it does not do: the default sandbox is a process, not a jail.** It runs
as your OS user. A hostile program can open files and sockets directly,
without going through a tool, and an agent with a shell of its own can do
whatever your user can. For code you do not trust, wrap the sandbox:

```json
{"executor": {"command": ["docker", "run", "--rm", "-i", "--network", "none", "--read-only",
                           "python:3.12-alpine", "python", "-I", "-u", "-c", "{bootstrap}"]}}
```

`{bootstrap}` is a one-line loader; the rest of the protocol is NDJSON on
stdin and stdout, so anything that can run `python -c` works: a container, a
jail, a remote shell. With no network in the sandbox, the tools become the
only way out, and the policy covers everything. The Docker line above is an
example and was not run in this project's tests; the mechanism is tested by
running the sandbox under a second interpreter, where one is installed, and
under deliberately broken commands.

## Outcomes and exit codes

Every surface returns the same payload:

```text
{"status": "completed", "executionId": "...", "result": ..., "logs": "...", "stats": {...}}
{"status": "paused",    "executionId": "...", "pending": [{"seq": 2, "tool": "...", "args": {...}}]}
{"status": "error",     "executionId": "...", "error": {"type": "...", "message": "...", "hint": "..."}}
{"status": "running",   "executionId": "...", "progress": {...}}
```

| Exit | Meaning |
|---|---|
| 0 | completed |
| 1 | the program failed, or the execution was rejected or cancelled |
| 2 | usage or configuration error |
| 3 | paused, waiting for approval |
| 4 | `verify` did not reproduce the recorded result |
| 5 | unknown execution, snippet or tool |
| 6 | `wait`: the execution is still running |

Results over `max_result_chars` are cut and flagged with `resultTruncated`,
and come with their shape; `codemode get <id> --select result.KEY` reads a
part, and `--full` prints the stored value. Every error says what to do next
in `error.hint`. [`docs/INTERFACE.md`](docs/INTERFACE.md) has every field.

## Tests

```text
python tests/run_all.py                           # everything below, one line per suite
python tests/e2e.py                               # end to end, on the real CLI
python -m unittest discover -s tests              # pure logic, and the states a CLI cannot produce
python tests/mutation_check.py                    # plants bugs, checks the suites catch them
```

`tests/e2e.py` starts the real program as a subprocess for every check and
writes [`docs/e2e-trace.txt`](docs/e2e-trace.txt): each command, its exit code
and its output. The cases in every suite come from
[`tests/FAILURE_MODES.md`](tests/FAILURE_MODES.md), a list of invariants and
failure modes written before the code that handles them. Three MCP servers
other than the built-in connectors are exercised: one that misbehaves on
purpose (`tests/fixtures/misbehaving_server.py`), one over HTTP
(`tests/fixtures/http_mcp_server.py`), and, when they are installed, one
built with the official MCP SDK and the reference filesystem server. Set
`CODEMODE_E2E_MCP_PYTHON`, `CODEMODE_E2E_NODE_MODULES` and
`CODEMODE_E2E_INSTALL` to include the optional scenarios. The numbers, and
what was run on which platform, are in
[`docs/VERIFICATION.md`](docs/VERIFICATION.md).

## Extending it

- **A new capability**: write or pick an MCP server and add a connector, or
  declare a command in a `commands` connector. No code change.
- **Functions your programs keep rewriting**: put them in the project's
  helpers folder.
- **A new connector type**: subclass `Connector` in
  [`codemode_runtime/connectors/`](codemode_runtime/connectors/__init__.py)
  (two methods: `list_tools`, `call`), register it in `build_connector`, and
  add its allowed keys to `CONNECTOR_KEYS` in `config.py`.
- **A different sandbox**: set `executor.command`. For a sandbox in another
  language, implement the frames in the docstring of
  [`sandbox_runner.py`](codemode_runtime/sandbox_runner.py).
- **A new surface**: call the methods of `Runtime`
  ([`runtime.py`](codemode_runtime/runtime.py)); the CLI and the MCP server
  are both thin wrappers over them.

| File | Responsibility |
|---|---|
| `config.py` | Load and validate the config; find it from a subfolder. |
| `policy.py`, `schema_sig.py`, `truncate.py`, `jsonutil.py`, `textio.py` | Pure logic: decisions, signatures, budgets and shapes, hashes, decoding. |
| `catalog.py` | Tool names, search, describe; the tool-list cache. |
| `connectors/` | `demo`, `chaos`, `mcp_stdio`, `mcp_http` (streamable HTTP and SSE), `commands`; `mcp_common` is the protocol they share. |
| `executor.py`, `sandbox_runner.py` | Start the sandbox; the API the program sees. |
| `engine.py` | One pass: replay, reuse, policy, limits, recording. |
| `records.py`, `sessions.py` | The execution log, locks and retention; session state and snippets. |
| `runtime.py` | `execute`, `get`, `wait`, `cancel`, `approve`, `reject`, `verify`, `check`, `prune`. |
| `helpers.py` | A project's helper modules: read, listed, never run on the host. |
| `project.py`, `instructions.py` | `init`, `doctor`, and the instructions for agents. |
| `detach.py`, `procutil.py`, `launcher.py` | Background workers; processes on every platform; hints that are real commands. |
| `mcp_server.py`, `llm_agent.py`, `cli.py`, `demo.py` | The surfaces. |

Agents working on this code should read [`AGENTS.md`](AGENTS.md) first.

## Where the design comes from

Six sources. [`docs/DESIGN.md`](docs/DESIGN.md) lists the 26 principles taken
from them, where they contradict each other, and which side this project took.

- Claude Code documentation, [Code Mode](https://code.claude.com/docs/en/code-mode):
  one program per step, the last expression as the result, each inner call
  checked under its own tool name.
- Cloudflare Agents documentation, [Code Mode](https://developers.cloudflare.com/agents/tools/codemode/):
  a single outer tool, signatures from JSON Schema, discovery at run time, a
  stateless executor, approval by deterministic replay.
- [Using a chaos loop to harden the Claude Docs launch](https://claude.dev/blog/chaos-loop-claude-docs-launch/):
  invariants first, per-operation telemetry, deliberate fault injection.
- [How the Claude Code team thinks about verification](https://claude.dev/blog/how-the-claude-code-team-thinks-about-verification/):
  end-to-end evidence, property and differential tests, a gate the change
  cannot edit, observe before enforce.
- [Safely scaling multi-agent teams with Claude Fable 5.2](https://claude.dev/blog/safely-scaling-multi-agent-teams-with-fable-5-2/):
  a credential-free sandbox behind a gateway that stamps identity and logs
  everything.
- [Unharnessed](https://github.com/unharnessed-agent/Unharnessed): one tool,
  state in the runtime instead of the context, MCP reached from code, and a
  wire protocol that user output cannot corrupt.

A seventh input since then is the first field report from a real project:
[`docs/FEEDBACK_TRIAGE.md`](docs/FEEDBACK_TRIAGE.md).

## Not built

- **Rollback.** Rejecting or cancelling an execution stops it; it does not
  undo calls that already ran. MCP has no notion of a compensating call to
  hang this on.
- **Wake-ups.** A detached program runs to its end; nothing calls the agent
  back when it finishes. The agent asks, with `wait`.
- **Live objects between executions.** `state` holds JSON values, not open
  connections or running processes.
- **A model-based reviewer.** Policy is deterministic rules. The audit log is
  where such a monitor would read from.
- **OAuth for remote MCP servers.** A token goes in a header. The MCP server
  this runtime offers is stdio only.
- **Built-in file tools.** A filesystem MCP server does that; one was run
  through this runtime as part of the tests.
- **Multi-agent orchestration.** Several agents can share one runtime safely;
  it does not coordinate them.

## License

MIT. See [`LICENSE`](LICENSE).
