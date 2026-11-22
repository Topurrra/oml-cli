# SPDX-License-Identifier: MIT
"""What every MCP transport shares: the handshake, the tool list, calls.

MCP is the open way to hand tools to an agent, so this is the main route for
real capabilities. It is a small client written against the public spec with
the standard library: JSON-RPC 2.0, ``initialize`` then ``tools/list`` and
``tools/call``. Only the tools part of the protocol is used.

A transport subclass provides three things: ``_ensure`` (connect, once),
``_exchange`` (send one request, return its response) and ``_notify``. The
transports are ``mcp_stdio`` (a local process), and in ``mcp_http`` the two
remote ones the spec defines: streamable HTTP and the older HTTP+SSE.

Credentials stay on the host. They come from the connector's ``env`` or
``headers`` in the config (``${NAME}`` references resolved here) and never
reach the sandbox; the values are also reported through ``secrets()`` so the
gateway can remove them from anything a server echoes back.
"""

from __future__ import annotations

import itertools
import json
import threading
from typing import Any, Callable, Dict, List, Optional

from .. import __version__
from ..errors import ToolFailure
from ..jsonutil import canonical, sha256_hex
from . import CallContext, Connector

PROTOCOL_VERSION = "2025-06-18"
SUPPORTED_VERSIONS = ("2025-06-18", "2025-03-26", "2024-11-05")
MAX_MESSAGE_BYTES = 64 * 1024 * 1024
_MAX_PAGES = 100

Progress = Optional[Callable[[str], None]]


def _reject_constant(name: str) -> Any:
    raise ValueError("non-finite number %s" % name)


def unwrap_result(result: Any, parse_json_text: bool = True) -> Any:
    """Turn a ``tools/call`` result into a plain value for the program.

    Structured content wins. A single text block that is JSON is parsed,
    because many servers return JSON as text and the program wants the value,
    not a string to parse again; ``parse_json_text=False`` (a connector
    option) keeps text as text, for servers whose text is a document that
    may happen to be JSON. A result marked ``isError`` becomes a failure the
    program can catch.
    """
    if not isinstance(result, dict):
        return result
    content = result.get("content") if isinstance(result.get("content"), list) else []
    texts = [b.get("text", "") for b in content if isinstance(b, dict) and b.get("type") == "text"]
    if result.get("isError"):
        raise ToolFailure("ToolError", "\n".join(texts) or "the tool reported an error")
    if result.get("structuredContent") is not None:
        return result["structuredContent"]
    others = [
        {"type": b.get("type"), "mimeType": b.get("mimeType"), "chars": len(str(b.get("data", "")))}
        for b in content
        if isinstance(b, dict) and b.get("type") != "text"
    ]
    if others:
        # Binary content is described, not inlined: the program gets what it
        # can use, and megabytes of base64 stay out of the log.
        return {"text": texts, "other": others}
    if len(texts) == 1:
        if not parse_json_text:
            return texts[0]
        try:
            return json.loads(texts[0], parse_constant=_reject_constant)
        except ValueError:
            return texts[0]
    return texts if texts else None


def progress_text(params: Any) -> str:
    """A ``notifications/progress`` as one short line."""
    if not isinstance(params, dict):
        return ""
    message = str(params.get("message") or "").strip()
    done, total = params.get("progress"), params.get("total")
    if isinstance(done, (int, float)) and not isinstance(done, bool):
        count = ("%g of %g" % (done, total)) if isinstance(total, (int, float)) and not isinstance(total, bool) else "%g" % done
        return ("%s (%s)" % (message, count)) if message else count
    return message


class Mailbox:
    """Responses waiting to be claimed, for a transport where every message
    from the server arrives on one stream (stdio, HTTP+SSE).

    One mailbox belongs to one connection. When a server is restarted it gets
    a new one, so a reader thread that is still draining the old connection
    cannot wake a request that was sent on the new one.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._waiting: Dict[Any, Dict[str, Any]] = {}
        self._tokens: Dict[Any, Callable[[str], None]] = {}
        self.closed = False
        self.noise = 0  # lines that were not protocol messages
        self.first_noise = ""

    def expect(self, request: Dict[str, Any], on_progress: Progress) -> Dict[str, Any]:
        slot: Dict[str, Any] = {"event": threading.Event(), "message": None, "token": None}
        token = ((request.get("params") or {}).get("_meta") or {}).get("progressToken")
        with self._lock:
            if self.closed:
                slot["event"].set()
                return slot
            self._waiting[request["id"]] = slot
            if token is not None and on_progress is not None:
                slot["token"] = token
                self._tokens[token] = on_progress
        return slot

    def forget(self, request_id: Any) -> None:
        with self._lock:
            slot = self._waiting.pop(request_id, None)
            if slot is not None and slot["token"] is not None:
                self._tokens.pop(slot["token"], None)

    def deliver(self, message: Any) -> Optional[Dict[str, Any]]:
        """Route one message from the server. Returns a reply to send back,
        when the message was a request from the server."""
        if not isinstance(message, dict):
            return None
        if "method" in message and "id" in message:
            # The server is asking the client something. Only ping is
            # supported; saying so beats leaving it waiting.
            if message["method"] == "ping":
                return {"jsonrpc": "2.0", "id": message["id"], "result": {}}
            return {"jsonrpc": "2.0", "id": message["id"], "error": {"code": -32601, "message": "not supported by this client"}}
        if message.get("method") == "notifications/progress":
            params = message.get("params") if isinstance(message.get("params"), dict) else {}
            with self._lock:
                callback = self._tokens.get(params.get("progressToken"))
            text = progress_text(params)
            if callback is not None and text:
                try:
                    callback(text)
                except Exception:
                    pass  # reporting progress must never break the call
            return None
        if "id" in message:
            with self._lock:
                slot = self._waiting.pop(message["id"], None)
                if slot is not None and slot["token"] is not None:
                    self._tokens.pop(slot["token"], None)
            if slot is not None:
                slot["message"] = message
                slot["event"].set()
        return None

    def note_noise(self, line: bytes) -> None:
        self.noise += 1
        if not self.first_noise:
            self.first_noise = line[:120].decode("utf-8", "replace").strip()

    def close(self) -> None:
        """The connection is gone. Wake everyone who is waiting on it."""
        with self._lock:
            self.closed = True
            slots = list(self._waiting.values())
            self._waiting.clear()
            self._tokens.clear()
        for slot in slots:
            slot["event"].set()

    def noise_note(self) -> str:
        if not self.noise:
            return ""
        return "; the server wrote %d line(s) to its output that are not protocol messages, the first: %r" % (self.noise, self.first_noise)


class McpConnector(Connector):
    """The protocol, without a transport."""

    transport = "mcp"
    cacheable = True

    def stamp(self) -> str:
        return sha256_hex(canonical([self._spec, self._file_stamps()]))

    def _file_stamps(self) -> List[Any]:
        """Local files the server is made of, with their size and time of
        change. None for a remote server."""
        return []

    def __init__(self, name: str, spec: Dict[str, Any], startup_timeout_s: float) -> None:
        self.name = name
        self._spec = spec
        self._startup_timeout = float(startup_timeout_s)
        self._ids = itertools.count(1)
        self._tools: Optional[List[Dict[str, Any]]] = None
        self._secrets: List[str] = []
        self._parse_json_text = bool(spec.get("parse_json_text", True))
        self._in_flight: Dict[int, Any] = {}  # call sequence number -> request id, for cancel()
        self.protocol_version = PROTOCOL_VERSION
        self.server_info: Dict[str, Any] = {}

    # -- what a transport provides ---------------------------------------

    def _ensure(self) -> None:
        raise NotImplementedError

    def _exchange(self, request: Dict[str, Any], timeout: float, on_progress: Progress) -> Dict[str, Any]:
        """Send one request and return the response message. Raises
        ``ToolFailure``: ``ToolTimeout`` or ``ConnectorUnavailable``."""
        raise NotImplementedError

    def _notify(self, method: str, params: Dict[str, Any]) -> None:
        raise NotImplementedError

    # -- the protocol ------------------------------------------------------

    def _request(self, method: str, params: Dict[str, Any], timeout: float, on_progress: Progress = None, seq: Optional[int] = None) -> Any:
        request = {"jsonrpc": "2.0", "id": next(self._ids), "method": method, "params": params}
        if seq is not None:
            self._in_flight[seq] = request["id"]
        try:
            message = self._exchange(request, timeout, on_progress)
        finally:
            if seq is not None:
                self._in_flight.pop(seq, None)
        if "error" in message:
            error = message["error"] if isinstance(message["error"], dict) else {}
            raise ToolFailure("ToolError", "%s (code %s)" % (error.get("message", "error"), error.get("code")))
        return message.get("result")

    def _handshake(self) -> None:
        result = self._request(
            "initialize",
            {
                "protocolVersion": PROTOCOL_VERSION,
                "capabilities": {},
                "clientInfo": {"name": "codemode-runtime", "version": __version__},
            },
            self._startup_timeout,
        )
        result = result if isinstance(result, dict) else {}
        version = result.get("protocolVersion")
        # The server answers with the version it will speak. An older one is
        # fine: the tools part of the protocol has not changed shape.
        self.protocol_version = version if isinstance(version, str) and version else PROTOCOL_VERSION
        self.server_info = result.get("serverInfo") if isinstance(result.get("serverInfo"), dict) else {}
        self._notify("notifications/initialized", {})

    def list_tools(self) -> List[Dict[str, Any]]:
        self._ensure()
        if self._tools is None:
            tools: List[Dict[str, Any]] = []
            cursor = None
            for _ in range(_MAX_PAGES):  # a server that pages forever must not hang the catalog
                params = {"cursor": cursor} if cursor else {}
                result = self._request("tools/list", params, self._startup_timeout) or {}
                tools.extend(t for t in result.get("tools", []) if isinstance(t, dict) and isinstance(t.get("name"), str))
                cursor = result.get("nextCursor")
                if not cursor:
                    break
            self._tools = tools
        return self._tools

    def call(self, tool: str, args: Dict[str, Any], ctx: CallContext) -> Any:
        self._ensure()
        params: Dict[str, Any] = {"name": tool, "arguments": args}
        if ctx.progress is not None:
            # Asking for progress costs the server nothing if it has none to
            # give, and a long call that reports it can be watched.
            params["_meta"] = {"progressToken": "%s-%d" % (ctx.execution_id, ctx.seq)}
        result = self._request("tools/call", params, ctx.timeout_s, ctx.progress, seq=ctx.seq)
        return unwrap_result(result, self._parse_json_text)

    def cancel(self, ctx: CallContext) -> None:
        """Tell the server the caller has given up on a call. A server that
        honours it can stop the work; one that does not loses nothing."""
        request_id = self._in_flight.get(ctx.seq)
        if request_id is None:
            return
        try:
            self._notify("notifications/cancelled", {"requestId": request_id, "reason": "the caller gave up on this call"})
        except ToolFailure:
            pass

    def secrets(self) -> List[str]:
        return list(self._secrets)
