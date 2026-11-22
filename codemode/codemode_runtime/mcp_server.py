# SPDX-License-Identifier: MIT
"""The runtime as an MCP server over stdio.

This is how any agent, on any model, uses the runtime: its harness starts
``codemode serve`` as an MCP server and sees exactly one tool, ``execute``.
The tool takes ``code`` (run a program) or ``executionId`` (ask about an
earlier one: its outcome after a human approved it, its progress while it
runs in the background, one part of a large result). There is deliberately
no approve tool, and nothing here edits policy or the log.

For an agent this is the better of the two surfaces: a step is one tool
call, the program travels as JSON with no shell in between to change its
backslashes, and the tool servers stay up between programs instead of being
started for each one.

Written against the public MCP spec with the standard library: JSON-RPC 2.0,
one message per line on stdin and stdout. Nothing else may be written to
stdout, so all diagnostics go to stderr.
"""

from __future__ import annotations

import json
import queue
import sys
import threading
from typing import Any, BinaryIO, Dict, Optional

from . import __version__
from .errors import CodemodeError
from .jsonutil import dumps_line
from .runtime import Runtime

SUPPORTED_VERSIONS = ("2025-06-18", "2025-03-26", "2024-11-05")
_MAX_WAIT_S = 600.0
_PROGRESS_EVERY_S = 5.0

_INPUT_SCHEMA = {
    "type": "object",
    "properties": {
        "code": {"type": "string", "description": "The Python program to run."},
        "executionId": {
            "type": "string",
            "description": "Instead of code: return the current outcome of this earlier execution.",
        },
        "session": {
            "type": "string",
            "description": "Which state dict the program sees. Defaults to 'default'.",
        },
        "args": {"type": "object", "description": "With code: arguments for the program, read there as args[name]."},
        "background": {
            "type": "boolean",
            "description": "With code: return the executionId at once and keep the program running. Ask about it with executionId.",
        },
        "reuse": {
            "type": "string",
            "description": "With code: an earlier executionId. Steps that match that execution are taken from its log and not run again.",
        },
        "check": {"type": "boolean", "description": "With code: do not run; compile the program and look up the tools it names."},
        "wait": {"type": "number", "description": "With executionId: wait up to this many seconds for it to stop running."},
        "select": {
            "type": "string",
            "description": "With executionId: return one part only, by dotted path: result.rows.0, variables.NAME, logs.",
        },
        "cancel": {"type": "boolean", "description": "With executionId: stop the execution. Calls that already ran stay applied."},
    },
    "additionalProperties": False,
}


class McpServer:
    def __init__(self, runtime: Runtime, stdin: BinaryIO, stdout: BinaryIO) -> None:
        self._runtime = runtime
        self._stdin = stdin
        self._stdout = stdout
        self._write_lock = threading.Lock()
        self._inbox: "queue.Queue[Optional[Dict[str, Any]]]" = queue.Queue()
        self._cancels: Dict[Any, threading.Event] = {}  # request id -> the event that stops its execution
        self._workers: Dict[Any, threading.Thread] = {}  # request id -> the thread running it
        self._state_lock = threading.Lock()
        runtime.surface = "mcp"  # hints then name the tool call to make, not a command line

    # -- transport --------------------------------------------------------

    def _send(self, message: Dict[str, Any]) -> None:
        with self._write_lock:
            self._stdout.write(dumps_line(message))
            self._stdout.flush()

    def _result(self, request_id: Any, result: Dict[str, Any]) -> None:
        self._send({"jsonrpc": "2.0", "id": request_id, "result": result})

    def _error(self, request_id: Any, code: int, message: str) -> None:
        self._send({"jsonrpc": "2.0", "id": request_id, "error": {"code": code, "message": message}})

    def _read(self) -> None:
        """Reader thread. It only queues, except for cancellation, which has
        to act while a worker is busy running the request being cancelled."""
        for line in self._stdin:
            if not line.strip():
                continue
            try:
                message = json.loads(line)
            except ValueError:
                self._error(None, -32700, "parse error: each line must be one JSON-RPC message")
                continue
            if not isinstance(message, dict):
                self._error(None, -32600, "invalid request: batches are not supported")
                continue
            if message.get("method") == "notifications/cancelled":
                params = message.get("params") if isinstance(message.get("params"), dict) else {}
                with self._state_lock:
                    cancel = self._cancels.get(params.get("requestId"))
                    worker = self._workers.get(params.get("requestId"))
                if cancel is not None:
                    cancel.set()
                if worker is not None:
                    # Whatever the client sends next is answered once the
                    # cancelled program has been stopped and recorded: a
                    # client that cancels and then asks must not be told
                    # the execution is still running.
                    self._inbox.put({"settle": worker})
                continue
            self._inbox.put(message)
        self._inbox.put(None)

    def serve(self) -> None:
        threading.Thread(target=self._read, daemon=True).start()
        while True:
            message = self._inbox.get()
            if message is None:
                return  # stdin closed: the client is gone
            if "settle" in message:
                message["settle"].join(timeout=15)
                continue
            self._handle(message)

    # -- methods ----------------------------------------------------------

    def _handle(self, message: Dict[str, Any]) -> None:
        method = message.get("method")
        request_id = message.get("id")
        is_request = "id" in message
        if not isinstance(method, str):
            return  # a response to something we never asked; ignore
        if not is_request:
            return  # notifications need no answer
        params = message.get("params") if isinstance(message.get("params"), dict) else {}
        try:
            if method == "initialize":
                wanted = params.get("protocolVersion")
                self._result(request_id, {
                    "protocolVersion": wanted if wanted in SUPPORTED_VERSIONS else SUPPORTED_VERSIONS[0],
                    "capabilities": {"tools": {}},
                    "serverInfo": {"name": "codemode-runtime", "version": __version__},
                    "instructions": "One tool, execute: write a Python program that calls tools. "
                    "Use search() and describe() inside the program to find them.",
                })
            elif method == "ping":
                self._result(request_id, {})
            elif method == "tools/list":
                self._result(request_id, {"tools": [{
                    "name": "execute",
                    "description": self._runtime.tool_description(),
                    "inputSchema": _INPUT_SCHEMA,
                }]})
            elif method == "tools/call":
                if params.get("name") != "execute":
                    self._error(request_id, -32602, "unknown tool %r; the only tool is 'execute'" % (params.get("name"),))
                    return
                # On its own thread: a program may run for minutes, and the
                # client must still get answers to ping, to a question about
                # another execution, and to a cancellation.
                cancel = threading.Event()
                worker = threading.Thread(target=self._call, args=(request_id, params, cancel), daemon=True)
                with self._state_lock:
                    self._cancels[request_id] = cancel
                    self._workers[request_id] = worker
                worker.start()
            else:
                self._error(request_id, -32601, "method not found: %s" % method)
        except Exception as exc:  # the server must outlive any single request
            print("codemode-runtime: %s failed: %r" % (method, exc), file=sys.stderr)
            self._error(request_id, -32603, "internal error: %s" % exc)

    def _call(self, request_id: Any, params: Dict[str, Any], cancel: threading.Event) -> None:
        args = params.get("arguments") if isinstance(params.get("arguments"), dict) else {}
        token = (params.get("_meta") or {}).get("progressToken") if isinstance(params.get("_meta"), dict) else None
        try:
            payload = self._execute(args, cancel, token)
        except CodemodeError as exc:
            # Usage problems are tool results the model can read and fix, not
            # protocol errors.
            payload = {"status": "error", "error": exc.to_dict()}
        except Exception as exc:  # the server must outlive any single request
            print("codemode-runtime: execute failed: %r" % (exc,), file=sys.stderr)
            payload = {"status": "error", "error": {"type": "InternalError", "message": "%s: %s" % (type(exc).__name__, exc)}}
        finally:
            with self._state_lock:
                self._cancels.pop(request_id, None)
                self._workers.pop(request_id, None)
        if cancel.is_set():
            return  # the spec says a cancelled request gets no response
        self._result(request_id, {
            "content": [{"type": "text", "text": json.dumps(payload, ensure_ascii=True)}],
            "structuredContent": payload,
            "isError": payload.get("status") in ("error", "rejected"),
        })

    def _execute(self, args: Dict[str, Any], cancel: threading.Event, token: Any) -> Dict[str, Any]:
        runtime = self._runtime
        code, exec_id = args.get("code"), args.get("executionId")
        if (code is None) == (exec_id is None):
            return {"status": "error", "error": {
                "type": "UsageError",
                "message": "pass exactly one of 'code' and 'executionId'",
                "hint": "code runs a program; executionId asks about an earlier one.",
            }}
        if exec_id is not None:
            ref = str(exec_id)
            if args.get("cancel"):
                return runtime.cancel(ref, by=runtime.agent)
            if args.get("select"):
                return runtime.get(ref, select=str(args["select"]))
            if args.get("wait") is not None:
                return runtime.wait(ref, max(0.0, min(float(args["wait"]), _MAX_WAIT_S)))
            return runtime.get(ref)
        source, session = str(code), str(args.get("session", "default"))
        if args.get("check"):
            return runtime.check(source)
        reuse = str(args["reuse"]) if args.get("reuse") else None
        started = runtime.start(source, session, args=args.get("args"), reuse=reuse)
        if args.get("background"):
            from .detach import spawn_worker

            return spawn_worker(runtime, started)
        done = threading.Event()
        if token is not None:
            threading.Thread(target=self._report, args=(started, token, done), daemon=True).start()
        try:
            return runtime.run_started(started, cancel_event=cancel)
        finally:
            done.set()

    def _report(self, exec_id: str, token: Any, done: threading.Event) -> None:
        """While a program runs in the foreground, tell a client that asked
        for progress where it is. Some clients extend their time limit for a
        call that keeps reporting; all of them can show it."""
        sent = 0
        while not done.wait(_PROGRESS_EVERY_S):
            try:
                progress = self._runtime.get(exec_id).get("progress") or {}
            except CodemodeError:
                return
            running = ", ".join("%s for %ss" % (item["tool"], item["forS"]) for item in progress.get("running", [])[:3])
            sent += 1
            self._send({"jsonrpc": "2.0", "method": "notifications/progress", "params": {
                "progressToken": token,
                "progress": sent,
                "message": "%d call(s) done%s" % (progress.get("callsDone", 0), ("; running " + running) if running else ""),
            }})


def serve(runtime: Runtime) -> None:
    McpServer(runtime, sys.stdin.buffer, sys.stdout.buffer).serve()
