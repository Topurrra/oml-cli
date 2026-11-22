# Failure modes and invariants

Written before the code. The unit cases in `test_pure_logic.py` and the
scenarios in `e2e.py` come from this list, not from reading the implementation.
Each item has an ID that the tests quote.

## Invariants (asserted end to end, on the real CLI)

- **I1** An execution has exactly one `start` and at most one `end`. A pass
  ends in exactly one of completed, paused, error.
- **I2** A call whose decision is "ask" has no side effect before an approval
  for its sequence number is in the log.
- **I3** Replaying a completed execution from its log, with no live calls,
  gives the same result hash.
- **I4** A tool result reaches the caller only if the code returns or prints
  it, and the returned payload respects the output budget.
- **I5** The sandbox process does not see the host's environment variables.
- **I6** Nothing the code prints, including JSON-looking lines and raw
  descriptor writes, breaks the protocol.
- **I7** Exit codes are 0 completed, 1 failed, 2 usage or config, 3 paused,
  4 verification mismatch, 5 not found.
- **I8** After any failure the next command works.
- **I9** An approved call runs exactly once, and calls recorded before the
  pause do not run again.

## Schema to signature (`schema_sig.py`)

- **S1** The schema is missing, `None`, a boolean or a list. Must not raise.
- **S2** `type` is a list such as `["string", "null"]`.
- **S3** There is no `type` but there are `properties`.
- **S4** A name is in `required` but not in `properties`.
- **S5** Required parameters must come before optional ones, each group in
  schema order.
- **S6** A property name is not a Python identifier (`weird-name`, `2fa`,
  `has space`) or is a keyword (`class`, `from`). The signature must not show
  it as a bare parameter.
- **S7** `enum` with mixed types, with quotes inside strings, or with many
  values.
- **S8** Nested arrays, and arrays with no `items`.
- **S9** `anyOf` or `oneOf`, with duplicates and with null; `allOf`.
- **S10** `$ref` into `$defs` or `definitions`; a ref whose target is missing;
  a schema that refers to itself.
- **S11** A schema nested far deeper than any sane tool. Must terminate with
  bounded output.
- **S12** A tool with a very large number of parameters. The compact form must
  stay short.
- **S13** `const`.
- **S14** `type` is not a string, or is a name JSON Schema does not define.
- **S15** `additionalProperties` carrying a type.
- **S16** No output schema.

## Tool name sanitizing (`catalog.py`)

- **N1** Dashes, dots, slashes, spaces.
- **N2** A leading digit.
- **N3** A Python keyword.
- **N4** Empty, or nothing but symbols.
- **N5** Two names that collide after sanitizing. Both must stay callable and
  the outcome must not depend on listing order.
- **N6** Leading underscores and dunder names, which would shadow attributes
  of the proxy object.
- **N7** A sanitized name must always be a valid identifier.

## Policy (`policy.py`)

- **L1** A path matches allow and deny: deny wins. Ask and allow: ask wins.
- **L2** Nothing matches: the default applies, and the default is "ask" when
  the config does not set one.
- **L3** Matching must be case-sensitive on every platform.
- **L4** `*` crosses the dot between connector and tool.
- **L5** A misspelt mode, default or decision is a config error, never a
  silent allow.
- **L6** A pattern that is not a string is a config error.
- **L7** An unbalanced `[` in a pattern must not raise.
- **L8** Observe mode computes the same decision and marks it not enforced.
- **L9** A rule is written with the server's spelling of a tool name
  (`files.delete-file`) and the program uses the sanitized one
  (`files.delete_file`). The rule must apply under both spellings.
- **L10** A paused call is approved after the policy changed to deny it.
  Deny must win over the approval, and the outcome must say the policy
  changed.
- **L11** A rule with argument patterns matches only when every named
  argument is present and matches: nested paths, list indexes, values that
  are not strings, a missing argument, an index out of range.
- **L12** A rule object with an unknown key, no tool, or patterns that are
  not strings is a config error.
- **L13** `approve`, or `run --interactive`, is started by a process with no
  terminal on stdin, which is what an agent's shell tool is. It must be
  refused unless the config allows it, and nothing pending may run.
- **L14** A paused execution is approved long after it paused. Past the
  time-to-live the approval is refused unless the person insists.
- **L15** The decision shown by `describe()` is for the tool alone. When a
  rule for that tool looks at arguments, the output must say so.

## Output budgets (`truncate.py`)

- **T1** Text at or under the budget comes back unchanged and unflagged.
- **T2** A budget of zero, a negative budget, or one smaller than the marker.
  Must not raise or loop, and the output stays bounded.
- **T3** A dict or list over budget. The result must be flagged and must not
  be a cut-off string passed off as the value.
- **T4** The marker says how much was dropped.
- **T5** Head-and-tail log truncation keeps both ends.
- **T6** Lengths exactly at the budget and one over.
- **T7** The bounded log buffer under a flood: memory stays bounded and the
  dropped count is right.

## Fingerprints (`jsonutil.py`)

- **F1** Key order must not change a fingerprint.
- **F2** Different arguments, a different name or a different kind must change
  it.
- **F3** NaN and infinities are refused rather than hashed.
- **F4** Non-ASCII text hashes the same on every platform.

## Configuration (`config.py`)

- **G1** An unknown top-level key, or an unknown key inside a section.
- **G2** A connector name that is not an identifier or is reserved.
- **G3** An unknown connector type.
- **G4** A limit that is zero, negative, or not a number.
- **G5** `${VAR}` where the variable is not set.
- **G6** A config file that is not valid JSON, or is not an object.
- **G7** `executor.command` without the `{bootstrap}` placeholder.

## What a program is given (`sandbox_runner.py`, `truncate.py`, `runtime.py`)

- **H1** `search()` finds more entries than it returns. The list must carry
  the total, and a caller who reads only the outcome must be able to see that
  the list stops short.
- **H2** A program treats the dict from `describe()` as text: prints it,
  slices it.
- **H3** `grep`, `tail`, `head` and `summarize` are given empty text, `None`,
  a list of lines, CRLF text, a pattern that matches nothing, more matches
  than the limit, context ranges that overlap, a line far longer than the
  width.
- **H4** `shape()` is given deep, wide and odd values: the depth and key caps,
  an empty list, a tuple, a value JSON cannot hold. The host's copy
  (`shape_of`) and the sandbox's must agree.
- **H5** `repeat()`: some runs raise, `ok` raises, the result says
  `"ok": false`, `until_fail` stops early, `keep` is smaller than the
  failures, `n` is zero. The counts must add up, and every run must be a
  recorded call so the execution replays.
- **H6** `call(..., on_error="return")` when the call fails, when it works,
  and with a misspelt mode.
- **H7** Program arguments: a value that is JSON, plain text, text with `=` in
  it, a repeated name; `--args-json` that is not an object; and a paused
  execution, which must resume with the arguments it started with.
- **H8** A program fails after doing its work. Its top-level variables must
  be recoverable: values JSON cannot hold are skipped, the smallest are kept
  when the budget is short, the API's own names are left out, and the
  outcome shows shapes, never the values.
- **H9** The program ends with a block whose last statement is a bare value.
  The null result must come with a warning; a block that ends in a call must
  not warn.
- **H10** A string literal was broken by a shell (a real line break inside
  the quotes), or a Windows path started a `\U` escape. The error must say
  what probably happened and what to do.
- **H11** The host process dies while the program is in a loop that makes no
  request. The sandbox process must exit too.
- **H12** `--select` with a path that does not exist, a list index, a value
  over the output budget, and a result that is not there because the
  execution failed.

## Calls: time limits, queues, large results, reuse (`engine.py`, `config.py`)

- **C1** Two tools need different time limits, a quick one and a long one. A
  limit set for one tool must apply to that tool only; the first matching
  pattern wins; a connector's own limit applies when no pattern matches.
- **C2** A server that handles one request at a time is sent several calls
  in one batch. With `max_concurrency` 1 they must run one after another,
  and the clock of a queued call must not run while it waits.
- **C3** A call is ended by the program's time limit, not by its own. The
  error must name the right limit, and a program that handles it must get
  the chance to return what it already has.
- **C4** One call in a batch times out while the others finish in time. The
  others' results must reach the program.
- **C5** A tool result is larger than the size limit. The call fails, but
  the result must be saved, and the error must say where and that the call
  ran.
- **C6** One batch holds more calls than the call limit. It must be refused
  as a single recorded step, not expanded into an entry per call.
- **C7** A program fails late, is fixed, and is run again with `--reuse`.
  Steps that match the earlier execution must not run again; the first step
  that differs and everything after it must run live; a step that failed
  earlier must run again; a result must not be handed out for a tool the
  policy now denies; the earlier execution may not exist.
- **C8** `--check` on a program with a syntax error, on one that names a
  missing tool (by attribute, by `call`, in a `parallel` list), and on a
  tuple that only looks like a tool path. No tool may be called and nothing
  may be recorded.
- **C9** A connector's credential appears in a tool result or in an error
  message. It must be replaced before the value is recorded or reaches the
  program. A short value must not be treated as a credential.
- **C10** The `config` command must not print a literal value from a
  connector's `env`; a `${NAME}` reference is not the secret and is shown.
- **C11** The sandbox's loader reads its one line through a buffer. A second
  frame that is already in the pipe is read into that buffer and never
  reaches the runner, and the host waits for an answer that cannot come. No
  frame may be sent before the sandbox has said it is ready. Found on Linux,
  where both frames arrive together; `--check` did this.

## Long runs (`runtime.py`, `records.py`, `procutil.py`, `cli.py`)

- **R1** A program runs longer than the caller's shell lets a command run.
  Started detached, the command must return at once with the id, and the
  program must keep running after that command has exited.
- **R2** `get` on a running execution must show where it is: the calls that
  finished, the call in flight and for how long, and what the program has
  printed so far.
- **R3** `wait` with less time than the run needs returns "running" and
  exit 6; with enough time it returns the outcome and the usual exit code;
  on an execution that has finished it returns at once.
- **R4** `cancel` from another process stops a running execution: the
  outcome is Cancelled, what ran stays applied and what had not started does
  not run. On a paused execution it rejects; on a finished one it is refused.
- **R5** The process running an execution is killed outright (a shell
  timeout). The execution must not say "running" for ever: the next `get`
  reports Interrupted and how to continue, and the dead process's lock must
  not block the commands that follow.
- **R6** A detached worker exits before it takes the execution. The
  execution must end as Interrupted with the worker's own output, not sit
  in "starting".
- **R7** The log is read while a writer is in the middle of a line, or after
  one died there. A half last line is ignored; a damaged line in the middle
  is an error that names the file.
- **R8** Two processes find the lock of a dead process at the same moment.
  Only one may end up holding the lock; a lock whose owner is alive is never
  cleared.
- **R9** A program prints far more than the live-output budget while it
  runs. What is persisted during the run must stay bounded.
- **R10** A foreground run is interrupted from the keyboard. The execution
  must end as Interrupted, not stay "running".
- **R11** A process has exited and nobody collected it (in a container with
  no init process this lasts as long as the container). It still has an id
  and still accepts a signal. It must count as gone, or a lock it held
  would be held for ever. Found on Linux, in a container.

## MCP servers as they really are (`connectors/`, `catalog.py`)

- **M1** A server takes longer to start than the default allows. A
  connector's own `startup_timeout_s` must cover it; without one the error
  must name that setting.
- **M2** A server prints lines that are not protocol messages to stdout and
  ends its lines with CRLF. Calls must still work.
- **M3** A server exits in the middle of a call. That call fails and shows
  the server's stderr; the next call must find a new server process; an
  answer or an end-of-stream from the old process must not reach a request
  sent to the new one.
- **M4** A tool returns plain text, JSON as text, the text `42`, two text
  blocks, an image. Each must give a predictable value, and
  `parse_json_text: false` must keep text as text.
- **M5** A tool's schema and description are hundreds of kilobytes.
  `describe()` must still answer within the size of one value and say that
  the schema was shortened; the steps of shortening must end.
- **M6** A server echoes its credential back: in a result, as a key, in an
  error message. None of them may reach the program, the outcome or the log.
- **M7** A long call reports progress. A watcher must be able to see the
  last report while the call is still running.
- **M8** The gateway gives up on a call. The server must be told, so that it
  can stop the work.
- **M9** A server ignores the end of its input. After the run neither it nor
  a process it started may be left running.
- **M10** Three tool names of one server become the same identifier. All
  three must be callable and each must reach its own tool.
- **M11** A batch reaches several servers, one slow to start and one that
  never starts. They must be started side by side, and the one that fails
  must not cost the others their results.
- **M12** Streamable HTTP: a JSON answer; an event-stream answer with
  progress before the result; the session id and the protocol version on
  later requests; a session the server has forgotten; a URL that is a web
  page; credentials that are missing; a call over its time limit; the
  session ended when the run ends.
- **M13** HTTP+SSE: the endpoint event; data spread over several lines;
  comment lines; an event with no data; a stream that ends in the middle of
  an event; a server that never names its endpoint.
- **M14** The tool list of a server is cached on disk. A later run must not
  start the server just to list its tools; editing the server's script must
  drop the cached list at once; a tool the cached list does not have must be
  looked up on the server before it is called unknown; an entry past its
  time limit, or with the cache turned off, is not used.
- **M15** A command is run from a folder below the project root, with no
  `--config`. The config must be found above, and the servers' relative
  paths and the data directory must then be read from the root. With
  `--config` they are read from the current directory, as they always were.
  The search must stop at the repository root and at the home folder.
- **M16** In a process that serves many executions, a server's script is
  edited between two of them. The second must talk to the new code.

## A project's commands as tools (`connectors/commands.py`)

- **K1** A command uses a parameter that is not declared; a conditional part
  is malformed; a parameter has an unknown type, or a default that does not
  fit its type; the tools object is empty; the program to run is itself a
  parameter, which would make the tool run anything. All are config errors
  at load.
- **K2** A value holds spaces, quotes, `;`, `&&` or starts with a dash: it
  must stay one argument. A list parameter becomes one argument per item. A
  conditional part appears only when its parameter was given. Doubled braces
  are literal braces.
- **K3** A call has an argument that is not declared, lacks a required one,
  or gives a value of the wrong type or outside the enum. It must fail
  before anything runs, and say what the tool takes.
- **K4** The caller sets environment variables. The names the project
  allowed pass; any other is refused before anything runs. Variables of the
  host are not passed unless the project says so.
- **K5** A command prints more than the output budget, or more than fits in
  memory. Both ends must be kept, the sizes must be the real ones, the cut
  must be marked, and the whole output must be in a file.
- **K6** A command runs past its time limit after starting a child. Both
  must be stopped, and the result must say so and keep the output so far.
- **K7** The program is not on PATH. The failure must say what to check.
- **K8** A command exits with a code other than zero: `ok` is false and the
  code and output are there. A tool may declare other codes as fine.
- **K9** Output that is not UTF-8, and CRLF line ends.
- **K10** The output of a command holds a credential from the connector's
  environment. It must not reach the program.

## Project helpers (`helpers.py`, `sandbox_runner.py`, `runtime.py`)

- **P1** A helper calls a tool the policy denies, or one it asks about. The
  call must be decided exactly as if the program had made it: denied, or
  paused with nothing run.
- **P2** A helper file does not parse. Programs that do not use it must
  still run; one that uses it gets an error that names the file and the
  line; `search()` lists the file as broken instead of leaving it out.
- **P3** A helper file is edited while an execution is paused. The resumed
  pass must run the helpers the first pass ran.
- **P4** A file name that cannot be a module name; a file that starts with
  an underscore (private to the project: not loaded); a helpers folder that
  does not exist; helpers larger than the size limit.
- **P5** Helpers use each other and `state`, and `from helpers import name`
  works.
- **P6** `search()` and `describe()` list helper functions with their
  parameters and the first line of their docstring. They are read, never
  run, on the host.
- **P7** A program asks for a helper module that does not exist. The error
  lists the ones that do.

## Setting a project up, and keeping it tidy (`project.py`, `records.py`, `sessions.py`)

- **U1** A server list in each client's shape (`mcpServers`, `servers`):
  entries with a command and arguments, with a URL (`http`, `sse`, no type),
  disabled entries, entries that are not objects; a file that is not JSON
  or has no list.
- **U2** A literal credential in a server's `env` or `headers`. It must
  never be copied into the config or printed; the config gets a reference
  and the person is told which variable to export. A reference stays a
  reference; one with a default loses the default; an editor's input
  reference becomes a variable. A credential under a name that says nothing
  (a password inside a URL, a token with a known prefix, a long opaque
  string) is caught by its value; a path, a URL without a password and a
  list of words are not.
- **U3** Server names that are not identifiers, that are names the sandbox
  uses, or that become the same name after cleaning. The runtime's own
  server in the list must not become a connector.
- **U4** The starting policy: tools a server marks read-only are allowed by
  name; a server that marks nothing is allowed by reading verbs; a tool
  marked destructive is listed under ask; deletes are denied; the default
  is ask.
- **U5** `init` where a config exists (refused without `--force`); with no
  server list and no repository (an error that says what to do); with no
  server list in a repository (a commands starter).
- **U6** `.gitignore`: entries are added once; an existing file keeps its
  line endings; nothing is written outside a repository.
- **U7** `doctor`: a server that is down fails its check with a hint; a rule
  that matches no tool is reported, a deny rule most loudly; a rule for a
  namespace that could not be listed is not called dead; `default: allow`
  and approvals without a terminal are warnings.
- **U8** A project that has never used the runtime is set up with the
  documented commands and nothing else: init, doctor, a first program, a
  write that pauses, an approval that needs a person.
- **U9** `prune`: old finished executions go, with their side files; one
  that a live process is running stays; a paused one goes only for its age;
  `--dry-run` removes nothing; a helper snapshot still in use stays; the
  count limit keeps the newest.
- **U10** Several agents on one data folder: `last` is the caller's own
  execution; two programs finishing at the same moment in one session lose
  none of each other's keys.
- **U11** The instructions for agents show only commands that exist, and
  the adoption guide carries the same text.

## Entry points, command hints, odd files (`main.py`, `launcher.py`, `textio.py`, `cli.py`)

- **E1** The interpreter is started in isolated mode (`python -I main.py`),
  which leaves the script's folder off `sys.path`. The package must still
  import.
- **E2** A hint names a command for a human to run. Pasted as printed, from
  another folder, it must reach the same execution: it has to repeat the
  launcher, `--config` and `--home` of the process that printed it.
- **E3** A config or a program file starts with a byte-order mark (UTF-8 from
  an editor, UTF-16 from a shell redirect), or has CRLF line endings.
- **E4** `run` is given no program and stdin is a pipe that stays open and
  empty. It must fail in bounded time instead of waiting for ever.
- **E5** A usage error with `--json` on the command line must still be one
  JSON object on stdout.
- **E6** `python -m codemode_runtime`, the package's `__main__.py` run as a
  script, and the installed `codemode` entry point must all be the same CLI.
- **E7** The host runs under the oldest Python it supports (3.9) and the
  sandbox under 3.8. A library call that a later Python added works
  everywhere else and fails only there (found: `init` could not write the
  config, because of an argument that file writing gained in 3.10). No
  check on a newer Python can see this: the check is the suites themselves,
  run under those two interpreters. `docs/VERIFICATION.md` says how.

## Fault scenarios (`e2e.py`, from the chaos list)

- **X1** The code loops forever.
- **X2** A tool stalls past its call timeout.
- **X3** The code floods stdout.
- **X4** The code writes straight to file descriptors 1 and 2.
- **X5** The sandbox process exits without finishing (`os._exit`).
- **X6** The executor prints garbage instead of frames.
- **X7** The executor never answers.
- **X8** A tool returns a value over the size limit.
- **X9** The code makes more calls than the limit.
- **X10** The code is not valid Python, or uses `return` or `await` at top
  level.
- **X11** The result is not JSON-serializable.
- **X12** The code changes its behaviour between the paused pass and the
  resumed pass (replay divergence).
- **X13** A second pass is started on an execution that is already running.
- **X14** An MCP server that fails to start, or a tool call the server rejects.
- **X15** An MCP client cancels a running execution.
- **X16** The model endpoint returns an error, or a tool call with malformed
  arguments.
