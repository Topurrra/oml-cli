# SPDX-License-Identifier: MIT
"""The runtime: the public API every surface uses.

The CLI, the MCP server and the agent loop all call the same few methods
here, so they cannot drift apart: ``execute``, ``get``, ``approve``,
``reject``, ``verify``. Each returns a plain dict (the payload) whose
``status`` is one of ``completed``, ``paused``, ``error``, ``rejected`` or
``running``.

``approve`` and ``reject`` are reachable from the CLI only. They are not
sandbox functions and not MCP tools: an agent must not be able to approve its
own request.
"""

from __future__ import annotations

import json
import os
import threading
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

from . import __version__, launcher
from .catalog import Catalog, ToolListCache
from .config import Config
from .connectors import build_connector
from .engine import Approver, Pass, PassOutcome, check_program
from .errors import BusyError, CodemodeError, NotFoundError, StateError, ToolFailure, UsageError
from .executor import SubprocessExecutor
from .helpers import describe_helpers, load_helpers
from .jsonutil import byte_len, canonical, lookup, sha256_hex
from .records import ExecutionStore, ExecutionView, parse_ts
from .sessions import SessionStore, SnippetStore, check_session_name
from .truncate import budget_result, shape_of, truncate_text

_ARGS_PREVIEW_CHARS = 2000
# How long an execution may sit recorded with no process holding its lock
# before it counts as never started. Starting a worker takes well under this.
_START_GRACE_S = 20.0

_TOOL_DESCRIPTION = """\
Run a Python program that calls tools. Write one program per step, not one call per step.

Inside the program:
- tools.<namespace>.<tool>(**arguments) calls a tool and returns a plain Python value. \
call("namespace.tool", {...}) does the same by name; with on_error="return" a failure comes \
back as a ToolError value instead of raising.
- parallel([("namespace.tool", {...}), ...]) runs independent calls at the same time and \
returns their results in order. repeat("namespace.tool", {...}, n=10) runs one call n times \
and returns only the failures.
- search("words") finds tools; describe("namespace.tool") returns the signature and schema. \
Look a tool up before you call it for the first time. shape(value) shows the keys and types \
of a result.
- grep(text, pattern), tail(text, n) and summarize(text) cut long text output down.
- state is a dict that persists between programs in this session. args holds the arguments \
this program was started with.
- step("name", fn) records the value of fn(). Use it for the time, randomness and anything \
else that would differ if the program ran again.
- run("snippet", **args) runs a saved snippet.
- print() goes to the logs. The value of the last expression, at the top level, is the result. \
Return only what you need: filter and aggregate in the program.

Calls are synchronous (no await). A failed or denied call raises ToolError(kind, message), \
which the program can catch; uncaught, it ends the program.

Outcomes: {"status": "completed", "result": ...}, {"status": "error", "error": ...}, or \
{"status": "paused", "pending": [...]} when a call needs human approval. When paused, nothing \
pending has run: tell the user, then fetch the outcome later by calling this tool with \
{"executionId": "..."} instead of code."""


class Runtime:
    def __init__(self, config: Config, home: Path, agent: str = "anonymous") -> None:
        self.config = config
        self.home = Path(home)
        self.agent = agent
        self.limits = config.limits
        self.store = ExecutionStore(self.home)
        self.sessions = SessionStore(self.home)
        self.snippets = SnippetStore(self.home)
        self.executor = SubprocessExecutor(config.executor_command, config.executor_env_passthrough)
        self.surface = "cli"  # "mcp" when served over MCP: hints then name the tool, not a command
        self._catalog: Optional[Catalog] = None
        self._catalog_lock = threading.Lock()

    # -- wiring -----------------------------------------------------------

    @property
    def catalog(self) -> Catalog:
        """Built on first use: commands that only read the log start no
        connector and no MCP server."""
        with self._catalog_lock:  # two requests of an MCP client may arrive together
            return self._build_catalog()

    def _build_catalog(self) -> Catalog:
        if self._catalog is None:
            connectors = {
                name: build_connector(name, spec, self.home, float(self.limits["startup_timeout_s"]), self.config.root)
                for name, spec in self.config.connectors.items()
            }
            gates = {name: spec["max_concurrency"] for name, spec in self.config.connectors.items() if spec.get("max_concurrency")}
            hint = ""
            if self.config.path is None:
                hint = (
                    "No codemode.json was found in this folder or above it, so the built-in demo config is in use. "
                    "Write one for this project with: %s" % launcher.command("init")
                )
            cache = ToolListCache(self.home / "cache", float(self.limits["catalog_cache_s"]))
            # A described tool must fit in one value with room to spare.
            schema_chars = min(60000, int(self.limits["max_value_bytes"]) * 3 // 5)
            self._catalog = Catalog(
                connectors, self.config.policy, self.snippets.list, gates, cache, hint, schema_chars,
                lambda: describe_helpers(self._current_helpers()),
            )
        return self._catalog

    # -- project helpers ---------------------------------------------------

    def _current_helpers(self) -> Dict[str, str]:
        """The project's helper modules as they are on disk now."""
        folder = self.config.helpers_dir
        if folder is None:
            return {}
        return load_helpers(folder, int(self.limits["max_value_bytes"]))

    def _keep_helpers(self, sources: Dict[str, str]) -> Optional[str]:
        """Store one set of helper sources under its hash and return the
        hash. An execution records the hash, not the sources: hundreds of
        executions with the same helpers share one file."""
        if not sources:
            return None
        text = canonical(sources)
        digest = sha256_hex(text)
        path = self.home / "helpers" / (digest + ".json")
        if not path.is_file():
            path.parent.mkdir(parents=True, exist_ok=True)
            tmp = path.with_name("%s.%d.tmp" % (path.name, os.getpid()))
            tmp.write_text(text, encoding="ascii")
            os.replace(tmp, path)
        return digest

    def _helpers_of(self, start: Dict[str, Any]) -> Dict[str, str]:
        """The helpers an execution started with. A resumed pass and a
        replay run these, whatever the project's files say by now: the
        program must see the same code both times."""
        digest = start.get("helpers_sha256")
        if not digest:
            return {}
        try:
            return json.loads((self.home / "helpers" / (str(digest) + ".json")).read_text(encoding="ascii"))
        except (OSError, ValueError):
            raise StateError(
                "the helper modules execution %s started with are no longer in the data folder" % start.get("id"),
                hint="Run the program again; it will use the helpers as they are now.",
            )

    def close(self) -> None:
        if self._catalog is not None:
            self._catalog.close()
            self._catalog = None

    def _resolve(self, ref: str) -> str:
        """An execution id from what the caller wrote: an id, a unique
        prefix, or ``last``, which is the last execution of this agent."""
        return self.store.resolve_id(ref, self.agent)

    def clear_tool_cache(self) -> None:
        """Forget the tool lists kept on disk, so the next lookup asks the
        servers."""
        ToolListCache(self.home / "cache", 1.0).clear()

    def prune(self, max_age_days: Optional[float] = None, max_executions: Optional[int] = None, dry_run: bool = False) -> Dict[str, Any]:
        """Remove old execution records. The defaults are the config's
        ``retention`` limits; 0 turns a limit off."""
        age = self.config.retention["max_age_days"] if max_age_days is None else max_age_days
        count = self.config.retention["max_executions"] if max_executions is None else max_executions
        if age < 0 or count < 0:
            raise UsageError("the limits of prune cannot be negative", hint="Use 0 to turn a limit off.")
        return self.store.prune(float(age) * 86400.0, int(count), dry_run)

    def tool_description(self) -> str:
        """The whole static prompt. Short on purpose: it names the namespaces
        and the sandbox functions, and leaves every tool to be discovered."""
        lines = []
        for name, info in self.catalog.namespaces().items():
            lines.append("%s (%s)" % (name, ("%d tools" % info) if isinstance(info, int) else info))
        text = _TOOL_DESCRIPTION + "\n\nNamespaces: " + ", ".join(lines) + "."
        try:
            modules = sorted(self._current_helpers())
        except CodemodeError:
            modules = []
        if modules:
            text += (
                " This project's own functions are called as helpers.<module>.<function>(); modules: %s. search() lists them."
                % ", ".join(modules[:30])
            )
        return text

    # -- the five operations ---------------------------------------------

    def execute(
        self,
        code: str,
        session: str = "default",
        approver: Optional[Approver] = None,
        cancel_event: Optional[threading.Event] = None,
        args: Optional[Dict[str, Any]] = None,
        reuse: Optional[str] = None,
    ) -> Dict[str, Any]:
        exec_id = self.start(code, session, args=args, reuse=reuse)
        with self.store.lock(exec_id):
            return self._run_pass(exec_id, approver, cancel_event)

    def start(self, code: str, session: str = "default", args: Optional[Dict[str, Any]] = None, reuse: Optional[str] = None) -> str:
        """Record a new execution and return its id. Nothing runs yet."""
        if not isinstance(code, str) or not code.strip():
            raise UsageError("the program is empty", hint="Pass Python source as the code.")
        if len(code.encode("utf-8", "replace")) > int(self.limits["max_value_bytes"]):
            raise UsageError("the program is larger than limits.max_value_bytes")
        check_session_name(session)
        args = self._check_args(args)
        if self._catalog is not None:
            # In a process that serves many executions: a server whose script
            # was edited since its tools were listed is stopped and listed
            # again, so this program talks to the code that is on disk now.
            self._catalog.refresh_changed()
        reuse_from = self._resolve(reuse) if reuse else None
        exec_id = self.store.new_id()
        policy = self.config.policy
        self.store.append(exec_id, {
            "type": "start",
            "id": exec_id,
            "session": session,
            "agent": self.agent,  # stamped here, on the host; the program cannot set it
            "code": code,
            "code_sha256": sha256_hex(code),
            "args": args,  # part of the input: a resumed pass must see the same ones
            "helpers_sha256": self._keep_helpers(self._current_helpers()),  # and the same helper code
            "reuse_from": reuse_from,
            "state_in": self.sessions.load(session),
            "limits": self.limits,
            "policy_sha256": self._policy_hash(),
            "policy_mode": policy.mode,
            "connectors": sorted(self.config.connectors),
            "runtime_version": __version__,
        })
        return exec_id

    def check(self, code: str) -> Dict[str, Any]:
        """Compile a program in the sandbox without running it, and look up
        every tool it names. No tool is called and nothing is recorded.

        It catches what otherwise costs a run to find: a syntax error, a
        string a shell mangled, a tool name that does not exist.
        """
        if not isinstance(code, str) or not code.strip():
            raise UsageError("the program is empty", hint="Pass Python source as the code.")
        found = check_program(self.executor, code, float(self.limits["startup_timeout_s"]))
        if found.get("error"):
            return {"status": "error", "check": True, "error": found["error"]}
        report: Dict[str, Any] = {"tools": [], "unknown": []}
        namespaces = set(self.config.connectors)
        for path, certain in [(p, True) for p in found.get("calls", [])] + [(p, False) for p in found.get("maybe", [])]:
            namespace = path.partition(".")[0]
            if not certain and namespace not in namespaces:
                continue  # a tuple that starts with "a.b" is only probably a tool path
            try:
                entry = self.catalog.resolve(path)
            except ToolFailure as failure:
                report["unknown"].append({"tool": path, "problem": failure.message})
                continue
            report["tools"].append({
                "tool": entry.path,
                "signature": entry.signature(max_params=8),
                "policy": self.config.policy.decide(entry.path, None, entry.original_path).action,
            })
        warnings = []
        if found.get("lostValue"):
            warnings.append(
                "The program ends with a block (if, for, try, with); a value inside a block is not the result. "
                "End the program with an expression at the top level."
            )
        if report["unknown"]:
            names = ", ".join(item["tool"] for item in report["unknown"])
            return {"status": "error", "check": True, "result": report, "error": {
                "type": "UnknownTool",
                "message": "the program names %d tool(s) that do not exist: %s" % (len(report["unknown"]), names),
                "hint": " ".join(item["problem"] for item in report["unknown"])[:1200],
            }}
        payload: Dict[str, Any] = {"status": "completed", "check": True, "result": report}
        if warnings:
            payload["warnings"] = warnings
        return payload

    def _check_args(self, args: Any) -> Dict[str, Any]:
        """Program arguments are data from the caller: a JSON object."""
        if args is None:
            return {}
        if not isinstance(args, dict) or not all(isinstance(key, str) for key in args):
            raise UsageError(
                "the program arguments must be a JSON object", hint='Example: {"app": "web", "runs": 3}; the program reads args["app"].'
            )
        try:
            size = byte_len(args)
        except (TypeError, ValueError) as exc:
            raise UsageError("the program arguments are not valid JSON: %s" % exc)
        if size > int(self.limits["max_value_bytes"]):
            raise UsageError("the program arguments are larger than limits.max_value_bytes")
        return args

    def approve(
        self,
        ref: str,
        by: str = "human",
        approver: Optional[Approver] = None,
        cancel_event: Optional[threading.Event] = None,
        stale_ok: bool = False,
    ) -> Dict[str, Any]:
        exec_id = self._resolve(ref)
        with self.store.lock(exec_id):
            view = self.store.view(exec_id)
            if view.status != "paused":
                raise StateError(
                    "execution %s is %s, not paused" % (exec_id, view.status),
                    hint="Only a paused execution has something to approve.",
                )
            # A pause is a question asked at one moment. Much later the answer
            # may no longer mean what the person thinks it means.
            ttl = float(self.config.approval["ttl_s"])
            age = view.paused_for()
            if ttl > 0 and age > ttl and not stale_ok:
                raise StateError(
                    "execution %s has been paused for %s; approvals expire after %s (approval.ttl_s)"
                    % (exec_id, _duration(age), _duration(ttl)),
                    hint="Run the program again so the request is current. To approve this one anyway: %s"
                    % launcher.command("approve", exec_id, "--stale"),
                )
            entry: Dict[str, Any] = {"type": "approval", "seqs": [p["seq"] for p in view.pending], "by": by}
            if self._policy_hash() != view.start.get("policy_sha256"):
                # The resumed pass decides under the policy as it is now, and
                # a deny rule beats this approval. The log says both.
                entry["policy_changed"] = True
            self.store.append(exec_id, entry)
            return self._run_pass(exec_id, approver, cancel_event)

    def _policy_hash(self) -> str:
        return sha256_hex(canonical(self.config.policy.to_dict()))

    def reject(self, ref: str, by: str = "human", reason: str = "") -> Dict[str, Any]:
        exec_id = self._resolve(ref)
        with self.store.lock(exec_id):
            view = self.store.view(exec_id)
            if view.status not in ("paused", "running"):
                raise StateError("execution %s is already %s" % (exec_id, view.status))
            # Calls that already ran stay applied. Rejecting stops the
            # execution; it does not pretend to undo it.
            self.store.append(exec_id, {
                "type": "end",
                "status": "rejected",
                "by": by,
                "reason": reason,
                "rejected_seqs": [p["seq"] for p in view.pending],
            })
        return self.get(exec_id)

    def get(self, ref: str, full: bool = False, select: Optional[str] = None, worker_gone: bool = False) -> Dict[str, Any]:
        exec_id = self._resolve(ref)
        view = self._settled(exec_id, worker_gone)
        if select:
            return self.select(view, select, full=full)
        return self._payload(view, full=full)

    # -- executions that outlive the command that started them ------------

    def run_started(self, exec_id: str, cancel_event: Optional[threading.Event] = None) -> Dict[str, Any]:
        """Run the first pass of an execution that ``start`` recorded. This
        is what a detached worker does."""
        with self.store.lock(exec_id):
            view = self.store.view(exec_id)
            if view.status != "running" or view.passes:
                raise StateError("execution %s is %s and has already been run" % (exec_id, view.status))
            return self._run_pass(exec_id, None, cancel_event)

    def wait(self, ref: str, timeout_s: float = 60.0) -> Dict[str, Any]:
        """The outcome of an execution, waiting up to ``timeout_s`` for it to
        stop running. A caller with a short command timeout calls this in a
        loop; each call returns what has happened so far."""
        exec_id = self._resolve(ref)
        deadline = time.monotonic() + max(0.0, float(timeout_s))
        while True:
            view = self._settled(exec_id)
            if view.status != "running" or time.monotonic() >= deadline:
                return self._payload(view)
            time.sleep(0.25)

    def cancel(self, ref: str, by: str = "human", wait_s: float = 10.0) -> Dict[str, Any]:
        """Stop a running execution, wherever it runs. Calls that already
        ran stay applied; a call in flight is given up on and the tool source
        is told. A paused execution is rejected, which is the same thing."""
        exec_id = self._resolve(ref)
        view = self._settled(exec_id)
        if view.status == "paused":
            return self.reject(exec_id, by=by, reason="cancelled")
        if view.status != "running":
            raise StateError("execution %s is already %s" % (exec_id, view.status))
        self.store.request_cancel(exec_id)
        return self.wait(exec_id, wait_s)

    def _settled(self, exec_id: str, worker_gone: bool = False) -> ExecutionView:
        """The view of an execution, after checking that one which says
        "running" still has a process behind it. ``worker_gone`` is the
        caller's knowledge that the process meant to run it has exited.

        A command that is killed (a shell timeout, a closed terminal) cannot
        write how it ended. Without this check its execution would say
        "running" for ever, and a caller would wait for ever.
        """
        view = self.store.view(exec_id)
        if view.status != "running" or self.store.holder(exec_id) is not None:
            return view
        started = parse_ts(view.start.get("ts")) or 0.0
        if not view.passes and not worker_gone and time.time() - started < _START_GRACE_S:
            return view  # recorded a moment ago; its process has not taken the lock yet
        try:
            with self.store.lock(exec_id):
                view = self.store.view(exec_id)
                if view.status != "running":
                    return view
                detail = ""
                if not view.passes:
                    detail = " It never started." + self._worker_note(exec_id)
                self.store.append(exec_id, {"type": "end", "status": "error", "error": {
                    "type": "Interrupted",
                    "message": "the process that was running this execution%s is gone: it was killed or it crashed.%s"
                    % ((" (pid %s)" % view.pass_pid) if view.pass_pid else "", detail),
                    "hint": "A command that outlives your shell's time limit should be started with --detach and "
                    "followed with wait. A call that was in flight may or may not have completed: check the target.",
                }, "logs": "".join(view.live_logs)[-int(self.limits["max_log_chars"]):]})
        except BusyError:
            pass  # someone took the lock in the meantime: it is running after all
        return self.store.view(exec_id)

    def _worker_note(self, exec_id: str) -> str:
        try:
            text = self.store.worker_log(exec_id).read_text(encoding="utf-8", errors="replace").strip()
        except OSError:
            return ""
        return (" Its output: " + text[-600:]) if text else ""

    def select(self, view: ExecutionView, path: str, full: bool = False, live_result: Any = None) -> Dict[str, Any]:
        """One part of an execution, by dotted path: ``result.rows.0``,
        ``variables.res``, ``logs``, ``error.message``, ``args``, ``code``.

        This is how a caller reads the rest of a value that was cut to the
        output budget, or one variable of a program that failed, without
        pulling everything into its context.
        """
        end = view.end or {}
        payload = self._payload(view, full=True, live_result=live_result)
        if view.status != "completed" and path.split(".")[0] == "result":
            return self._payload(view)  # there is no result; the outcome says why
        document = dict(payload)
        document["variables"] = end.get("vars") or {}
        document["args"] = view.start.get("args") or {}
        document["code"] = view.start.get("code", "")
        found, value = lookup(document, path)
        if not found:
            root = path.split(".")[0]
            inside = document.get(root) if root in document else document
            keys = sorted(inside) if isinstance(inside, dict) else []
            raise NotFoundError(
                "execution %s has nothing at %r" % (view.id, path),
                hint="Available%s: %s" % ((" under %s" % root) if root in document else "", ", ".join(keys[:40]) or "(nothing)"),
            )
        out: Dict[str, Any] = {"status": view.status, "executionId": view.id, "select": path}
        if full:
            out["value"] = value
            return out
        shown, truncated, total = budget_result(value, int(self.limits["max_result_chars"]))
        out["value"] = shown
        if truncated:
            out["valueTruncated"] = True
            out["valueChars"] = total
            out["valueShape"] = shape_of(value)
            out["hint"] = "Cut to the output budget. Select a smaller part (the shape shows what is inside), or pass --full."
        return out

    def verify(self, ref: str) -> Dict[str, Any]:
        """Replay a completed execution from its log, calling no tool, and
        compare the result. This is a differential test of the record against
        the program: it shows the stored result follows from the stored calls."""
        exec_id = self._resolve(ref)
        with self.store.lock(exec_id):
            view = self.store.view(exec_id)
            if view.status != "completed" or view.end is None:
                raise StateError(
                    "execution %s is %s; only a completed execution can be verified" % (exec_id, view.status)
                )
            outcome = self._pass(view, "verify", None, None).run(
                self.executor, view.start["code"], view.start.get("state_in") or {}, view.start.get("args") or {},
                self._helpers_of(view.start),
            )
            detail = ""
            if outcome.status != "completed":
                error = outcome.error or {}
                detail = "%s: %s" % (error.get("type", "error"), error.get("message", ""))
            elif sha256_hex(canonical(outcome.result)) != view.end.get("result_sha256"):
                detail = "the replayed result differs from the recorded one"
            elif sha256_hex(canonical(outcome.state_out)) != view.end.get("state_out_sha256"):
                detail = "the replayed state differs from the recorded one"
            ok = not detail
            self.store.append(exec_id, {"type": "verify", "ok": ok, "detail": detail, "replayed": outcome.stats.get("replayed")})
        return {
            "executionId": exec_id,
            "verified": ok,
            "verification": "replay-ok" if ok else "replay-failed",
            "replayedSteps": outcome.stats.get("replayed", 0),
            "detail": detail or "the replay reproduced the recorded result and state without calling any tool",
        }

    # -- discovery and listings (host side) ------------------------------

    def search(self, query: str, limit: Optional[int] = None) -> List[Dict[str, Any]]:
        return self.catalog.search(query, limit)

    def describe(self, target: str) -> Dict[str, Any]:
        return self.catalog.describe(target)

    def list_executions(self, limit: int = 20) -> List[Dict[str, Any]]:
        ids = self.store.ids()[-max(1, int(limit)):]
        return [self._settled(i).summary() for i in reversed(ids)]

    def save_snippet(self, name: str, ref: str, description: str = "") -> Dict[str, Any]:
        """Promote the program of a completed execution to a reusable snippet.
        Only code that has run to completion can be promoted, and only from
        the host."""
        exec_id = self._resolve(ref)
        view = self.store.view(exec_id)
        if view.status != "completed":
            raise StateError(
                "execution %s is %s; only a completed execution can become a snippet" % (exec_id, view.status)
            )
        return self.snippets.save(name, view.start["code"], description, exec_id)

    # -- one pass ---------------------------------------------------------

    def _pass(self, view: ExecutionView, mode: str, approver: Optional[Approver], cancel_event: Any) -> Pass:
        reuse, reuse_id = None, ""
        if mode == "live" and view.passes == 0 and view.start.get("reuse_from"):
            # Only the first pass looks at the earlier execution. What it
            # took from there is in this execution's own log from then on.
            reuse_id = str(view.start["reuse_from"])
            try:
                reuse = self.store.view(reuse_id).calls
            except NotFoundError:
                reuse = None  # pruned since; the program simply runs live
        return Pass(
            call_timeout=self.config.call_timeout,
            reuse=reuse,
            reuse_id=reuse_id,
            store=self.store,
            exec_id=view.id,
            view=view,
            catalog=self.catalog,
            policy=self.config.policy,
            limits=self.limits,
            snippets=self.snippets,
            agent=view.start.get("agent", self.agent),
            pass_no=view.passes + 1,
            mode=mode,
            approver=approver,
            cancel_event=cancel_event,
        )

    def _run_pass(self, exec_id: str, approver: Optional[Approver], cancel_event: Any) -> Dict[str, Any]:
        """Caller holds the execution lock."""
        view = self.store.view(exec_id)
        start = view.start
        # Cancellation can come from another process (the cancel command), so
        # every pass watches for the request file as well as its own event.
        cancel_event = cancel_event or threading.Event()
        finished = threading.Event()

        def watch() -> None:
            while not finished.wait(0.5):
                if self.store.cancel_requested(exec_id):
                    cancel_event.set()
                    return

        self.store.clear_cancel(exec_id)
        threading.Thread(target=watch, daemon=True).start()
        runner = self._pass(view, "live", approver, cancel_event)
        self.store.append(exec_id, {"type": "pass_start", "pass": runner.pass_no, "pid": os.getpid()})
        try:
            outcome = runner.run(
                self.executor, start["code"], start.get("state_in") or {}, start.get("args") or {}, self._helpers_of(start)
            )
        except KeyboardInterrupt:
            # Ctrl-C: the sandbox is already stopped (the pass kills it on
            # the way out). Say how the execution ended before leaving.
            self.store.append(exec_id, {"type": "end", "status": "error", "error": {
                "type": "Interrupted",
                "message": "the execution was interrupted from the keyboard",
                "hint": "A call that was in flight may or may not have completed: check the target.",
            }})
            raise
        finally:
            finished.set()
            self.store.clear_cancel(exec_id)
        extras: Dict[str, Any] = {}
        if outcome.status == "paused":
            self.store.append(exec_id, {
                "type": "pass_end",
                "pass": runner.pass_no,
                "status": "paused",
                "pending": [p["seq"] for p in outcome.pending],
                "stats": outcome.stats,
            })
        elif outcome.status == "completed":
            delta, warnings = self._state_delta(start["session"], start.get("state_in") or {}, outcome.state_out)
            self.sessions.apply_delta(start["session"], delta["set"], delta["delete"])
            result_text = canonical(outcome.result)
            fits = len(result_text) <= int(self.limits["max_value_bytes"])
            self.store.append(exec_id, {"type": "pass_end", "pass": runner.pass_no, "status": "completed"})
            self.store.append(exec_id, {
                "type": "end",
                "status": "completed",
                # A result too large for the log is hashed, not stored; the
                # caller still receives it, within the output budget.
                "result": outcome.result if fits else None,
                "result_omitted": not fits,
                "result_chars": len(result_text),
                "result_sha256": sha256_hex(result_text),
                "result_repr": outcome.result_repr,
                "state_out_sha256": sha256_hex(canonical(outcome.state_out)),
                "state_delta": {"set": sorted(delta["set"]), "delete": delta["delete"]},
                "logs": outcome.logs,
                "logs_dropped": outcome.logs_dropped,
                "stats": outcome.stats,
                "warnings": warnings + outcome.notes,
            })
            if not fits:
                extras["live_result"] = outcome.result
        else:
            self.store.append(exec_id, {"type": "pass_end", "pass": runner.pass_no, "status": "error"})
            self.store.append(exec_id, {
                "type": "end",
                "status": "error",
                "error": outcome.error,
                # What the program held when it failed. Kept here, where it
                # can be fetched; the payload shows only the shapes.
                "vars": outcome.vars,
                "vars_omitted": outcome.vars_omitted,
                "logs": outcome.logs,
                "logs_dropped": outcome.logs_dropped,
                "stats": outcome.stats,
            })
        # Housekeeping, at most once every few hours per data directory.
        self.store.auto_prune(float(self.config.retention["max_age_days"]) * 86400.0, int(self.config.retention["max_executions"]))
        return self._payload(self.store.view(exec_id), outcome=outcome, **extras)

    def _state_delta(self, session: str, before: Dict[str, Any], after: Dict[str, Any]) -> Any:
        """What this execution changed in ``state``, cut down to the size
        limit. A value that does not fit is dropped and named, so the next
        program is told rather than finding a hole."""
        changed = {k: v for k, v in after.items() if k not in before or before[k] != v}
        deleted = sorted(k for k in before if k not in after)
        warnings: List[str] = []
        limit = int(self.limits["max_state_bytes"])
        current = self.sessions.load(session)
        for key in deleted:
            current.pop(key, None)
        merged = dict(current, **changed)
        while changed and byte_len(merged) > limit:
            biggest = max(changed, key=lambda k: byte_len(changed[k]))
            warnings.append(
                "state[%r] was not saved: the session state would exceed %d bytes (limits.max_state_bytes)"
                % (biggest, limit)
            )
            del changed[biggest]
            merged = dict(current, **changed)
        return {"set": changed, "delete": deleted}, warnings

    # -- payloads ---------------------------------------------------------

    def _fetch_command(self, exec_id: str, path: str) -> str:
        """How this caller reads one part of an execution: a hint has to name
        something the reader can actually do on the surface it is using."""
        if self.surface == "mcp":
            return 'execute {"executionId": "%s", "select": "%s"}' % (exec_id, path)
        return launcher.command("get", exec_id, "--select", path)

    def _payload(
        self,
        view: ExecutionView,
        outcome: Optional[PassOutcome] = None,
        full: bool = False,
        live_result: Any = None,
    ) -> Dict[str, Any]:
        """What the caller sees. Built from the log, so ``get`` on an old
        execution and the return of a fresh one have the same shape."""
        payload: Dict[str, Any] = {"status": view.status, "executionId": view.id}
        end = view.end or {}
        budget = int(self.limits["max_result_chars"])
        if view.status == "completed":
            result = live_result if end.get("result_omitted") and live_result is not None else end.get("result")
            if end.get("result_omitted") and live_result is None:
                payload["result"] = None
                payload["resultOmitted"] = "the result was %d chars, too large to keep in the log" % end.get("result_chars", 0)
            elif full:
                payload["result"] = result
            else:
                shown, truncated, total = budget_result(result, budget)
                payload["result"] = shown
                if truncated:
                    payload["resultTruncated"] = True
                    payload["resultChars"] = total
                    payload["resultShape"] = shape_of(result)
                    payload["hint"] = (
                        "The result was cut to %d chars; the whole value is kept. Read a part of it with: %s    "
                        "Next time return less: filter, slice or aggregate in the program."
                        % (budget, self._fetch_command(view.id, "result.KEY"))
                    )
            if end.get("result_repr"):
                payload["resultIsRepr"] = True
            payload["verification"] = view.verification
        elif view.status == "paused":
            payload["pending"] = [self._pending_item(p) for p in view.pending]
            payload["hint"] = (
                "Paused before a call that needs approval; nothing pending has run. A human approves with "
                "'%s', or refuses with the same command and 'reject' in place of 'approve'. Fetch the outcome "
                "afterwards by passing this executionId instead of code." % launcher.command("approve", view.id)
            )
        elif view.status == "rejected":
            payload["error"] = {
                "type": "Rejected",
                "message": "a human rejected this execution%s" % ((": " + end["reason"]) if end.get("reason") else ""),
                "hint": "Calls that ran before the pause stay applied.",
            }
        elif view.status == "error":
            payload["error"] = end.get("error") or {"type": "Error", "message": "failed"}
            kept = end.get("vars") or {}
            if kept or end.get("vars_omitted"):
                # Not the values: tool output that the program did not choose
                # to return stays out of the caller's context. The shapes say
                # what is there to fetch.
                payload["variables"] = {name: shape_of(value, depth=2, keys=8) for name, value in sorted(kept.items())}
                for item in end.get("vars_omitted") or []:
                    payload["variables"][str(item.get("name"))] = "not kept: %s chars" % item.get("chars")
                payload["hint"] = (
                    "The program's top-level variables at the failure were kept. Read one with: %s"
                    % self._fetch_command(view.id, "variables.NAME")
                )
            done = sum(1 for call in view.calls.values() if call.get("kind") == "tool" and call.get("state") == "applied")
            if done and (end.get("error") or {}).get("type") != "Rejected":
                # The work that finished is in the log. Saying how to pick it
                # up is the difference between a retry and a restart.
                how = ('"reuse": "%s"' % view.id) if self.surface == "mcp" else ("--reuse %s" % view.id)
                payload["hint"] = (payload.get("hint", "") + (
                    " %d tool call(s) completed before the failure. To continue without repeating them, fix the "
                    "program and run it again with %s." % (done, how)
                )).strip()
        elif view.status == "running":
            payload["progress"] = self._progress(view)
            if self.surface == "mcp":
                payload["hint"] = (
                    'Still running. Wait for it with execute {"executionId": "%s", "wait": 60}; stop it with "cancel": true '
                    'in place of "wait".' % view.id
                )
            else:
                payload["hint"] = "Still running. Wait for it with: %s    To stop it, use cancel in place of wait." % (
                    launcher.command("wait", view.id, "--timeout", "60")
                )
        warnings = list(end.get("warnings") or [])
        if view.policy_changed:
            warnings.append(
                "The policy changed between the start of this execution and its approval. Calls made after "
                "the approval were decided under the policy as it was then; a deny rule beats an approval."
            )
        if warnings:
            payload["warnings"] = warnings
        logs = outcome.logs if outcome is not None else end.get("logs", "")
        if view.status == "running":
            # What the program has printed so far, from the entries the pass
            # writes as it goes: the end of it, where the program is now.
            logs = truncate_text("".join(view.live_logs), int(self.limits["max_log_chars"]), keep="both")[0]
        dropped = outcome.logs_dropped if outcome is not None else end.get("logs_dropped", 0)
        if logs:
            payload["logs"] = logs
        if dropped:
            payload["logsDroppedChars"] = dropped
        stats = dict(outcome.stats if outcome is not None else end.get("stats") or {})
        if stats:
            payload["stats"] = stats
        return payload

    @staticmethod
    def _progress(view: ExecutionView) -> Dict[str, Any]:
        """Where a running execution is: derived from its log, so any process
        can ask. The calls that finished, and the ones in flight with how
        long each has been running and the last thing its server reported."""
        now = time.time()
        started = parse_ts(view.start.get("ts")) or now
        tools = [call for _, call in sorted(view.calls.items()) if call.get("kind") == "tool"]
        running = []
        for call in tools:
            if call.get("state") != "executing":
                continue
            item: Dict[str, Any] = {
                "seq": call["seq"], "tool": call.get("name"),
                "forS": int(max(0.0, now - (parse_ts(call.get("ts")) or now))),
            }
            if view.progress.get(int(call["seq"])):
                item["reported"] = view.progress[int(call["seq"])]
            running.append(item)
        return {
            "phase": "running" if view.passes else "starting",
            "elapsedS": int(max(0.0, now - started)),
            "callsDone": sum(1 for call in tools if call.get("state") in ("applied", "error", "denied")),
            "callsFailed": sum(1 for call in tools if call.get("state") in ("error", "denied")),
            "running": running[:20],
        }

    @staticmethod
    def _pending_item(entry: Dict[str, Any]) -> Dict[str, Any]:
        shown, truncated, _ = budget_result(entry.get("args"), _ARGS_PREVIEW_CHARS)
        item = {"seq": entry["seq"], "tool": entry["name"], "args": shown, "rule": entry.get("rule")}
        if truncated:
            item["argsTruncated"] = True
        return item


def _duration(seconds: float) -> str:
    """A span of time for a message: 45s, 12 min, 3.5 h, 2.0 days."""
    if seconds < 90:
        return "%ds" % seconds
    if seconds < 5400:
        return "%d min" % round(seconds / 60)
    if seconds < 2 * 86400:
        return "%.1f h" % (seconds / 3600)
    return "%.1f days" % (seconds / 86400)


def summarize_stats(stats: Dict[str, Any], payload: Dict[str, Any]) -> str:
    """One line a human can read: what code mode saved on this execution."""
    returned = len(canonical(payload.get("result"))) + len(payload.get("logs", ""))
    text = "%d tool call(s), %d replayed step(s), %d bytes of tool results stayed in the sandbox, %d bytes returned, %d ms" % (
        stats.get("toolCalls", 0),
        stats.get("replayed", 0),
        stats.get("toolResultBytes", 0),
        returned,
        stats.get("durationMs", 0),
    )
    return truncate_text(text, 400)[0]
