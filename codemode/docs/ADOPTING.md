# Adopting code mode in a project

For a person, or an agent, setting this runtime up in a project of any kind:
any language, any set of MCP servers, Windows, Linux or macOS.

What you get: the agent writes one short program per step instead of making
one tool call per round trip. The program calls your tools, keeps the
intermediate data to itself and returns a few lines. Every call it makes is
checked against a policy you control and written to a log.

## Contents

- [Set up, in three commands](#set-up-in-three-commands)
- [How the agent reaches it](#how-the-agent-reaches-it)
- [The config](#the-config)
- [A project with no MCP server](#a-project-with-no-mcp-server)
- [Policy](#policy)
- [Long programs](#long-programs)
- [Project helpers](#project-helpers)
- [Many agents at once](#many-agents-at-once)
- [A command or a layout of your own](#a-command-or-a-layout-of-your-own)
- [Platforms](#platforms)
- [Instructions for the agent](#instructions-for-the-agent)
- [Coming from 0.1](#coming-from-01)
- [Coming from 0.2](#coming-from-02)

## Set up, in three commands

You need Python 3.9 or newer and nothing else.

```text
pip install /path/to/code-mode        # gives you the codemode command; no dependencies are installed
cd your-project
codemode init                         # writes codemode.json, checks the servers, updates .gitignore
codemode doctor                       # checks everything a program needs
```

Without installing, the same commands work as
`python /path/to/code-mode/main.py init` and so on. Every hint the runtime
prints uses whichever form you started it with.

`codemode init` reads the MCP server list your project already has
(`.mcp.json` in the project root, or `mcp.json` in one of its dot-folders,
where several editors and agents keep theirs; name another file with
`--from FILE`) and writes `codemode.json` in the project root:

- one connector per server, under the server's name;
- a starting policy: tools that read are allowed, everything else asks
  first, deletes never run;
- `${NAME}` references where the server list had secrets written out. The
  values are not copied; `init` tells you which variables to export.

It then connects to each server once and reports what it found:

```text
wrote /work/your-project/codemode.json  (from /work/your-project/.mcp.json)
  [ok  ] files (mcp-stdio): 14 tools, 0.6s
  [FAIL] tracker (mcp-http): MCP server 'tracker' answered HTTP 401 to initialize
         The server refused the credentials. Set them as headers on the connector ...
policy: default ask; allowed without asking: files.read_*, files.list_* ...; never: *.delete_*
.gitignore: added .codemode/, .codemode-work/
```

Read the policy before you hand the project to an agent. It is the one part
of the file that is yours to decide.

`codemode doctor` can be run at any time. It checks the interpreter, the
config, the data folder, the sandbox, every connector, the policy (a rule
that matches no tool is usually a typo, and a deny rule that matches nothing
denies nothing) and the size of the records. It exits 0 when nothing failed.

The config is found from any folder inside the project: with no `--config`,
the runtime looks in the current folder and then in the folders above it, up
to the repository root.

## How the agent reaches it

Two ways. Use the first if your agent speaks MCP.

### As an MCP server

Register it like any stdio MCP server, in the project's server list:

```json
{
  "mcpServers": {
    "codemode": {"command": "codemode", "args": ["serve"]}
  }
}
```

The agent sees one tool, `execute`. A step is one tool call; the program
travels as JSON, so no shell can change its backslashes; and your tool
servers stay up between programs instead of being started for each one.

If the agent should use your tools only through code mode, list only
`codemode` for it and keep the other servers in `codemode.json`. `init`
leaves a `codemode` entry out when it reads a server list, so a program can
never reach the runtime through itself.

### From the command line

For an agent that can run a command:

```text
codemode run --json -f .codemode-work/program.py
```

One JSON object on stdout, and an exit code that says which outcome it was.
[`INTERFACE.md`](INTERFACE.md) is the reference for both surfaces.

## The config

One JSON file, `codemode.json`, in the project root. Unknown keys are
errors. `codemode config` prints what was resolved, with credentials hidden.

```json
{
  "connectors": {
    "files":   {"type": "mcp-stdio", "command": ["npx", "-y", "@modelcontextprotocol/server-filesystem", "."]},
    "tracker": {"type": "mcp-http", "url": "https://tracker.example.com/mcp",
                "headers": {"Authorization": "Bearer ${TRACKER_TOKEN}"}},
    "ci":      {"type": "commands", "tools": {"test": {"command": ["pytest", "-q"], "timeout_s": 900}}}
  },
  "policy": {
    "default": "ask",
    "allow": ["files.read_*", "files.list_*", "tracker.get_*", "tracker.list_*", "ci.test"],
    "deny": ["*.delete_*"]
  },
  "limits": {"timeout_s": 600, "call_timeout_s": 60, "call_timeouts": {"ci.test": 900}},
  "helpers": "codemode_helpers"
}
```

### Connectors

A connector is a source of tools, under a name of your choice. The name is
the namespace in programs (`tools.files.read_file(...)`) and in policy rules.

| Type | What it connects to | Keys |
|---|---|---|
| `mcp-stdio` | An MCP server started as a local process. | `command` (list), `env`, `inherit_env`, `cwd` |
| `mcp-http` | An MCP server over streamable HTTP. | `url`, `headers` |
| `mcp-sse` | An MCP server over HTTP with server-sent events (the older remote transport). | `url`, `headers` |
| `commands` | Command lines you declare. No server needed. | `tools`, `cwd`, `env`, `inherit_env`, `max_output_chars` |

Every type also takes:

- `call_timeout_s`: how long one call may run, for this connector.
- `startup_timeout_s`: how long it may take to start. Raise it for a server
  that downloads itself on first run.
- `max_concurrency`: how many of its calls run at once. Set it to `1` for a
  server that handles one request at a time: calls then queue here, and a
  queued call's time limit starts when the call does.

MCP connectors also take `parse_json_text: false` (keep a server's text
results as text even when they look like JSON) and `secrets` (names of `env`
entries whose values must never appear in a result).

**Secrets.** Write `${NAME}` in `env` and `headers`. The value is read from
the environment of the process that runs the runtime, when the server
starts. It never reaches the sandbox, and it is removed from anything the
server echoes back. Do not write a token into the file.

**The environment of a server or a command.** A process the runtime starts
does not get the host's whole environment, which holds every credential the
host has. It gets:

- a base list: `PATH`, the user's name and home folder, the folders for
  programs, settings, caches and temporary files of the platform
  (`APPDATA`, `ProgramFiles` and the like on Windows; `XDG_*` and `TMPDIR`
  elsewhere), the locale and the time zone;
- what you write in `env`;
- what `inherit_env` names: `["JAVA_HOME", "MYTOOL_*"]` passes those
  variables as they are on the host, with `*` and `?` as wildcards.
  `true` passes everything.

An inherited variable whose name says it holds a credential (`TOKEN`,
`KEY`, `SECRET`, `PASSWORD`, `AUTH` and the like) is removed from results,
as a `${NAME}` reference in `env` is. Names are compared as your system
compares them: without regard to case on Windows.

**Paths.** A relative `cwd`, and relative paths in a `command`, are read
from the folder you start the runtime in. When the runtime found the config
by looking in the folders above, they are read from the folder of the
config, which is the project root.

**OAuth** is not implemented. A server that needs it is given a token
obtained elsewhere, in a header.

### Limits

| Key | Default | Meaning |
|---|---|---|
| `timeout_s` | 60 | One program, wall clock. |
| `call_timeout_s` | 30 | One tool call. |
| `call_timeouts` | none | Per tool: `{"ci.build": 1800, "ci.*": 300}`. The first pattern that matches wins. |
| `startup_timeout_s` | 20 | Starting the sandbox, or a server. |
| `max_calls` | 200 | Tool calls per program. |
| `max_value_bytes` | 1000000 | One argument object or one tool result. A larger result fails the call and is saved under `.codemode/blobs/`. |
| `max_result_chars` | 24000 | The result returned to the caller. Past it you get a preview, the shape, and a way to read a part. |
| `max_log_chars` | 8000 | Printed output returned to the caller. |
| `max_state_bytes` | 1000000 | One session's `state`. |
| `catalog_cache_s` | 300 | How long a server's tool list is used from disk. `0` turns the cache off. |
| `max_guidance_chars` | 4000 | The project's own `guidance` text. Past it the text is cut and says so. |

Set `timeout_s` to the longest program you expect, and give long tools
their own entry in `call_timeouts`, rather than raising `call_timeout_s`
for everything: a status query that hangs should not be waited on for an
hour.

### Other sections

- `approval`: `require_terminal` (default `true`), `ttl_s` (default
  86400) and `standing` (default `all`; `same-tool` or `off` narrow what a
  person may approve in advance). See [Policy](#policy).
- `retention`: `max_age_days` (default 30) and `max_executions` (default
  2000). Records past either are removed now and then; `0` turns a limit
  off. `codemode prune` does it on demand.
- `helpers`: a folder of Python files. See [Project helpers](#project-helpers).
- `guidance`: this project's own words for its agents: a short text, or
  `{"file": "docs/agents.md"}`. It is added to the description of the
  `execute` tool, which is all an agent that uses the MCP server reads, and
  under its own heading to what `codemode instructions` prints. Use it for
  what the runtime cannot know: which helper to reach for, what a tool's
  result is good for, what never to do here. A file is read when it is
  needed, so an edit shows at once. Keep it short, because every agent reads
  it on every start: past `limits.max_guidance_chars` it is cut and says
  so, and `codemode doctor` warns.
- `work_dir`: the folder agents are told to write their program files into
  (default `.codemode-work`). The instructions name it and `init` keeps it
  out of version control. See
  [A command or a layout of your own](#a-command-or-a-layout-of-your-own).
- `executor`: where programs run. See the README on containers.
- `agent`: the name written into the log for this caller. `--agent` and
  `$CODEMODE_AGENT` override it.

## A project with no MCP server

Many projects have none: their tools are a build, a test runner, version
control. Declare those commands and they are tools:

```json
"ci": {
  "type": "commands",
  "env": {"CI": "1"},
  "inherit_env": ["JAVA_HOME", "CARGO_*"],
  "tools": {
    "build": {"description": "Build one configuration.",
              "command": ["cmake", "--build", "build", "--config", "{config}"],
              "params": {"config": {"type": "string", "enum": ["Debug", "Release"], "default": "Release"}},
              "timeout_s": 1800},
    "test":  {"description": "Run the tests, or the ones that match.",
              "command": ["ctest", "--test-dir", "build", "--output-on-failure", {"if": "only", "then": ["-R", "{only}"]}],
              "params": {"only": {"type": "string"}},
              "allow_env": ["CTEST_*"],
              "timeout_s": 900},
    "status": {"command": ["git", "status", "--short"]}
  }
}
```

- `{name}` in an element is replaced by the parameter. A list parameter
  that is a whole element becomes one argument per item.
  `{"if": "name", "then": [...]}` adds arguments only when the parameter
  was given. `{{` and `}}` are literal braces.
- **No shell runs the command.** A value with spaces or `;` in it is one
  argument. There is no tool that takes an arbitrary command line unless
  you declare one, and then the policy applies to it by name.
- A parameter with no default that the command uses is required.
- `allow_env` names the environment variables a caller may set for a run
  (`tools.ci.test(env={"CTEST_PARALLEL_LEVEL": "4"})`). Everything else is
  refused.
- The command gets a short list of host variables (`PATH` and the like),
  your `env`, and what `inherit_env` names. `"inherit_env": true` passes
  the whole environment, including any secrets in it. See "The environment
  of a server or a command" above.
- Every tool returns
  `{ok, exit, timed_out, seconds, stdout, stderr, stdout_chars, stderr_chars, truncated, output_file}`.
  Output over `max_output_chars` (20000) keeps its start and its end, is
  marked, and the whole of it is in `output_file`. A command over its time
  limit is stopped with the processes it started, and the result says so.

When a command needs logic around it (parse the output, compare with a
baseline), write a small MCP server. [`SERVER_AUTHORS.md`](SERVER_AUTHORS.md)
says what makes its tools good to program against.

## Policy

Each call is decided when it is made, on the host, with its real arguments:

- **deny**: the call fails with `PolicyDenied`. The program can catch that
  and carry on.
- **ask**: the program pauses before the call. Nothing pending has run. You
  approve from your terminal, and the same program continues from where it
  stopped; calls that already ran are not made again.
- **allow**: the call runs.

Deny wins over ask, ask over allow. A call that no rule matches gets
`default`, which is `ask` unless you say otherwise: a project that has
never written a policy pauses on everything.

```json
"policy": {
  "default": "ask",
  "allow": ["files.read_*", "files.list_*", "ci.*"],
  "ask":   [{"tool": "ci.git", "args": {"argv.0": "push"}}],
  "deny":  ["*.delete_*", {"tool": "files.write_file", "args": {"path": "*.env"}}]
}
```

- A rule is a glob over `namespace.tool`. It matches the name a program
  writes and the name the server gave the tool, so `files.delete-file` and
  `files.delete_file` are the same rule.
- A rule can also constrain arguments: `{"tool": ..., "args": {"path":
  "glob"}}`. The path into the arguments uses dots and list indexes
  (`argv.0`). Every named argument has to be present and match. Patterns are
  compared as text: `docs/*` also matches `docs/../secret`, so do not rely
  on them for path containment. A server that takes a root does that better.
- `"mode": "observe"` logs what the rules would have done and lets every
  call through. Use it for a day to see what a policy would stop, then
  switch to `enforce`.

**Approving.** `codemode approve <id>` in your own terminal. It refuses to
run without a terminal on stdin, so an agent that drives the CLI through a
shell tool cannot approve its own request with one more command. A pipe, a
file and the null device (`< /dev/null`, `< NUL`) are all refused; on
Windows the console itself is asked, because Python calls the null device a
terminal there. If you approve from a script on purpose, set
`"approval": {"require_terminal": false}`. A pause older than
`approval.ttl_s` (a day) needs `approve --stale`: the question was asked a
while ago.

This check tells a shell tool from a terminal. It cannot tell an agent from
a person: a tool that types into a real terminal (a terminal panel of an
editor that an agent can drive, a pseudo-terminal) looks like you. If your
agent has such a tool, do not let it run `approve`, and put what must never
happen under `deny`, which no approval overrides. The other way round, a
window that gives programs a pipe instead of a terminal is refused even
with you at the keyboard (Git Bash in its own window does this without
`winpty`): approve from another terminal.

A deny rule beats an approval. If the policy changed to deny a call while
its program was paused, approving does not run it.

**Many gated calls in a row.** A program that makes several calls the
policy asks about pauses at each one that comes after the last approval.
There are three ways to give one answer for several calls, from the
narrowest to the widest:

- Put the calls in one `parallel()`. They pause together, and one
  `codemode approve <id>` covers them with every argument in front of you.
- `codemode approve <id> --same-tool` approves what is pending and, in
  advance, later calls of this execution to the same tools, whatever their
  arguments.
- `codemode approve <id> --all-remaining` approves what is pending and
  every later call of this execution that the policy asks about. You do
  not see those calls first, so read the program before you use it:
  `codemode get <id> --select code`.

An approval in advance is for that one execution; the same program run
again asks again. It ends when any approval would (`approval.ttl_s`), and
it never covers a call under `deny`. Each call that runs under it is
written to the log with the person who approved, before the call, and
every outcome of the execution lists what was approved in advance
(`standingApprovals`). It needs a terminal like any approval; a program
and the MCP tool have no way to give one. To keep people to the narrower
form set `"approval": {"standing": "same-tool"}`; `"off"` turns both off.

**What the policy does not cover.** By default the program runs as a
process under your user, not in a jail: it can open files and sockets by
itself. If you do not trust the code, run the sandbox in a container
(`executor.command`; the README has the line), where the tools are then the
only way out.

## Long programs

A build or a test run can outlast the time an agent's shell gives one
command. Start such a program detached and ask about it:

```text
codemode run --detach --json -f program.py     # returns {"status": "running", "executionId": ...} at once
codemode wait <id> --timeout 100 --json        # the outcome, or status "running" and exit 6 after 100 s
codemode get <id> --json                       # calls done, the call in flight and for how long, output so far
codemode cancel <id>                           # stop it; what already ran stays applied
```

Over MCP the same things are `background`, `wait` and `cancel` on the
`execute` tool.

If a program does fail late, its work is not lost:

- its top-level variables are kept: `codemode get <id> --select variables.NAME`;
- `codemode run --reuse <id> -f fixed.py` runs the corrected program and
  takes every step that still matches from the earlier log, so only what is
  new, or failed, runs.

An execution whose process was killed (a shell timeout, a closed terminal)
is recorded as `Interrupted` the next time anything looks at it, and can be
continued with `--reuse` in the same way.

## Project helpers

Agents write the same small functions again in every program. Keep them in
the project instead:

```text
your-project/
  codemode.json            "helpers": "codemode_helpers"
  codemode_helpers/
    ci.py                  def failed_tests(suite): ...
```

A program calls `helpers.ci.failed_tests("unit")`. `search()` lists helper
functions with their parameters and the first line of their docstring.

A helper is not trusted more than a program is. Its source is sent to the
sandbox and runs there, with the same names a program has (`tools`, `call`,
`parallel`, `state`, ...). A tool call made by a helper crosses the same
gateway: same policy, same limits, same log. Files whose name starts with an
underscore are skipped. The helpers an execution started with are recorded,
so a program that paused resumes with the same helper code even if the files
changed in between.

## Many agents at once

**Separate data folders** are the simple way: each agent in its own
checkout or worktree, with its own `codemode.json` and its own `.codemode/`.
Nothing is shared and nothing can collide. To point one server at each
agent's copy of the project, give it the root in the connector's `env`
(`"env": {"PROJECT_ROOT": "/work/agent-3"}`).

**One shared data folder** also works:

- Each execution has its own log and its own lock, so executions never
  mix.
- Give each agent a name (`--agent NAME` or `$CODEMODE_AGENT`). It is
  written into the log, and `last` then means that agent's last execution,
  not whoever ran most recently.
- Give each agent a session (`--session NAME`) unless they are meant to
  share `state`. Writes to one session are serialized, and the last write
  to a key wins.
- Tool lists are cached per data folder, so the second agent does not start
  a server just to list its tools.

**Records.** Every execution is a file under `.codemode/executions/`. They
are removed after 30 days or beyond the newest 2000 (the `retention`
section); `codemode prune --older-than 7` removes more, and
`codemode doctor` shows the size.

## A command or a layout of your own

**Your own command.** If people and agents in your project start the
runtime through something else (a wrapper script, a task runner, a tool
that ships a copy of it), tell the runtime what they type:

```text
CODEMODE_COMMAND="mytool code"
```

Every hint, `--help` and `codemode instructions` then print that command
followed by the subcommand, for example `mytool code approve <id>`, in
place of the interpreter and the script. Set it in the environment the
runtime is started in, which is what a wrapper does anyway.

Nothing is added to the command: it is taken to choose the config and the
data folder itself. If yours passes `--config` and `--home` through, say
where they go with `{options}`:

```text
CODEMODE_COMMAND="task codemode {options} --"
```

With the variable set, `init` also knows the runtime's own MCP server when
your server list has it under that command (`mytool code serve`), and
leaves it out as it does under its own name: a program must not reach the
runtime through itself.

**Your own layout.** By default the runtime puts three things in the
project root: `codemode.json`, the data folder `.codemode/`, and
`.codemode-work/`, where agents are told to write their program files. A
project that keeps such things elsewhere says so once, at `init`:

```text
codemode init --out tools/agents/codemode.json --home tools/agents/data --work-dir tools/agents/programs
```

- `--out FILE` writes the config there. Nothing looks for it there by
  itself, so later commands name it: `--config FILE`, or `CODEMODE_CONFIG`.
  Every command `init` prints already does. Relative paths in a config
  named this way are read from the folder a command runs in, so run from
  the project root.
- `--home DIR` (or `CODEMODE_HOME`) is the data folder, as on every command.
- `--work-dir DIR` is written into the config as `work_dir`.

`.gitignore` gets the folders the project really uses, and none for a
folder outside the project. `codemode instructions` names the same folders:
where programs go, and which folder an agent must leave alone.

## Platforms

Tested on Windows 11 and on Linux. macOS shares the POSIX code path with
Linux and was not run; [`VERIFICATION.md`](VERIFICATION.md) says exactly
what was run where.

- **Paths.** Use forward slashes in programs and in the config, on Windows
  too. Hints print forward slashes for the same reason: they survive every
  shell.
- **Encodings.** Config and program files may be UTF-8 with or without a
  byte-order mark, or UTF-16. Line endings do not matter. Records are ASCII
  JSON. Output of commands that is not UTF-8 is decoded in the machine's
  console encoding.
- **Processes.** A program, a server or a command that is stopped is
  stopped with everything it started (a process group on POSIX, the process
  tree on Windows). No console windows are opened on Windows.
- **The interpreter.** The runtime starts the sandbox with the interpreter
  that is running it, and prints that interpreter in its hints. It never
  runs a bare `python`.

## Instructions for the agent

`codemode instructions` prints the block below with your project's own
command line in it (`codemode instructions --mcp` for an agent that uses the
MCP server). Paste it into `AGENTS.md` or `CLAUDE.md`, then add what is
specific to your project underneath: which namespaces there are, which calls
ask first.

<!-- instructions:start -->
````markdown
## Code mode

In this project you use code mode for every task that needs more than one
tool call. You write one short Python program per step. The program calls
the tools, filters and joins the results, and returns only what you need.
Tool output that the program does not return never enters your context.

### Run a program

Write the program to a file with your file tool, in `.codemode-work/`, then
run it:

```text
codemode run --json -f .codemode-work/<name>.py
```

The output is one JSON object. Its `status` is `completed` (read `result`),
`error` (read `error.type`, `error.message` and `error.hint`), `paused`
(read `pending`) or `running`. Exit codes: 0 completed, 1 failed, 2 usage or
config error, 3 paused, 5 not found, 6 still running.

- **Write the program with your file tool.** Never pass it through a shell
  heredoc, `-c` or `echo`: shells change backslashes, and `\n` in a string
  becomes a real line break.
- **Pass inputs as arguments**, not by editing the program:
  `--arg name=value`, read in the program as `args["name"]`.
- `--check` compiles the program and looks up the tools it names, without
  running it.
- `--select result.count` prints one part of the outcome and nothing else.

### Inside a program

| Name | Use |
|---|---|
| `tools.<namespace>.<tool>(**args)` | Call a tool. It returns a plain Python value. |
| `call("namespace.tool", {...})` | The same, by name. With `on_error="return"` a failure comes back as a `ToolError` value instead of raising. |
| `parallel([("namespace.tool", {...}), ...])` | Run independent calls at the same time. Results come back in order. |
| `repeat("namespace.tool", {...}, n=10)` | Run one call n times and get back only the failures. |
| `search("words")` | Find tools. Returns one-line signatures; `.more` says how many matches were left out. `search(namespace="name")` lists what one namespace offers. |
| `describe("namespace.tool")` | The signature, the schema and the policy of one tool. |
| `shape(value)` | The keys and types of a value, without its content. |
| `grep(text, pattern)`, `tail(text, n)`, `summarize(text)` | Cut long text output down inside the program. |
| `state` | A dict that persists between programs in this session. |
| `args` | The arguments this program was started with. |
| `step("name", fn)` | Record a value that changes between runs, such as the time or a random number. |
| `helpers.<module>.<function>()` | This project's own helper functions, if it has any; `search()` lists them. |
| `ToolError` | Raised when a call is denied, fails or is unknown. It has `.kind`, `.message` and `.hint`. |

Rules:

- **One program per step.** Do not make one call per program.
- **Look a tool up before its first use**, with `search` and `describe`. If
  `describe` shows no result schema, look at one result with `shape(value)`
  before you write code that indexes it.
- **Independent calls go in `parallel`.**
- **Return only what you need.** The value of the last expression, at the
  top level, is the result; a value inside an `if` block is not. Filter,
  count and join in the program. `print()` goes to the logs: keep it short.
- **Expect failures.** An uncaught `ToolError` ends the program. Wrap calls
  that may fail in `try/except ToolError`, or use `on_error="return"`.
- **Believe the counts.** When a list says it was cut (`.more`, a `total`
  next to the items, `truncated: true`), it was. Ask for the rest or use the
  count; do not count the items you were given.
- **Keep what you read in `state`**, so the next program does not fetch it
  again.

### Long programs

Your shell gives a command only so long (often two or three minutes). A
program that may run longer is started detached and waited for in steps:

```text
codemode run --detach --json -f .codemode-work/<name>.py     # returns the executionId at once
codemode wait <executionId> --timeout 100 --json             # exit 6 and status "running" until it ends: repeat
codemode get <executionId> --json                            # where it is: calls done, the call in flight, output so far
codemode cancel <executionId>                                # stop it
```

### When a program fails

- Read `error.hint`. It says what to do next.
- A failed program keeps its top-level variables. `variables` in the
  outcome shows their shapes;
  `codemode get <executionId> --select variables.NAME` returns one.
- To continue after a fix without repeating the calls that worked:
  `codemode run --reuse <executionId> --json -f <the fixed program>`.

### Approvals and denials

A call that the policy marks `ask` pauses the run (`status: paused`). Nothing
pending has run.

- Tell the user what is pending and why, and give them the command from the
  `hint`. Approving is for the user, in their own terminal.
- Never approve it yourself, and never change the policy, the config or the
  `.codemode/` folder to get past a pause or a denial.
- A denied call (`PolicyDenied`) is final. Do not look for another way to
  make it; tell the user which rule denied it.
- After the user approves, fetch the outcome by its `executionId`.

### When not to use it

- One call that returns little: call the tool directly.
- Work with no tool behind it, such as editing a file: use your normal tools.

### Care

- Stay inside the project folder. Do not search the whole disk, and do not
  load the machine on purpose.
- Secrets stay in environment variables. Never write a token into a program
  or into the config.
````
<!-- instructions:end -->

## Coming from 0.1

Commands, JSON fields and exit codes are unchanged; everything else was
added. Four behaviours are different, on purpose:

1. `approve`, and `run --interactive`, need a terminal on stdin, unless the
   config says `"approval": {"require_terminal": false}`.
2. A deny rule now applies under the server's spelling of a tool name as
   well, and beats an approval given before the rule existed.
3. A pause older than a day needs `approve --stale`.
4. Records older than 30 days, or beyond the newest 2000, are removed. Set
   `"retention": {"max_age_days": 0, "max_executions": 0}` to keep them all.

Also new and visible: hints print a full command line (the interpreter's
path, `--config`, `--home`) instead of `python main.py ...`; an empty
`search("")` lists up to 50 tools instead of 10; the `execute` MCP tool has
more optional parameters.

## Coming from 0.2

Commands, JSON fields and exit codes are unchanged; everything else was
added. Three behaviours are different, on purpose:

1. On Windows, `approve` with the null device on stdin (`< NUL`,
   `< /dev/null`, or a tool that gives its commands no input) is refused.
   It used to be accepted, which let an agent approve its own request.
2. A server or a command gets a few more host variables by default: the
   platform's folder variables, the locale, the time zone. None is a
   credential.
3. With `inherit_env`, a variable whose name says it holds a credential is
   removed from what the server or the command returns, and a pattern
   written in another case now matches on Windows.

New, and off until you use them: `inherit_env` on stdio servers;
`CODEMODE_COMMAND`; `init --out` and `--work-dir`, the `work_dir` key;
`search --namespace` and `search(namespace=...)`; the `guidance` key;
`approve --same-tool` and `--all-remaining`, with `approval.standing` to
narrow them. A config that uses a new key is an error under 0.2, which
rejects keys it does not know.
