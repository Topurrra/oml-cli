# SPDX-License-Identifier: MIT
"""The command line.

Built for two readers. A human gets short text. An agent passes ``--json`` and
gets one JSON value on stdout, plus an exit code that says which outcome
happened without parsing anything:

    0 completed      1 failed or rejected    2 usage or config error
    3 paused         4 verify mismatch       5 not found

``--json``, ``--config``, ``--home`` and ``--agent`` are accepted before or
after the command.
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import sys
import threading
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from . import __version__, launcher
from .config import load_config, resolve_agent, resolve_home
from .errors import (
    EXIT_FAILED,
    EXIT_OK,
    EXIT_PAUSED,
    EXIT_RUNNING,
    EXIT_USAGE,
    EXIT_VERIFY_MISMATCH,
    CodemodeError,
    TerminalRequired,
    UsageError,
)
from . import procutil, textio
from .jsonutil import pretty
from .runtime import Runtime, summarize_stats

STATUS_EXIT = {"completed": EXIT_OK, "running": EXIT_OK, "paused": EXIT_PAUSED, "error": EXIT_FAILED, "rejected": EXIT_FAILED}

_EPILOG = """\
exit codes: 0 completed, 1 failed or rejected, 2 usage or config error,
            3 paused for approval, 4 verify mismatch, 5 not found,
            6 still running (wait and cancel only)
with no command, the zero-config demo runs."""


def _out(text: str = "") -> None:
    sys.stdout.write(text + "\n")


def _print_json(value: Any) -> None:
    _out(json.dumps(value, ensure_ascii=True))


def _print_payload(payload: Dict[str, Any]) -> None:
    _out("status: %s    execution: %s" % (payload.get("status"), payload.get("executionId")))
    if "result" in payload:
        _out("result:")
        _out(pretty(payload["result"]) if not isinstance(payload["result"], str) else payload["result"])
    if "select" in payload:
        _out("%s:" % payload["select"])
        _out(payload["value"] if isinstance(payload.get("value"), str) else pretty(payload.get("value")))
    for key in ("resultTruncated", "resultOmitted", "resultIsRepr", "verification", "valueTruncated"):
        if payload.get(key) not in (None, False, "none"):
            _out("%s: %s" % (key, payload[key]))
    for key in ("resultShape", "valueShape", "variables"):
        if payload.get(key):
            _out("%s: %s" % (key, json.dumps(payload[key], ensure_ascii=True)))
    progress = payload.get("progress")
    if progress:
        _out("%s for %ss: %d call(s) done, %d failed" % (progress["phase"], progress["elapsedS"], progress["callsDone"], progress["callsFailed"]))
        for item in progress["running"]:
            _out("  step %s: %s, running for %ss%s" % (item["seq"], item["tool"], item["forS"], ("  [%s]" % item["reported"]) if item.get("reported") else ""))
    for item in payload.get("pending", []):
        _out("pending step %s: %s %s  (rule: %s)" % (item["seq"], item["tool"], json.dumps(item["args"], ensure_ascii=True), item.get("rule")))
    error = payload.get("error")
    if error:
        _out("error: %s: %s" % (error.get("type"), error.get("message")))
        if error.get("traceback"):
            _out(error["traceback"].rstrip())
        if error.get("hint"):
            _out("hint: %s" % error["hint"])
    for grant in payload.get("standingApprovals", []):
        what = "every later call" if "*" in grant["tools"] else "later calls to %s" % ", ".join(grant["tools"])
        _out("approved in advance by %s: %s of this execution%s; %d call(s) so far" % (
            grant.get("by"), what, (" until %s" % grant["expires"]) if grant.get("expires") else "", grant.get("calls", 0)))
    for warning in payload.get("warnings", []):
        _out("warning: %s" % warning)
    if payload.get("logs"):
        _out("logs:")
        _out(payload["logs"].rstrip("\n"))
    if payload.get("hint"):
        _out("hint: %s" % payload["hint"])
    if payload.get("stats"):
        _out("stats: %s" % summarize_stats(payload["stats"], payload))


def _finish(payload: Dict[str, Any], as_json: bool) -> int:
    if as_json:
        _print_json(payload)
    else:
        _print_payload(payload)
    return STATUS_EXIT.get(payload.get("status", ""), EXIT_FAILED)


def _tty_approver(request: Dict[str, Any]) -> Optional[bool]:
    """Ask the person at the terminal. Anything but yes is a refusal: the
    safe answer must be the default one."""
    sys.stderr.write(
        "\napproval needed for step %s: %s\narguments: %s\napprove? [y/N] "
        % (request["seq"], request["tool"], json.dumps(request["args"], ensure_ascii=True)[:2000])
    )
    sys.stderr.flush()
    return sys.stdin.readline().strip().lower() in ("y", "yes")


_NO_PROGRAM_HINT = (
    "Write the program to a file with your editor or file tool and pass it with -f FILE. "
    "A short one can be the argument; - reads it from stdin."
)
_IMPLICIT_STDIN_WAIT_S = 5.0


def _read_stdin(wait_s: Optional[float]) -> str:
    """Read the program from stdin. With ``wait_s`` the first byte must
    arrive in time: a shell that leaves stdin open and empty would otherwise
    hang the command for ever, with nothing to say why."""
    if wait_s is None:
        return textio.decode_text(sys.stdin.buffer.read())
    box: "queue.Queue[Any]" = queue.Queue()

    def pump() -> None:
        # The raw descriptor, not sys.stdin.buffer: a thread left blocked
        # inside the buffered reader holds its lock, and the interpreter then
        # aborts at exit instead of returning the exit code.
        try:
            fd = sys.stdin.fileno()
            chunks = []
            while True:
                chunk = os.read(fd, 65536)
                if not chunk:
                    break
                chunks.append(chunk)
                box.put(None)  # something arrived: the wait is over
            box.put(b"".join(chunks))
        except (OSError, ValueError) as exc:
            box.put(exc)

    threading.Thread(target=pump, daemon=True).start()
    try:
        data = box.get(timeout=wait_s)
    except queue.Empty:
        raise UsageError(
            "no program given: stdin is open but nothing arrived on it within %gs" % wait_s, hint=_NO_PROGRAM_HINT
        )
    while data is None:  # the first bytes came in time; now read to the end
        data = box.get()
    if isinstance(data, Exception):
        raise UsageError("cannot read the program from stdin: %s" % data, hint=_NO_PROGRAM_HINT)
    return textio.decode_text(data)


def _read_code(args: argparse.Namespace) -> str:
    try:
        if args.file:
            return textio.read_text(args.file)
        if args.code and args.code != "-":
            return args.code
        if args.code == "-" or not sys.stdin.isatty():
            if getattr(args, "interactive", False):
                raise UsageError(
                    "--interactive needs stdin for the approval prompt", hint="Pass the program as an argument or with -f."
                )
            # An explicit - waits as long as it takes; a bare pipe does not.
            return _read_stdin(None if args.code == "-" else _IMPLICIT_STDIN_WAIT_S)
    except OSError as exc:
        raise UsageError("cannot read %s: %s" % (args.file, exc), hint=_NO_PROGRAM_HINT)
    except UnicodeDecodeError as exc:
        raise UsageError(
            "the program is not valid UTF-8 text: %s" % exc,
            hint="Save the file as UTF-8. A file written by a shell redirect may be in another encoding.",
        )
    raise UsageError("no program given", hint=_NO_PROGRAM_HINT)


# -- commands -------------------------------------------------------------


def _require_terminal(runtime: Runtime, what: str, give_user: str) -> None:
    """Approving is for a person. An agent that drives this CLI through a
    shell tool has no terminal on stdin (a pipe, a file or the null device),
    so the default refuses it there: an agent must not be able to approve its
    own request by running one more command. A project that approves from
    scripts on purpose says so in its config, which is a file the agent is
    told not to edit.

    This tells a shell tool from a terminal, not an agent from a person: a
    tool that types into a real terminal passes. See docs/ADOPTING.md."""
    if not runtime.config.approval["require_terminal"]:
        return
    if procutil.stdin_is_terminal():
        return
    raise TerminalRequired(
        "%s must be run by a person at a terminal; this process has no terminal on stdin" % what,
        hint="If you are an agent: do not approve your own request. Tell the user what is pending and give them "
        "this command to run in their own terminal: %s    If you are the user and approve from a script on "
        'purpose, set "approval": {"require_terminal": false} in the config. If you are the user at a terminal '
        "and still see this, that window gives programs a pipe instead of a terminal (some POSIX-style shells "
        "on Windows do): run the command from another one." % give_user,
    )


def _program_args(args: argparse.Namespace) -> Dict[str, Any]:
    """Collect ``--args-file``, ``--args-json`` and ``--arg NAME=VALUE`` into
    the dict the program sees as ``args``; later sources win."""
    out: Dict[str, Any] = {}
    for label, text in (("--args-file", None if not args.args_file else _read_args_file(args.args_file)), ("--args-json", args.args_json)):
        if text is None:
            continue
        try:
            value = json.loads(text)
        except ValueError as exc:
            raise UsageError("%s is not valid JSON: %s" % (label, exc), hint='Pass an object, for example {"app": "web"}.')
        if not isinstance(value, dict):
            raise UsageError("%s must be a JSON object" % label, hint='For example {"app": "web"}.')
        out.update(value)
    for item in args.arg or []:
        name, eq, text = item.partition("=")
        if not eq or not name:
            raise UsageError(
                "--arg takes NAME=VALUE, got %r" % item,
                hint="VALUE is read as JSON when it is valid JSON (3, true, [1, 2]) and as text otherwise.",
            )
        try:
            out[name] = json.loads(text)
        except ValueError:
            out[name] = text
    return out


def _read_args_file(path: str) -> str:
    try:
        return textio.read_text(path)
    except (OSError, UnicodeDecodeError) as exc:
        raise UsageError("cannot read --args-file %s: %s" % (path, exc))


def _print_selected(payload: Dict[str, Any], as_json: bool) -> int:
    """With ``--select`` and no ``--json`` the value is printed alone: text
    as it is, anything else as JSON. Nothing else needs to parse the output."""
    if as_json or "select" not in payload:
        return _finish(payload, as_json)
    value = payload.get("value")
    _out(value if isinstance(value, str) else json.dumps(value, ensure_ascii=True))
    if payload.get("valueTruncated"):
        sys.stderr.write("note: cut to the output budget (%s chars in all); select a smaller part or pass --full\n" % payload.get("valueChars"))
    return STATUS_EXIT.get(payload.get("status", ""), EXIT_FAILED)


def _detach(runtime: Runtime, exec_id: str) -> Dict[str, Any]:
    """Start a worker process that runs the recorded execution and outlives
    this command. Returns the outcome so far, which is normally "running".

    A program that takes minutes cannot be run in the foreground by a caller
    whose shell gives a command two minutes. Detached, the caller gets the id
    at once and asks again with ``wait`` and ``get``.
    """
    from .detach import spawn_worker

    return spawn_worker(runtime, exec_id)


def cmd_worker(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    """Run an execution that ``run --detach`` recorded. Not for people."""
    payload = runtime.run_started(args.id)
    return STATUS_EXIT.get(payload.get("status", ""), EXIT_FAILED)


def cmd_wait(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    payload = runtime.wait(args.id, args.timeout)
    code = _finish(payload, as_json)
    return EXIT_RUNNING if payload.get("status") == "running" else code


def cmd_cancel(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    payload = runtime.cancel(args.id, by=args.by)
    code = _finish(payload, as_json)
    return EXIT_RUNNING if payload.get("status") == "running" else code


def cmd_run(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    if args.interactive:
        _require_terminal(runtime, "run --interactive", launcher.command("run", "--interactive", "-f", "FILE"))
    code = _read_code(args)
    if args.check:
        return _finish(runtime.check(code), as_json)
    if args.detach:
        if args.interactive:
            raise UsageError("--detach and --interactive do not go together", hint="A detached run pauses when a call needs approval; approve it afterwards.")
        return _finish(_detach(runtime, runtime.start(code, args.session, args=_program_args(args), reuse=args.reuse)), as_json)
    approver = _tty_approver if args.interactive else None
    payload = runtime.execute(code, args.session, approver=approver, args=_program_args(args), reuse=args.reuse)
    if args.select and payload.get("executionId"):
        return _print_selected(runtime.get(payload["executionId"], select=args.select), as_json)
    return _finish(payload, as_json)


def cmd_get(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    return _print_selected(runtime.get(args.id, full=args.full, select=args.select), as_json)


def cmd_approve(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    _require_terminal(runtime, "approve", launcher.command("approve", args.id))
    standing = "all" if args.all_remaining else ("same-tool" if args.same_tool else None)
    payload = runtime.approve(args.id, by=args.by, stale_ok=args.stale, standing=standing)
    code = _finish(payload, as_json)
    if not as_json and payload.get("status") == "paused" and standing is None and runtime.config.standing != "off":
        # Said to the person, at the moment it matters, and nowhere an agent
        # reads: the hint of a pause does not mention it.
        sys.stderr.write(
            "note: it paused again. To approve later calls to the same tools along with the pending ones, for this "
            "execution only: %s\n" % launcher.command("approve", str(payload.get("executionId")), "--same-tool")
        )
    return code


def cmd_reject(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    return _finish(runtime.reject(args.id, by=args.by, reason=args.reason), as_json)


def cmd_verify(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    report = runtime.verify(args.id)
    if as_json:
        _print_json(report)
    else:
        _out("%s: %s" % (report["verification"], report["detail"]))
    return EXIT_OK if report["verified"] else EXIT_VERIFY_MISMATCH


def cmd_executions(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    rows = runtime.list_executions(args.limit)
    if as_json:
        _print_json(rows)
    elif not rows:
        _out("no executions yet")
    else:
        for row in rows:
            _out("%(executionId)s  %(status)-9s  session=%(session)s  agent=%(agent)s  calls=%(toolCalls)d  verified=%(verification)s" % row)
    return EXIT_OK


def cmd_show(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    """The audit log of one execution, entry by entry."""
    entries = runtime.store.read(runtime.store.resolve_id(args.id, runtime.agent))
    if as_json:
        _print_json(entries)
        return EXIT_OK
    for entry in entries:
        kind = entry.get("type")
        if kind == "start":
            _out("%s  session=%s  agent=%s  policy=%s" % (entry["id"], entry["session"], entry["agent"], entry["policy_mode"]))
        elif kind == "pass_start":
            _out("pass %s" % entry["pass"])
        elif kind == "call":
            detail = ""
            if entry.get("decision"):
                detail = "%s(%s)%s" % (entry["decision"], entry.get("rule"), "" if entry.get("enforced", True) else " not enforced")
            if entry.get("state") in ("error", "denied"):
                detail += "  %s" % (entry.get("error") or {}).get("type")
            if "duration_ms" in entry:
                detail += "  %d ms" % entry["duration_ms"]
            if entry.get("result_bytes"):
                detail += "  %d B" % entry["result_bytes"]
            _out("  %3d  %-8s %-28s %-9s %s" % (entry["seq"], entry["kind"], entry["name"][:28], entry["state"], detail))
        elif kind == "approval":
            more = ""
            if entry.get("standing"):
                more = ", and in advance of %s" % ("every later call" if "*" in entry["standing"] else "later calls to %s" % ", ".join(entry["standing"]))
            elif entry.get("via") == "standing":
                more = ", under the approval given in advance at %s" % entry.get("granted")
            _out("  approval of step(s) %s by %s%s" % (", ".join(str(s) for s in entry["seqs"]), entry["by"], more))
        elif kind == "pass_end":
            _out("  pass ended: %s" % entry["status"])
        elif kind == "end":
            _out("end: %s" % entry["status"])
        elif kind == "verify":
            _out("verify: %s %s" % ("ok" if entry["ok"] else "FAILED", entry.get("detail", "")))
    return EXIT_OK


def cmd_search(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    from .errors import NotFoundError, ToolFailure

    try:
        hits, total = runtime.catalog.search_with_total(args.query, args.limit, args.namespace)
    except ToolFailure as failure:  # only with --namespace: one that does not exist, or whose server does not answer
        if failure.kind == "UnknownTool":
            raise NotFoundError(failure.message, failure.hint)
        error = CodemodeError(failure.message, failure.hint)
        error.kind = failure.kind
        raise error
    if as_json:
        _print_json(hits)
    else:
        for hit in hits:
            _out("%s    [%s]" % (hit["signature"], hit["policy"]))
            if hit["description"]:
                _out("    %s" % hit["description"])
    if total > len(hits):
        # On stderr, so the JSON on stdout stays the list it has always been.
        sys.stderr.write("showing %d of %d matches; pass --limit %d for all of them, or narrow the query\n" % (len(hits), total, total))
    return EXIT_OK


def cmd_describe(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    from .errors import NotFoundError, ToolFailure

    try:
        detail = runtime.describe(args.target)
    except ToolFailure as failure:
        raise NotFoundError(failure.message)
    _print_json(detail) if as_json else _out(pretty(detail))
    return EXIT_OK


def cmd_prompt(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    """Print the tool description, for a harness that wants to register the
    tool itself instead of going through MCP."""
    text = runtime.tool_description()
    _print_json({"name": "execute", "description": text}) if as_json else _out(text)
    return EXIT_OK


def cmd_config(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    detail = dict(runtime.config.describe(), home=str(runtime.home), agent=runtime.agent)
    _print_json(detail) if as_json else _out(pretty(detail))
    return EXIT_OK


_MARKS = {"ok": "ok  ", "warn": "WARN", "fail": "FAIL"}


def cmd_init(args: argparse.Namespace, as_json: bool) -> int:
    """Write codemode.json for the project in the current folder. Runs
    before any config is loaded: there is none yet."""
    from .project import init

    home = getattr(args, "home", None)
    if args.out:
        # The config is written where no later command looks by itself, so
        # every command this one prints has to name it.
        launcher.set_global_options(args.out, home)
    report = init(Path.cwd(), source=args.source, force=args.force, home=Path(home) if home else None, out=args.out, work_dir=args.work_dir)
    if as_json:
        _print_json(report)
        return EXIT_OK
    _out("wrote %s%s" % (report["config"], ("  (from %s)" % report["from"]) if report["from"] else ""))
    for name, info in report["connectors"].items():
        if "error" in info:
            _out("  [FAIL] %s (%s): %s" % (name, info["type"], info["error"]))
            if info.get("hint"):
                _out("         %s" % info["hint"])
        else:
            _out("  [ok  ] %s (%s): %d tools, %.1fs" % (name, info["type"], info["tools"], info["seconds"]))
    policy = report["policy"]
    _out("policy: default %s; allowed without asking: %s; never: %s" % (
        policy["default"], ", ".join(policy["allow"]) or "nothing", ", ".join(policy["deny"]) or "nothing"))
    _out(".gitignore: %s" % report["gitignore"])
    for note in report["notes"]:
        _out("note: %s" % note)
    _out("next:")
    for step in report["next"]:
        _out("  - %s" % step)
    return EXIT_OK


def cmd_doctor(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    from .project import doctor

    if args.refresh:
        runtime.clear_tool_cache()
    report = doctor(runtime.config, runtime.home, runtime.executor, runtime.guidance)
    if as_json:
        _print_json(report)
    else:
        for check in report["checks"]:
            _out("[%s] %s: %s" % (_MARKS[check["status"]], check["check"], check["detail"]))
            if check.get("hint"):
                _out("       %s" % check["hint"])
        failed = sum(1 for c in report["checks"] if c["status"] == "fail")
        warned = sum(1 for c in report["checks"] if c["status"] == "warn")
        _out("%s%s" % ("all checks passed" if not failed else "%d check(s) failed" % failed, (", %d warning(s)" % warned) if warned else ""))
    return EXIT_OK if report["ok"] else EXIT_FAILED


def cmd_prune(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    report = runtime.prune(max_age_days=args.older_than, max_executions=args.max_executions, dry_run=args.dry_run)
    if as_json:
        _print_json(report)
    else:
        _out("%s %d execution(s), %.1f MB; %d kept" % (
            "would remove" if report["dryRun"] else "removed", report["removed"], report["freedBytes"] / 1e6, report["kept"]))
    return EXIT_OK


def cmd_instructions(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    """The block of instructions for this project's agents, to paste into
    the file where the project keeps them, with the command line this
    project uses."""
    from .instructions import instructions
    from .project import folder_in_project

    # The folders as this project has them: the instructions tell an agent
    # where to write programs and which folder to leave alone.
    data = folder_in_project(runtime.home, runtime.config.root or Path.cwd()) or launcher._portable(os.path.abspath(str(runtime.home)))
    text = instructions(launcher.command(), "mcp" if args.mcp else "cli", runtime.config.work_dir, data, runtime.guidance()["text"])
    _print_json({"instructions": text}) if as_json else _out(text)
    return EXIT_OK


def cmd_serve(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    from .mcp_server import serve

    serve(runtime)
    return EXIT_OK


def cmd_agent(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    from .llm_agent import run_agent

    def emit(event: Dict[str, Any]) -> None:
        if as_json:
            _print_json(event)
            sys.stdout.flush()
        elif event["event"] == "tool":
            sys.stderr.write("turn %s: execute -> %s %s\n" % (event["turn"], event["status"], event.get("error") or ""))

    if args.interactive:
        _require_terminal(runtime, "agent --interactive", launcher.command("agent", "--interactive", "TASK"))
    approver = _tty_approver if args.interactive else None
    finished, text = run_agent(runtime, args.task, args.session, args.max_turns, emit, approver)
    if not as_json:
        _out(text)
    return EXIT_OK if finished else EXIT_FAILED


def cmd_snippet(runtime: Runtime, args: argparse.Namespace, as_json: bool) -> int:
    if args.action == "save":
        if not args.name or not args.source:
            raise UsageError("snippet save needs a name and --from", hint=launcher.command("snippet", "save", "NAME", "--from", "EXECUTION_ID"))
        saved = runtime.save_snippet(args.name, args.source, args.description)
        _print_json(saved) if as_json else _out("saved snippet %s from %s" % (saved["name"], saved["sourceExecution"]))
    elif args.action == "delete":
        if not args.name:
            raise UsageError("snippet delete needs a name")
        runtime.snippets.delete(args.name)
        _print_json({"deleted": args.name}) if as_json else _out("deleted snippet %s" % args.name)
    else:
        rows = runtime.snippets.list()
        if as_json:
            _print_json(rows)
        else:
            for row in rows:
                _out("%s    %s  (from %s)" % (row["name"], row.get("description", ""), row.get("sourceExecution")))
            if not rows:
                _out("no snippets yet")
    return EXIT_OK


COMMANDS: Dict[str, Callable[[Runtime, argparse.Namespace, bool], int]] = {
    "run": cmd_run,
    "_worker": cmd_worker,
    "wait": cmd_wait,
    "cancel": cmd_cancel,
    "get": cmd_get,
    "approve": cmd_approve,
    "reject": cmd_reject,
    "verify": cmd_verify,
    "executions": cmd_executions,
    "show": cmd_show,
    "search": cmd_search,
    "describe": cmd_describe,
    "prompt": cmd_prompt,
    "config": cmd_config,
    "doctor": cmd_doctor,
    "prune": cmd_prune,
    "instructions": cmd_instructions,
    "serve": cmd_serve,
    "agent": cmd_agent,
    "snippet": cmd_snippet,
}


class _Parser(argparse.ArgumentParser):
    """A usage error is an outcome like any other: with ``--json`` on the
    command line it is one JSON object on stdout, not only text on stderr."""

    json_errors = False

    def error(self, message: str) -> Any:
        if _Parser.json_errors:
            _print_json({"status": "error", "error": {
                "type": "UsageError",
                "message": message,
                "hint": "See: %s --help" % self.prog,
            }})
            sys.stdout.flush()
            self.exit(EXIT_USAGE)
        super().error(message)


def build_parser() -> argparse.ArgumentParser:
    # SUPPRESS keeps a flag given before the command from being overwritten by
    # the default of the same flag on the subcommand.
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--json", action="store_true", default=argparse.SUPPRESS, help="print one JSON value on stdout")
    common.add_argument("--config", default=argparse.SUPPRESS, metavar="FILE", help="config file (default: ./codemode.json, else the built-in demo config)")
    common.add_argument("--home", default=argparse.SUPPRESS, metavar="DIR", help="data directory (default: ./.codemode)")
    common.add_argument("--agent", default=argparse.SUPPRESS, metavar="NAME", help="who is acting; recorded in the audit log")

    parser = _Parser(
        prog=launcher.program_name(),
        description="codemode-runtime %s: one tool, execute(code), for any agent." % __version__,
        epilog=_EPILOG,
        formatter_class=argparse.RawDescriptionHelpFormatter,
        parents=[common],
    )
    parser.add_argument("--version", action="version", version="codemode-runtime %s" % __version__)
    sub = parser.add_subparsers(dest="command", metavar="COMMAND", parser_class=_Parser)

    def add(name: str, text: str) -> argparse.ArgumentParser:
        return sub.add_parser(name, help=text, description=text, parents=[common])

    p = add("demo", "run the zero-config demo (also what runs with no command)")
    p.add_argument("--keep", action="store_true", help="keep the demo's data directory")

    p = add("run", "run a program")
    p.add_argument("code", nargs="?", help="the program, or - to read it from stdin")
    p.add_argument("-f", "--file", help="read the program from a file")
    p.add_argument("--session", default="default", help="which state dict the program sees")
    p.add_argument("--interactive", action="store_true", help="ask on the terminal when a call needs approval")
    p.add_argument("--arg", action="append", metavar="NAME=VALUE", help="an argument for the program, read there as args[NAME]; repeatable")
    p.add_argument("--args-json", metavar="JSON", help="arguments for the program as one JSON object")
    p.add_argument("--args-file", metavar="FILE", help="arguments for the program from a JSON file")
    p.add_argument("--select", metavar="PATH", help="print one part of the outcome, such as result.count or variables.res")
    p.add_argument("--detach", action="store_true",
                   help="return at once with the execution id and keep running in the background; follow it with wait and get")
    p.add_argument("--check", action="store_true", help="do not run: compile the program and look up the tools it names")
    p.add_argument("--reuse", metavar="EXECUTION_ID",
                   help="take results from this earlier execution for as long as the program makes the same requests in the "
                        "same order; use it to continue a fixed program without repeating the calls that worked")

    p = add("wait", "wait for a running execution to stop, then show its outcome (exit 6 if it is still running)")
    p.add_argument("id", help="execution id, a unique prefix, or 'last'")
    p.add_argument("--timeout", type=float, default=60.0, metavar="SECONDS",
                   help="how long to wait this time (default 60); keep it under your own command timeout and call again")

    p = add("cancel", "stop a running execution; calls that already ran stay applied")
    p.add_argument("id")
    p.add_argument("--by", default="human", help="who cancelled; recorded in the audit log")

    p = sub.add_parser("_worker", parents=[common])  # started by run --detach; not listed in --help
    p.add_argument("id")

    p = add("get", "show the current outcome of an execution")
    p.add_argument("id", help="execution id, a unique prefix, or 'last'")
    p.add_argument("--full", action="store_true", help="do not cut the result to the output budget")
    p.add_argument("--select", metavar="PATH", help="print one part: result.rows.0, variables.res, logs, error.message, args, code")

    p = add("approve", "approve the pending call(s) of a paused execution and resume it (for a person, at a terminal)")
    p.add_argument("id")
    p.add_argument("--by", default="human", help="who approved; recorded in the audit log")
    p.add_argument("--stale", action="store_true", help="approve even if the pause is older than approval.ttl_s")
    wider = p.add_mutually_exclusive_group()
    wider.add_argument("--same-tool", action="store_true",
                       help="also approve, in advance, later calls of this execution to the tools approved now, whatever their arguments")
    wider.add_argument("--all-remaining", action="store_true",
                       help="also approve, in advance, every later call of this execution that the policy asks about; you will not see them first")

    p = add("reject", "refuse a paused execution; calls that already ran stay applied")
    p.add_argument("id")
    p.add_argument("--by", default="human")
    p.add_argument("--reason", default="")

    p = add("verify", "replay a completed execution from its log and compare the result")
    p.add_argument("id")

    p = add("executions", "list recent executions")
    p.add_argument("--limit", type=int, default=20)

    p = add("show", "print the audit log of an execution")
    p.add_argument("id")

    p = add("search", "find tools, as a program would with search()")
    p.add_argument("query", nargs="?", default="")
    p.add_argument("--limit", type=int, default=None, help="default: 10 for a query, 50 for an empty one")
    p.add_argument("--namespace", metavar="NAME", default=None,
                   help="only the tools of this namespace (or helpers); with no query, everything that server offers")

    p = add("describe", "describe a tool, a namespace or snippet:NAME")
    p.add_argument("target")

    p = add("init", "set this project up: write codemode.json from its MCP server list, check the servers, update .gitignore")
    p.add_argument("--from", dest="source", metavar="FILE", help="the MCP server list to read (default: .mcp.json, or mcp.json in a dot-folder of the project)")
    p.add_argument("--force", action="store_true", help="write the config even if there is one")
    p.add_argument("--out", metavar="FILE", default=None,
                   help="write the config to this file instead of codemode.json in the current folder; later commands then name it with --config")
    p.add_argument("--work-dir", metavar="DIR", default=None,
                   help="the folder agents write their programs into (default: .codemode-work); kept in the config and out of version control")

    p = add("doctor", "check the setup: interpreter, config, data folder, sandbox, every connector, policy, records")
    p.add_argument("--refresh", action="store_true", help="forget cached tool lists first")

    p = add("prune", "remove old execution records")
    p.add_argument("--older-than", type=float, default=None, metavar="DAYS", help="remove executions that ended more than DAYS ago (default: retention.max_age_days)")
    p.add_argument("--max-executions", type=int, default=None, metavar="N", help="keep at most the N newest (default: retention.max_executions)")
    p.add_argument("--dry-run", action="store_true", help="say what would be removed, and remove nothing")

    p = add("instructions", "print the instructions for this project's agents, to paste into its instructions file (AGENTS.md or the like)")
    p.add_argument("--mcp", action="store_true", help="for an agent that reaches the runtime as an MCP server, not through the command line")

    add("prompt", "print the description of the execute tool")
    add("config", "print the resolved configuration")
    add("serve", "serve the execute tool over MCP (stdio)")

    p = add("agent", "run a task with a model on any OpenAI-compatible endpoint")
    p.add_argument("task")
    p.add_argument("--session", default="default")
    p.add_argument("--max-turns", type=int, default=None)
    p.add_argument("--interactive", action="store_true", help="ask on the terminal when a call needs approval")

    p = add("snippet", "save, list or delete reusable programs")
    p.add_argument("action", choices=["save", "list", "delete"])
    p.add_argument("name", nargs="?")
    p.add_argument("--from", dest="source", metavar="EXECUTION_ID", help="the completed execution to promote")
    p.add_argument("--description", default="")
    return parser


def main(argv: Optional[list] = None) -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            # A result may contain characters the console cannot encode;
            # printing it must degrade, not raise.
            stream.reconfigure(errors="backslashreplace")  # type: ignore[union-attr]
        except (AttributeError, ValueError):
            pass
    raw = list(sys.argv[1:] if argv is None else argv)
    _Parser.json_errors = "--json" in raw
    args = build_parser().parse_args(raw)
    as_json = bool(getattr(args, "json", False))
    command = args.command or "demo"
    launcher.set_global_options(getattr(args, "config", None), getattr(args, "home", None))
    try:
        if command == "demo":
            from .demo import run_demo

            return run_demo(as_json, keep=bool(getattr(args, "keep", False)))
        if command == "init":
            return cmd_init(args, as_json)
        config = load_config(getattr(args, "config", None))
        home = resolve_home(getattr(args, "home", None), None, config)
        if config.root is not None:
            # Found by searching upward: say so in every hint, with absolute
            # paths, so a pasted command works from any folder.
            launcher.set_global_options(str(config.path), str(home))
        runtime = Runtime(config, home, resolve_agent(getattr(args, "agent", None), config))
        try:
            return COMMANDS[command](runtime, args, as_json)
        finally:
            runtime.close()
    except CodemodeError as exc:
        if as_json:
            _print_json({"status": "error", "error": exc.to_dict()})
        else:
            sys.stderr.write("error: %s\n" % exc.message)
            if exc.hint:
                sys.stderr.write("hint: %s\n" % exc.hint)
        return exc.exit_code
    except KeyboardInterrupt:
        return 130
