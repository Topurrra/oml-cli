# SPDX-License-Identifier: MIT
"""One pass of one execution: the gateway between the sandbox and the tools.

Every request the program makes arrives here as a frame and is given the next
sequence number. The gateway then does the same thing for every kind of
request:

1. Look at the log. If this sequence number already has an outcome, check that
   the request is the same one (by fingerprint) and return the recorded
   outcome. Nothing runs. This is replay.
2. Otherwise the request is live. For a tool call that means: resolve the
   name, apply the limits, ask the policy, write an ``executing`` entry, call
   the connector, record the outcome.

A call the policy says to ask about, and that no one has approved, is recorded
as pending and the pass ends as ``paused``. After approval the next pass runs
the same program from the top; steps 1 and 2 above make everything before the
approved call a replay and the approved call live. Nothing runs twice.

The pass only appends ``call`` entries. ``runtime.py`` owns the entries that
describe the execution as a whole.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import Any, Callable, Dict, List, Optional

from .catalog import Catalog, default_limit
from .connectors import CallContext, redact
from .errors import CodemodeError, ToolFailure
from .executor import RUNNER_SOURCE, SandboxEOF, SandboxProtocolError
from .jsonutil import byte_len, fingerprint
from .policy import Policy
from .records import ExecutionStore, ExecutionView
from .sessions import SnippetStore
from .truncate import BoundedLog

Approver = Callable[[Dict[str, Any]], Optional[bool]]

# How long past its time limit a call is waited for before the gateway
# answers for it. Connectors enforce the limit themselves and get to say so.
_GRACE_S = 0.5
# Printed output and progress texts persisted while a pass runs, per pass.
_LIVE_LOG_CHARS = 64000
_PROGRESS_ENTRIES = 200
# When the program's time limit ends a call that was still running, the
# program gets this long to deal with the ToolError and finish. Its earlier
# results are in its variables; killing it at once would throw them away.
_WIND_DOWN_S = 2.0

_DIVERGENCE_HINT = (
    "A resumed execution runs the program again and must make the same requests in the same "
    "order. Wrap anything that changes between runs (time, randomness, file listings) in "
    "step(name, fn)."
)


class _Abort(Exception):
    """End the pass with an error outcome."""

    def __init__(self, kind: str, message: str, hint: str = "") -> None:
        super().__init__(message)
        self.kind = kind
        self.message = message
        self.hint = hint


class _Paused(Exception):
    def __init__(self, pending: List[Dict[str, Any]]) -> None:
        super().__init__("paused")
        self.pending = pending


@dataclass
class PassOutcome:
    status: str  # completed | paused | error
    result: Any = None
    result_repr: bool = False
    state_out: Dict[str, Any] = field(default_factory=dict)
    error: Optional[Dict[str, Any]] = None
    pending: List[Dict[str, Any]] = field(default_factory=list)
    notes: List[str] = field(default_factory=list)  # warnings from the sandbox about the program
    vars: Dict[str, Any] = field(default_factory=dict)  # top-level variables of a program that failed
    vars_omitted: List[Dict[str, Any]] = field(default_factory=list)
    logs: str = ""
    logs_dropped: int = 0
    stats: Dict[str, Any] = field(default_factory=dict)


def check_program(executor: Any, code: str, timeout_s: float) -> Dict[str, Any]:
    """Compile ``code`` in the sandbox without running it.

    Returns what the sandbox found (``calls``, ``maybe``, ``lostValue``), or
    ``{"error": {...}}`` for a program that does not compile or a sandbox
    that does not answer. It runs where the program would run, because that
    interpreter decides what is valid syntax, not the host's.
    """
    try:
        proc = executor.start()
    except OSError as exc:
        return {"error": {"type": "ExecutorStartFailed", "message": "could not start the sandbox: %s" % exc,
                          "hint": "Check executor.command in the config; the default needs no setup."}}
    try:
        proc.send({"op": "init", "runner": RUNNER_SOURCE})
        deadline = time.monotonic() + timeout_s
        sent = False
        while True:
            frame = proc.recv(max(0.0, deadline - time.monotonic()))
            if frame.get("op") == "ready" and not sent:
                # Only now. The loader reads its one line through a buffer,
                # and a frame that is already in the pipe behind it would be
                # read into that buffer and never seen by the runner.
                proc.send({"op": "run", "code": code, "check": True})
                sent = True
            if frame.get("op") == "done":
                if frame.get("ok"):
                    return frame.get("result") if isinstance(frame.get("result"), dict) else {}
                error = frame.get("error") if isinstance(frame.get("error"), dict) else {}
                error.setdefault("type", "ProgramError")
                error.setdefault("message", "the program does not compile")
                return {"error": error}
    except (TimeoutError, SandboxEOF, SandboxProtocolError) as exc:
        return {"error": {"type": "ExecutorStartFailed", "message": "the sandbox did not answer the check: %s" % (exc or type(exc).__name__),
                          "hint": "See the executor section of the config."}}
    finally:
        proc.kill()


class Pass:
    def __init__(
        self,
        *,
        store: ExecutionStore,
        exec_id: str,
        view: ExecutionView,
        catalog: Catalog,
        policy: Policy,
        limits: Dict[str, float],
        snippets: SnippetStore,
        agent: str,
        pass_no: int,
        mode: str = "live",
        approver: Optional[Approver] = None,
        cancel_event: Optional[threading.Event] = None,
        call_timeout: Optional[Callable[[str, str, str], float]] = None,
        reuse: Optional[Dict[int, Dict[str, Any]]] = None,
        reuse_id: str = "",
    ) -> None:
        self.store = store
        self.exec_id = exec_id
        self.view = view
        self.catalog = catalog
        self.policy = policy
        self.limits = limits
        self.snippets = snippets
        self.agent = agent
        self.pass_no = pass_no
        self.mode = mode  # "live", or "verify": replay only, never call a tool
        self.approver = approver
        self.cancel_event = cancel_event
        # (connector, path, server spelling) -> seconds one call may run.
        self.call_timeout = call_timeout or (lambda connector, path, alias: float(limits["call_timeout_s"]))
        # Outcomes of an earlier execution, by sequence number. While this
        # program asks for the same things in the same order, they are taken
        # from there instead of being run again.
        self.reuse = dict(reuse) if reuse else None
        self.reuse_id = reuse_id
        self.reused = 0
        self.wind_down = 0.0  # set when the program's time limit cut a call short
        self.cut_short: List[str] = []
        self._live_parts: List[str] = []
        self._live_at = 0.0
        self._live_total = 0
        self._progress_seen: Dict[int, Any] = {}
        self._progress_lock = threading.Lock()
        self.seq = 0
        self.tool_calls = 0
        self.replayed = 0
        self.tool_result_bytes = 0
        self.deadline = 0.0
        self.logs = BoundedLog(int(limits["max_log_chars"]))
        self._open_steps: Dict[int, Any] = {}
        self._handlers = {
            "call": self._on_call,
            "search": self._on_search,
            "describe": self._on_describe,
            "snippet": self._on_snippet,
            "step": self._on_step,
            "step_set": self._on_step_set,
        }

    # -- the loop ---------------------------------------------------------

    def run(
        self,
        executor: Any,
        code: str,
        state_in: Dict[str, Any],
        args: Optional[Dict[str, Any]] = None,
        helpers: Optional[Dict[str, str]] = None,
    ) -> PassOutcome:
        started = time.monotonic()
        self.deadline = started + float(self.limits["timeout_s"])
        try:
            proc = executor.start()
        except OSError as exc:
            return self._finish(
                PassOutcome(status="error", error={
                    "type": "ExecutorStartFailed",
                    "message": "could not start the sandbox: %s" % exc,
                    "hint": "Check executor.command in the config; the default needs no setup.",
                }),
                started,
            )
        try:
            # send() never blocks or raises; a sandbox that is gone or silent
            # shows up in _recv, as an exit, a bad frame or a timeout.
            proc.send({"op": "init", "runner": RUNNER_SOURCE})
            ready_by = min(self.deadline, started + float(self.limits["startup_timeout_s"]))
            if self._recv(proc, ready_by, starting=True).get("op") != "ready":
                raise _Abort("ProtocolError", "the sandbox did not start with a ready frame")
            proc.send({
                "op": "run", "code": code, "state": state_in, "args": args or {}, "helpers": helpers or {},
                "vars_budget": int(self.limits["max_value_bytes"]),
            })
            while True:
                frame = self._recv(proc, self.deadline)
                op = frame.get("op")
                if op == "log":
                    text = str(frame.get("text", ""))
                    self.logs.add(text)
                    self._live_log(text)
                    continue
                if op == "done":
                    outcome = self._on_done(frame)
                    proc.wait_exit(1.0)
                    break
                handler = self._handlers.get(op)
                if handler is None:
                    raise _Abort("ProtocolError", "the sandbox sent an unknown request %r" % (op,))
                # What the program printed before a long call is what tells a
                # watcher where it is. Write it down before the call starts.
                self._live_log("", flush=True)
                reply = handler(frame)
                reply["id"] = frame.get("id")
                proc.send(reply)
        except _Paused as paused:
            outcome = PassOutcome(status="paused", pending=paused.pending)
        except _Abort as abort:
            error = {"type": abort.kind, "message": abort.message}
            if abort.hint:
                error["hint"] = abort.hint
            outcome = PassOutcome(status="error", error=error)
        finally:
            proc.kill()
            stderr = proc.stderr_tail().strip()
            if stderr:
                # Raw writes to the process's own stdout and stderr land here.
                self.logs.add("\n[sandbox stderr]\n" + stderr + "\n")
        return self._finish(outcome, started)

    def _live_log(self, text: str, flush: bool = False) -> None:
        """Persist printed output while the pass runs, so that ``get`` on a
        running execution can show it. Not part of replay: the program cannot
        observe these entries. Bounded: at most one entry a second, and a
        fixed total, after which output is kept only in the final logs."""
        if self.mode != "live" or self._live_total >= _LIVE_LOG_CHARS:
            return
        self._live_parts.append(text)
        now = time.monotonic()
        size = sum(len(part) for part in self._live_parts)
        if not size or not (flush or size >= 4096 or now - self._live_at >= 1.0):
            return
        chunk = "".join(self._live_parts)[: _LIVE_LOG_CHARS - self._live_total]
        self._live_parts = []
        self._live_at = now
        self._live_total += len(chunk)
        if self._live_total >= _LIVE_LOG_CHARS:
            chunk += "\n[output from here on is kept only in the final logs]\n"
        self.store.append(self.exec_id, {"type": "log", "pass": self.pass_no, "text": chunk})

    def _progress(self, seq: int, text: str) -> None:
        """A tool source reports on a call that is still running. Called on
        worker threads. Throttled per call: a chatty server must not be able
        to grow the log without bound."""
        now = time.monotonic()
        with self._progress_lock:
            last, count = self._progress_seen.get(seq, (0.0, 0))
            if now - last < 2.0 or count >= _PROGRESS_ENTRIES:
                return
            self._progress_seen[seq] = (now, count + 1)
        self.store.append(self.exec_id, {"type": "progress", "pass": self.pass_no, "seq": seq, "text": str(text)[:300]})

    def _finish(self, outcome: PassOutcome, started: float) -> PassOutcome:
        outcome.logs = self.logs.render()
        outcome.logs_dropped = self.logs.dropped
        outcome.stats = {
            "toolCalls": self.tool_calls,
            "replayed": self.replayed - self.reused,
            "toolResultBytes": self.tool_result_bytes,
            "durationMs": int((time.monotonic() - started) * 1000),
        }
        if self.reuse_id:
            outcome.stats["reused"] = self.reused
        return outcome

    def _recv(self, proc: Any, deadline: float, starting: bool = False) -> Dict[str, Any]:
        while True:
            if self.cancel_event is not None and self.cancel_event.is_set():
                raise _Abort("Cancelled", "the execution was cancelled")
            remaining = (deadline if starting else max(deadline, self.wind_down)) - time.monotonic()
            if remaining <= 0:
                if starting:
                    raise _Abort(
                        "ExecutorStartFailed",
                        "the sandbox did not report ready within %gs" % self.limits["startup_timeout_s"],
                        "The executor command must run the bootstrap: python -I -u -c {bootstrap}.",
                    )
                raise _Abort(
                    "Timeout",
                    "the program ran longer than %gs (limits.timeout_s)" % self.limits["timeout_s"],
                    "Do less in one program, or raise limits.timeout_s.",
                )
            try:
                return proc.recv(min(remaining, 0.2))
            except TimeoutError:
                continue
            except SandboxEOF:
                proc.wait_exit(0.5)
                raise _Abort(
                    "ExecutorStartFailed" if starting else "SandboxCrashed",
                    "the sandbox process exited (code %s) before it %s"
                    % (proc.exit_code(), "started" if starting else "finished"),
                    "See the logs for its stderr.",
                )
            except SandboxProtocolError as exc:
                raise _Abort(
                    "ProtocolError",
                    str(exc),
                    "The executor must run the bootstrap, and nothing else may write to its stdout.",
                )

    # -- replay and recording --------------------------------------------

    def _next(self, kind: str, name: str, args: Any) -> Any:
        """Give a request its sequence number and find its recorded outcome."""
        self.seq += 1
        fp = fingerprint(kind, name, args)
        rec = self.view.calls.get(self.seq)
        if rec is not None and rec.get("fp") != fp:
            same = rec.get("kind") == kind and rec.get("name") == name
            raise _Abort(
                "ReplayDivergence",
                "at step %d the log has %s %r but the program now asks for %s %r%s"
                % (self.seq, rec.get("kind"), rec.get("name"), kind, name, " with different arguments" if same else ""),
                _DIVERGENCE_HINT,
            )
        if rec is None and self.mode == "verify":
            raise _Abort(
                "VerifyMismatch",
                "the program asked for %s %r at step %d, which is not in the log" % (kind, name, self.seq),
                _DIVERGENCE_HINT,
            )
        if rec is None and self.reuse is not None:
            rec = self._reuse(self.seq, kind, name, args, fp)
        return self.seq, fp, rec

    def _reuse(self, seq: int, kind: str, name: str, args: Any, fp: str) -> Optional[Dict[str, Any]]:
        """Take the outcome of this request from the earlier execution, if it
        made the same request at the same step and it succeeded.

        This is how a program that failed late is continued: fix it, run it
        again with the old execution named, and everything up to the point
        where the two differ comes from the log. Nothing is called twice. A
        failed or denied step is not reused, so it gets its second chance.
        The first step that differs ends the reuse: after it the programs are
        no longer the same program.
        """
        assert self.reuse is not None
        old = self.reuse.get(seq)
        usable = (
            old is not None and old.get("fp") == fp and old.get("kind") == kind and old.get("state") == "applied"
            and (kind != "tool" or self._still_allowed(name, args))
        )
        if not usable:
            self.reuse = None
            return None
        self.reused += 1
        extra = {key: old[key] for key in ("result", "result_bytes", "duration_ms") if key in old}  # type: ignore[index]
        return self._append(seq, kind, name, args, fp, state="applied", reused_from=self.reuse_id, **extra)

    def _still_allowed(self, path: str, args: Any) -> bool:
        """A recorded result is handed out again only if the policy of today
        would not deny the call: a rule added since must not be bypassed by
        reading old results."""
        try:
            entry = self.catalog.resolve(path)
        except ToolFailure:
            return False
        return self._decide(entry, args).effective != "deny"

    def _decide(self, entry: Any, args: Any) -> Any:
        decision = self.policy.decide(entry.path, args, alias=entry.original_path)
        return decision

    def _append(self, seq: int, kind: str, name: str, args: Any, fp: str, **extra: Any) -> Dict[str, Any]:
        assert self.mode == "live", "a verify pass must never write a call"
        entry = {"type": "call", "seq": seq, "pass": self.pass_no, "kind": kind, "name": name, "args": args, "fp": fp}
        entry.update(extra)
        self.store.append(self.exec_id, entry)
        self.view.calls[seq] = entry
        return entry

    @staticmethod
    def _reply(rec: Dict[str, Any]) -> Dict[str, Any]:
        if rec.get("state") == "applied":
            return {"ok": True, "value": rec.get("result")}
        return {"ok": False, "error": rec.get("error") or {"type": "ToolError", "message": "failed"}}

    def _size(self, value: Any, what: str) -> int:
        try:
            size = byte_len(value)
        except (TypeError, ValueError) as exc:
            raise ToolFailure("BadValue", "%s is not valid JSON: %s" % (what, exc))
        limit = int(self.limits["max_value_bytes"])
        if size > limit:
            raise ToolFailure(
                "ValueTooLarge",
                "%s is %d bytes; the limit is %d (limits.max_value_bytes). "
                "Ask for less: filter, paginate or summarise at the source." % (what, size, limit),
            )
        return size

    # -- discovery, snippets, steps --------------------------------------

    def _simple(self, kind: str, name: str, args: Any, compute: Callable[[], Any]) -> Dict[str, Any]:
        """Search, describe and snippet lookups are recorded like tool calls:
        the catalog may change between passes, and replay must not."""
        seq, fp, rec = self._next(kind, name, args)
        if rec is not None:
            self.replayed += 1
            return self._reply(rec)
        try:
            value = compute()
            self._size(value, "the %s result" % kind)
            return self._reply(self._append(seq, kind, name, args, fp, state="applied", result=value))
        except ToolFailure as failure:
            error = failure.to_dict()
        except CodemodeError as exc:
            error = {"type": exc.kind, "message": exc.message}
        return self._reply(self._append(seq, kind, name, args, fp, state="error", error=error))

    def _on_search(self, frame: Dict[str, Any]) -> Dict[str, Any]:
        query = str(frame.get("query", ""))
        try:
            limit = int(frame["limit"])
        except (KeyError, TypeError, ValueError):
            # Resolved here, before the fingerprint, so that search("words")
            # with no limit replays against a log that recorded the default.
            limit = default_limit(query)

        def find() -> Dict[str, Any]:
            # The total travels with the hits: a list that stops at the limit
            # and does not say so reads as "this is everything".
            hits, total = self.catalog.search_with_total(query, limit)
            return {"hits": hits, "total": total}

        return self._simple("search", query, {"limit": limit}, find)

    def _on_describe(self, frame: Dict[str, Any]) -> Dict[str, Any]:
        target = str(frame.get("target", ""))
        return self._simple("describe", target, None, lambda: self.catalog.describe(target))

    def _on_snippet(self, frame: Dict[str, Any]) -> Dict[str, Any]:
        name = str(frame.get("name", ""))
        return self._simple("snippet", name, None, lambda: {"code": self.snippets.get(name)["code"]})

    def _on_step(self, frame: Dict[str, Any]) -> Dict[str, Any]:
        name = str(frame.get("name", ""))
        seq, fp, rec = self._next("step", name, None)
        if rec is not None:
            self.replayed += 1
            if rec.get("state") == "applied":
                return {"ok": True, "hit": True, "value": rec.get("result")}
            return self._reply(rec)
        self._open_steps[seq] = (name, fp)
        return {"ok": True, "hit": False, "seq": seq}

    def _on_step_set(self, frame: Dict[str, Any]) -> Dict[str, Any]:
        seq = frame.get("seq")
        if seq not in self._open_steps:
            raise _Abort("ProtocolError", "the sandbox closed a step that is not open")
        name, fp = self._open_steps.pop(seq)
        if "error" in frame:
            raw = frame["error"] if isinstance(frame["error"], dict) else {}
            error = {"type": "StepError", "message": str(raw.get("message", ""))}
            self._append(seq, "step", name, None, fp, state="error", error=error)
            return {"ok": True}
        value = frame.get("value")
        try:
            self._size(value, "the value of step %r" % name)
        except ToolFailure as failure:
            return self._reply(self._append(seq, "step", name, None, fp, state="error", error=failure.to_dict()))
        self._append(seq, "step", name, None, fp, state="applied", result=value)
        return {"ok": True}

    # -- tool calls -------------------------------------------------------

    def _on_call(self, frame: Dict[str, Any]) -> Dict[str, Any]:
        calls = frame.get("calls")
        if not isinstance(calls, list) or not calls:
            raise _Abort("ProtocolError", "the sandbox sent a call request with no calls")
        limit = int(self.limits["max_calls"])
        if len(calls) > limit:
            # Refused as one step, before any bookkeeping per call: a batch of
            # a million must not write a million log entries.
            def refuse() -> Any:
                raise ToolFailure(
                    "CallLimitExceeded",
                    "one parallel() batch of %d calls is over the limit of %d tool calls per execution (limits.max_calls)" % (len(calls), limit),
                    "Split the work over several programs, or raise limits.max_calls.",
                )

            return self._simple("batch", "parallel", {"calls": len(calls)}, refuse)
        if self.mode == "live":
            # A batch that reaches several servers starts them side by side.
            self.catalog.preload(str(raw.get("tool", "")).partition(".")[0] for raw in calls if isinstance(raw, dict))
        plan: List[Dict[str, Any]] = []
        for raw in calls:
            raw = raw if isinstance(raw, dict) else {}
            path = str(raw.get("tool", ""))
            args = raw.get("args") if isinstance(raw.get("args"), dict) else {}
            seq, fp, rec = self._next("tool", path, args)
            self.tool_calls += 1
            item: Dict[str, Any] = {"seq": seq, "path": path, "args": args, "fp": fp}
            state = rec.get("state") if rec else None
            if state in ("applied", "error", "denied"):
                item["rec"] = rec
                self.replayed += 1
            elif self.mode == "verify":
                raise _Abort("VerifyMismatch", "step %d (%s) has no recorded outcome" % (seq, path))
            elif state == "executing":
                # An earlier pass started this call and died before recording
                # how it ended. Running it again could repeat a side effect.
                raise _Abort(
                    "IndeterminateCall",
                    "step %d (%s) was started earlier and its outcome was never recorded; it will not be run again"
                    % (seq, path),
                    "Check the target system by hand to see whether the call took effect. Then reject this execution and "
                    "run the program again with this execution as the one to reuse (run --reuse %s): the calls that "
                    "were recorded before this one are then not made again." % self.exec_id,
                )
            elif state == "pending" and seq not in self.view.approved:
                item["pending"] = rec
            else:
                item.update(self._classify(seq, path, args, approved=(state == "pending")))
            plan.append(item)

        waiting = [i for i in plan if "pending" in i]
        if waiting:
            # Pause before anything in this batch runs: a batch is either
            # fully decided or not started.
            pending = []
            for i in waiting:
                rec = i["pending"]
                if rec is None:
                    rec = self._append(i["seq"], "tool", i["path"], i["args"], i["fp"], state="pending", **i["info"])
                pending.append({"seq": i["seq"], "tool": i["path"], "args": i["args"], "rule": rec.get("rule")})
            raise _Paused(pending)

        live = [i for i in plan if "entry" in i]
        for i in live:
            # Write-ahead: the log names every side effect before it happens.
            self._append(i["seq"], "tool", i["path"], i["args"], i["fp"], state="executing", **i["info"])
            entry = i["entry"]
            i["limit"] = float(self.call_timeout(entry.connector, entry.path, entry.original_path))
            i["done"] = threading.Event()
            i["lock"] = threading.Lock()
        # Daemon threads, not a pool: a stalled tool must not be able to keep
        # the process alive after the pass has given up on it.
        for i in live:
            threading.Thread(target=self._invoke, args=(i,), daemon=True).start()
        for i in live:
            # Each call has its own clock, which starts when the call does.
            # One slow call cannot take the time of the others in its batch.
            state, payload, size = self._await(i, live)
            extra = dict(i["info"], state=state, duration_ms=i.get("duration_ms", 0))
            if state == "applied":
                extra.update(result=payload, result_bytes=size)
            else:
                extra.update(error=payload)
            i["rec"] = self._append(i["seq"], "tool", i["path"], i["args"], i["fp"], **extra)

        results = []
        for i in plan:
            if "fail" in i:
                state, error = i["fail"]
                i["rec"] = self._append(
                    i["seq"], "tool", i["path"], i["args"], i["fp"], state=state, error=error, **i.get("info", {})
                )
            reply = self._reply(i["rec"])
            if reply["ok"]:
                self.tool_result_bytes += i["rec"].get("result_bytes") or 0
            results.append(reply)
        return {"ok": True, "results": results}

    def _classify(self, seq: int, path: str, args: Dict[str, Any], approved: bool) -> Dict[str, Any]:
        """Decide what happens to a call that has no recorded outcome."""
        if self.wind_down and time.monotonic() > self.deadline:
            return {"fail": ("error", {
                "type": "ToolTimeout",
                "message": "%s was not started: the program has reached its time limit (limits.timeout_s = %g)"
                % (path, self.limits["timeout_s"]),
                "hint": "End the program now and return what it has.",
            })}
        if self.tool_calls > int(self.limits["max_calls"]):
            return {"fail": ("error", {
                "type": "CallLimitExceeded",
                "message": "this execution made more than %d tool calls (limits.max_calls)" % self.limits["max_calls"],
                "hint": "Split the work over several programs, or raise limits.max_calls.",
            })}
        try:
            self._size(args, "the arguments of %s" % path)
            entry = self.catalog.resolve(path)
        except ToolFailure as failure:
            return {"fail": ("error", failure.to_dict())}
        decision = self._decide(entry, args)
        info = {"decision": decision.action, "rule": decision.rule, "enforced": decision.enforced}
        # Deny is checked before the approval: a human said yes to the call
        # as the policy stood, and a deny rule added since then still wins.
        if decision.effective == "deny":
            return {"info": info, "fail": ("denied", {
                "type": "PolicyDenied",
                "message": "%s is denied by policy (rule %r)%s"
                % (entry.path, decision.rule, "; the earlier approval does not override a deny rule" if approved else ""),
                "hint": "Do not retry or look for another way to make this call. Tell the user it was denied and by which rule.",
            })}
        if approved:
            return {"entry": entry, "info": dict(info, approved=True)}
        if decision.effective == "ask":
            verdict = None
            if self.approver is not None:
                asked = time.monotonic()
                verdict = self.approver({"seq": seq, "tool": entry.path, "args": args, "rule": decision.rule})
                self.deadline += time.monotonic() - asked  # time spent waiting on a human is not the program's
            if verdict is True:
                self.store.append(self.exec_id, {"type": "approval", "seqs": [seq], "by": "interactive"})
                self.view.approved.add(seq)
                return {"entry": entry, "info": dict(info, approved=True)}
            if verdict is False:
                return {"info": info, "fail": ("denied", {
                    "type": "PolicyDenied",
                    "message": "%s was refused by the human" % entry.path,
                })}
            return {"pending": None, "info": info}
        return {"entry": entry, "info": info}

    def _invoke(self, item: Dict[str, Any]) -> None:
        """Runs on a worker thread. Writes one slot; the pass reads it once."""
        entry = item["entry"]
        gate = self.catalog.gate_for(entry)
        if gate is not None:
            gate.acquire()  # queued behind earlier calls to a connector with max_concurrency
            with item["lock"]:
                if item.get("abandoned"):
                    gate.release()
                    item["done"].set()
                    return
                item["gate"] = gate
        started = time.monotonic()
        remaining = self.deadline - started
        item["cut_by_program"] = remaining < item["limit"]
        item["timeout"] = max(0.1, min(item["limit"], remaining))
        item["started"] = started  # last: the waiting pass reads "timeout" once it sees this
        connector = self.catalog.connector_for(entry)
        try:
            ctx = CallContext(
                self.exec_id, self.agent, item["seq"], item["timeout"],
                progress=lambda text, seq=item["seq"]: self._progress(seq, text),
            )
            item["ctx"] = ctx
            value, _ = redact(connector.call(entry.original, item["args"], ctx), connector.secrets())
            try:
                slot = ("applied", value, self._size(value, "the result of %s" % entry.path))
            except ToolFailure as too_large:
                if too_large.kind != "ValueTooLarge":
                    raise
                # The call ran and its side effect happened. Failing it must
                # not also throw the result away.
                saved = self.store.save_blob(self.exec_id, item["seq"], value)
                raise ToolFailure(
                    "ValueTooLarge",
                    too_large.message + (" The whole result was saved on the host: %s" % saved if saved else ""),
                    "Do not repeat the call to get the result: it ran. Read the saved file, or call the tool in a way "
                    "that returns less; limits.max_value_bytes sets the limit.",
                )
        except ToolFailure as failure:
            error = self._timeout_error(item) if failure.kind == "ToolTimeout" else failure.to_dict()
            slot = ("error", redact(error, connector.secrets())[0], 0)
        except Exception as exc:  # a connector bug is a failed call, not a crashed runtime
            message = redact("%s: %s" % (type(exc).__name__, exc), connector.secrets())[0]
            slot = ("error", {"type": "ToolCrashed", "message": message}, 0)
        finally:
            self._release(item)
        item["duration_ms"] = int((time.monotonic() - started) * 1000)
        item["slot"] = slot
        item["done"].set()

    @staticmethod
    def _release(item: Dict[str, Any]) -> None:
        with item["lock"]:
            gate = item.pop("gate", None)
        if gate is not None:
            gate.release()

    def _await(self, item: Dict[str, Any], batch: List[Dict[str, Any]]) -> Any:
        """Wait for one live call: until it answers, until its own time limit
        has passed since it started, or until the execution is cancelled."""
        while not item["done"].wait(0.1):
            if self.cancel_event is not None and self.cancel_event.is_set():
                for other in batch:
                    self._give_up(other)
                # The entry stays "executing": the log says the call was
                # started and that nobody saw how it ended.
                raise _Abort("Cancelled", "cancelled while %s was running; its outcome is unknown" % item["path"])
            now = time.monotonic()
            started = item.get("started")
            if started is not None and now > started + item["timeout"] + _GRACE_S:
                break  # connectors report their own timeouts first; this one did not
            if started is None and now > self.deadline + _GRACE_S:
                break  # still queued when the program's time ran out
        if "slot" not in item:
            self._give_up(item)
            if "started" in item:
                item.setdefault("duration_ms", int((time.monotonic() - item["started"]) * 1000))
            return ("error", self._timeout_error(item), 0)
        return item["slot"]

    def _give_up(self, item: Dict[str, Any]) -> None:
        """Stop waiting for a call: free its place in the queue and tell the
        tool source, which may be able to stop the work."""
        with item["lock"]:
            if item.get("abandoned") or "slot" in item:
                return
            item["abandoned"] = True
        self._release(item)
        ctx = item.get("ctx")
        if ctx is not None:
            try:
                self.catalog.connector_for(item["entry"]).cancel(ctx)
            except Exception:
                pass  # best effort; the call is already recorded as not answered

    def _timeout_error(self, item: Dict[str, Any]) -> Dict[str, Any]:
        """Say which limit ended the call, and what to do about that one."""
        path = item["path"]
        connector = item["entry"].connector
        program_limit = float(self.limits["timeout_s"])
        if "started" not in item or item.get("cut_by_program"):
            if not self.wind_down:
                self.wind_down = time.monotonic() + _WIND_DOWN_S
            if path not in self.cut_short:
                self.cut_short.append(path)
        if "started" not in item:
            return {
                "type": "ToolTimeout",
                "message": "%s never started: it was waiting behind earlier calls to %r (max_concurrency %s) when the "
                "program reached its time limit (limits.timeout_s = %g)"
                % (path, connector, self.catalog.max_concurrency(connector), program_limit),
                "hint": "It did not run. Do less in one program, or raise limits.timeout_s.",
            }
        if item.get("cut_by_program"):
            return {
                "type": "ToolTimeout",
                "message": "%s was given up on after %gs because the program reached its time limit "
                "(limits.timeout_s = %g); the call may still be running" % (path, item["timeout"], program_limit),
                "hint": "Raise limits.timeout_s, or do less in one program. Check the target before you retry: "
                "the call may have completed.",
            }
        return {
            "type": "ToolTimeout",
            "message": "%s did not answer within %gs; it may still be running" % (path, item["timeout"]),
            "hint": 'Give this tool more time: "call_timeouts": {"%s": %d} under limits, or call_timeout_s on the '
            "%s connector. Check the target before you retry: the call may have completed."
            % (path, max(60, int(item["timeout"] * 4)), connector),
        }

    # -- the end ----------------------------------------------------------

    def _on_done(self, frame: Dict[str, Any]) -> PassOutcome:
        if not frame.get("ok"):
            error = frame.get("error") if isinstance(frame.get("error"), dict) else {}
            error.setdefault("type", "ProgramError")
            error.setdefault("message", "the program failed")
            kept = frame.get("vars") if isinstance(frame.get("vars"), dict) else {}
            omitted = frame.get("vars_omitted") if isinstance(frame.get("vars_omitted"), list) else []
            try:
                # The budget was the sandbox's to keep; the host checks it.
                if byte_len(kept) > int(self.limits["max_value_bytes"]) + 1024:
                    kept, omitted = {}, [{"name": name, "chars": 0} for name in sorted(kept)]
            except (TypeError, ValueError):
                kept, omitted = {}, []
            return PassOutcome(status="error", error=error, vars=kept, vars_omitted=omitted)
        if self.mode == "verify":
            recorded = max(self.view.calls, default=0)
            if self.seq != recorded:
                raise _Abort(
                    "VerifyMismatch",
                    "the log has %d steps but the replay made %d" % (recorded, self.seq),
                    _DIVERGENCE_HINT,
                )
        state = frame.get("state") if isinstance(frame.get("state"), dict) else {}
        notes = [str(note)[:600] for note in (frame.get("notes") if isinstance(frame.get("notes"), list) else [])[:5]]
        if self.cut_short and self.mode == "live":
            notes.append(
                "The program reached its time limit (limits.timeout_s = %g) while %s was still running. That call was "
                "given up on and may still be running; the result covers only what finished in time."
                % (self.limits["timeout_s"], ", ".join(self.cut_short[:5]))
            )
        return PassOutcome(
            status="completed",
            result=frame.get("result"),
            result_repr=bool(frame.get("result_repr")),
            state_out=state,
            notes=notes,
        )
