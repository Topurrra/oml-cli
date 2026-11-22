# SPDX-License-Identifier: MIT
"""The instructions an agent needs to use code mode in a project.

``codemode instructions`` prints this block for a project to paste into the
file where it keeps instructions for its agents. It is written for any
project and any agent: it names no tool, no language, no layout and no
product. The one thing that varies is how the agent reaches the runtime, so
there are two forms: the command line (any agent that can run a command) and
the MCP tool (any MCP client).

It lives in code so that it is tested: every command it shows is one the
CLI has, and ``docs/ADOPTING.md`` carries the same text.
"""

from __future__ import annotations

_INSIDE = """\
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
"""

_APPROVALS = """\
### Approvals and denials

A call that the policy marks `ask` pauses the run (`status: paused`). Nothing
pending has run.

- Tell the user what is pending and why, and give them the command from the
  `hint`. Approving is for the user, in their own terminal.
- Never approve it yourself, and never change the policy, the config or the
  `{data}/` folder to get past a pause or a denial.
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
"""

_CLI = """\
## Code mode

In this project you use code mode for every task that needs more than one
tool call. You write one short Python program per step. The program calls
the tools, filters and joins the results, and returns only what you need.
Tool output that the program does not return never enters your context.

### Run a program

Write the program to a file with your file tool, in `{work}/`, then
run it:

```text
{cmd} run --json -f {work}/<name>.py
```

The output is one JSON object. Its `status` is `completed` (read `result`),
`error` (read `error.type`, `error.message` and `error.hint`), `paused`
(read `pending`) or `running`. Exit codes: 0 completed, 1 failed, 2 usage or
config error, 3 paused, 5 not found, 6 still running.

- **Write the program with your file tool.** Never pass it through a shell
  heredoc, `-c` or `echo`: shells change backslashes, and `\\n` in a string
  becomes a real line break.
- **Pass inputs as arguments**, not by editing the program:
  `--arg name=value`, read in the program as `args["name"]`.
- `--check` compiles the program and looks up the tools it names, without
  running it.
- `--select result.count` prints one part of the outcome and nothing else.

{inside}
### Long programs

Your shell gives a command only so long (often two or three minutes). A
program that may run longer is started detached and waited for in steps:

```text
{cmd} run --detach --json -f {work}/<name>.py     # returns the executionId at once
{cmd} wait <executionId> --timeout 100 --json             # exit 6 and status "running" until it ends: repeat
{cmd} get <executionId> --json                            # where it is: calls done, the call in flight, output so far
{cmd} cancel <executionId>                                # stop it
```

### When a program fails

- Read `error.hint`. It says what to do next.
- A failed program keeps its top-level variables. `variables` in the
  outcome shows their shapes;
  `{cmd} get <executionId> --select variables.NAME` returns one.
- To continue after a fix without repeating the calls that worked:
  `{cmd} run --reuse <executionId> --json -f <the fixed program>`.

{approvals}"""

_MCP = """\
## Code mode

In this project you use code mode for every task that needs more than one
tool call: the `execute` tool. You write one short Python program per step.
The program calls the tools, filters and joins the results, and returns only
what you need. Tool output that the program does not return never enters
your context.

### Run a program

Call `execute` with `code`. The answer is one object. Its `status` is
`completed` (read `result`), `error` (read `error.type`, `error.message` and
`error.hint`), `paused` (read `pending`) or `running`.

- **Pass inputs as `args`**, an object the program reads as `args["name"]`,
  instead of writing them into the code.
- `"check": true` compiles the program and looks up the tools it names,
  without running it.

{inside}
### Long programs

A program that may run for minutes is started in the background and asked
about:

```text
execute {{"code": "...", "background": true}}            returns the executionId at once
execute {{"executionId": "...", "wait": 60}}             its outcome, or status "running" after 60 s: repeat
execute {{"executionId": "..."}}                         where it is: calls done, the call in flight, output so far
execute {{"executionId": "...", "cancel": true}}         stop it
```

### When a program fails

- Read `error.hint`. It says what to do next.
- A failed program keeps its top-level variables. `variables` in the
  outcome shows their shapes;
  `execute {{"executionId": "...", "select": "variables.NAME"}}` returns one.
- To continue after a fix without repeating the calls that worked:
  `execute {{"code": "<the fixed program>", "reuse": "<executionId>"}}`.

{approvals}"""


def instructions(
    command: str = "codemode", surface: str = "cli", work_dir: str = ".codemode-work", data_dir: str = ".codemode", guidance: str = ""
) -> str:
    """The block for an agent, for the command line (``cli``) or for an MCP
    client (``mcp``). ``command`` is how the CLI is started in this project,
    including ``--config`` and ``--home`` if it needs them. ``work_dir`` is
    the folder agents write their programs into and ``data_dir`` the folder
    they must leave alone: a project may keep either somewhere of its own.
    ``guidance`` is the project's own text; it goes last, under its own
    heading, so that what the project says is told apart from what holds
    everywhere."""
    approvals = _APPROVALS.format(data=str(data_dir).rstrip("/"))
    if surface == "mcp":
        text = _MCP.format(inside=_INSIDE, approvals=approvals)
    else:
        text = _CLI.format(cmd=command, work=str(work_dir).rstrip("/"), inside=_INSIDE, approvals=approvals)
    if guidance.strip():
        text += "\n### In this project\n\n%s\n" % guidance.strip()
    return text
