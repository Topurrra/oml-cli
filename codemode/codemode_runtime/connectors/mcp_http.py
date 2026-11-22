# SPDX-License-Identifier: MIT
"""Connectors for MCP servers reached over HTTP.

The spec defines two remote transports, and both are here, written with the
standard library:

- ``mcp-http``: streamable HTTP (2025-03-26 and later). Every message is a
  POST to one URL. The answer is either a JSON body or a ``text/event-stream``
  that carries notifications and then the response. The server may hand out
  an ``Mcp-Session-Id`` at ``initialize``; it is sent with every later
  request, and a 404 for it means the session is gone and a new one is made.
- ``mcp-sse``: HTTP with server-sent events (2024-11-05). A GET opens one
  long stream; its first event names the URL to POST messages to, and every
  response comes back on the stream.

Credentials are sent as ``headers`` from the config (``${NAME}`` references,
resolved on the host). OAuth flows are not implemented: a server that needs
one is given a token obtained elsewhere, in a header.
"""

from __future__ import annotations

import http.client
import json
import socket
import ssl
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
from typing import Any, Dict, Iterator, List, Optional, Tuple

from ..errors import ToolFailure
from .mcp_common import MAX_MESSAGE_BYTES, Mailbox, McpConnector, Progress
from .mcp_stdio import resolve_env

_ACCEPT = "application/json, text/event-stream"
_AUTH_HINT = (
    "The server refused the credentials. Set them as headers on the connector, for example "
    '"headers": {"Authorization": "Bearer ${TOKEN_NAME}"}, and export that variable.'
)


def iter_sse(lines: Iterator[bytes]) -> Iterator[Tuple[str, str]]:
    """Parse a ``text/event-stream`` into (event name, data) pairs.

    Pure: it takes lines and yields events. Data spread over several
    ``data:`` lines is joined with newlines, comment lines (``:``) are
    dropped, and an event with no data is not dispatched, as the HTML
    standard says.
    """
    event = "message"
    data: List[str] = []
    for raw in lines:
        line = raw.decode("utf-8", "replace").rstrip("\r\n")
        if not line:
            if data:
                yield event, "\n".join(data)
            event, data = "message", []
            continue
        if line.startswith(":"):
            continue
        field, _, value = line.partition(":")
        if value.startswith(" "):
            value = value[1:]
        if field == "event":
            event = value
        elif field == "data":
            data.append(value)
    if data:
        yield event, "\n".join(data)


def _reason(exc: BaseException) -> str:
    return str(getattr(exc, "reason", None) or exc) or type(exc).__name__


def _is_timeout(exc: BaseException) -> bool:
    reason = getattr(exc, "reason", None)
    return isinstance(exc, (socket.timeout, TimeoutError)) or isinstance(reason, (socket.timeout, TimeoutError))


class _HttpBase(McpConnector):
    def __init__(self, name: str, spec: Dict[str, Any], startup_timeout_s: float) -> None:
        super().__init__(name, spec, startup_timeout_s)
        self._url = str(spec["url"])
        self._state_lock = threading.RLock()
        self._configured: Optional[Dict[str, str]] = None

    def _base_headers(self) -> Dict[str, str]:
        if self._configured is None:
            self._configured, self._secrets = resolve_env(self.name, self._spec, "headers")
        return dict(self._configured)

    def _http_failure(self, exc: urllib.error.HTTPError, what: str) -> ToolFailure:
        detail = ""
        try:
            detail = exc.read(600).decode("utf-8", "replace").strip()
        except Exception:
            pass
        message = "MCP server %r answered HTTP %d to %s%s" % (self.name, exc.code, what, (": " + detail[:300]) if detail else "")
        if exc.code in (401, 403):
            return ToolFailure("ConnectorUnavailable", message, _AUTH_HINT)
        return ToolFailure("ConnectorUnavailable", message, "Check connectors.%s.url (%s)." % (self.name, self._url))

    def _unreachable(self, exc: BaseException, what: str, timeout: float) -> ToolFailure:
        if _is_timeout(exc):
            return ToolFailure("ToolTimeout", "%s on MCP server %r did not answer within %gs" % (what, self.name, timeout))
        return ToolFailure(
            "ConnectorUnavailable",
            "could not reach MCP server %r at %s: %s" % (self.name, self._url, _reason(exc)),
            "Is the server running, and is connectors.%s.url right?" % self.name,
        )


class McpHttpConnector(_HttpBase):
    """Streamable HTTP: one POST per message."""

    transport = "streamable-http"

    def __init__(self, name: str, spec: Dict[str, Any], startup_timeout_s: float) -> None:
        super().__init__(name, spec, startup_timeout_s)
        self._session: Optional[str] = None
        self._ready = False

    def _ensure(self) -> None:
        with self._state_lock:
            if self._ready:
                return
            try:
                self._handshake()
            except ToolFailure as exc:
                if exc.kind == "ConnectorUnavailable":
                    raise
                raise ToolFailure(
                    "ConnectorUnavailable",
                    "MCP server %r did not complete the handshake: %s" % (self.name, exc.message),
                    exc.hint or "Check connectors.%s.url (%s)." % (self.name, self._url),
                )
            self._ready = True

    def _headers(self, initializing: bool) -> Dict[str, str]:
        headers = self._base_headers()
        headers["Content-Type"] = "application/json"
        headers["Accept"] = _ACCEPT
        if not initializing:
            headers["MCP-Protocol-Version"] = self.protocol_version
        if self._session:
            headers["Mcp-Session-Id"] = self._session
        return headers

    def _post(self, message: Dict[str, Any], timeout: float) -> Any:
        initializing = message.get("method") == "initialize"
        request = urllib.request.Request(
            self._url, data=json.dumps(message).encode("utf-8"), headers=self._headers(initializing), method="POST"
        )
        response = urllib.request.urlopen(request, timeout=max(0.1, timeout))
        if initializing and response.headers.get("Mcp-Session-Id"):
            self._session = response.headers.get("Mcp-Session-Id")
        return response

    def _exchange(self, request: Dict[str, Any], timeout: float, on_progress: Progress) -> Dict[str, Any]:
        method = request["method"]
        deadline = time.monotonic() + timeout
        for attempt in (1, 2):
            try:
                response = self._post(request, timeout)
            except urllib.error.HTTPError as exc:
                if exc.code == 404 and self._session and method != "initialize" and attempt == 1:
                    # The server no longer knows the session (it restarted, or
                    # the session expired). The request was not run: start a
                    # new session and send it again, once.
                    with self._state_lock:
                        self._session, self._ready, self._tools = None, False, self._tools
                    self._ensure()
                    continue
                raise self._http_failure(exc, method)
            except (urllib.error.URLError, OSError) as exc:
                if _is_timeout(exc) and method != "initialize":
                    self._tell_cancelled(request)  # the server may still be working on it
                raise self._unreachable(exc, method, timeout)
            try:
                with response:
                    return self._read_response(response, request, deadline, timeout, on_progress)
            except (socket.timeout, TimeoutError):
                self._tell_cancelled(request)
                raise ToolFailure("ToolTimeout", "%s on MCP server %r did not answer within %gs" % (method, self.name, timeout))
            except (OSError, http.client.HTTPException) as exc:
                raise ToolFailure(
                    "ConnectorUnavailable",
                    "the connection to MCP server %r broke during %s: %s" % (self.name, method, _reason(exc)),
                    "If the call changed something, check the target before you repeat it.",
                )
        raise ToolFailure("ConnectorUnavailable", "MCP server %r kept rejecting the session" % self.name)

    def _read_response(self, response: Any, request: Dict[str, Any], deadline: float, timeout: float, on_progress: Progress) -> Dict[str, Any]:
        content_type = (response.headers.get("Content-Type") or "").lower()
        if "text/event-stream" not in content_type:
            body = response.read(MAX_MESSAGE_BYTES + 1)
            if len(body) > MAX_MESSAGE_BYTES:
                raise ToolFailure("ValueTooLarge", "the response of MCP server %r is larger than %d bytes" % (self.name, MAX_MESSAGE_BYTES))
            return self._pick(self._parse(body, request["method"]), request)
        box = Mailbox()  # for this one stream: it routes progress and finds the response
        slot = box.expect(request, on_progress)

        def lines() -> Iterator[bytes]:
            while True:
                if time.monotonic() > deadline:
                    raise socket.timeout()
                line = response.readline(MAX_MESSAGE_BYTES + 1)
                if not line:
                    return
                yield line

        for _event, data in iter_sse(lines()):
            try:
                message = json.loads(data)
            except ValueError:
                continue
            reply = box.deliver(message)
            if reply is not None:
                self._send_quietly(reply)  # the server asked us something on the stream
            if slot["message"] is not None:
                return slot["message"]
        raise ToolFailure(
            "ConnectorUnavailable",
            "MCP server %r closed the stream of %s before sending its response" % (self.name, request["method"]),
            "If the call changed something, check the target before you repeat it.",
        )

    def _parse(self, body: bytes, method: str) -> Any:
        try:
            return json.loads(body)
        except ValueError:
            preview = body[:160].decode("utf-8", "replace").strip()
            raise ToolFailure(
                "ConnectorUnavailable",
                "MCP server %r answered %s with something that is not JSON: %s" % (self.name, method, preview),
                "Is connectors.%s.url (%s) the MCP endpoint itself, not a web page in front of it?" % (self.name, self._url),
            )

    def _pick(self, parsed: Any, request: Dict[str, Any]) -> Dict[str, Any]:
        candidates = parsed if isinstance(parsed, list) else [parsed]
        for message in candidates:
            if isinstance(message, dict) and message.get("id") == request["id"]:
                return message
        raise ToolFailure("ConnectorUnavailable", "MCP server %r did not answer %s" % (self.name, request["method"]))

    def _send_quietly(self, message: Dict[str, Any]) -> None:
        try:
            self._post(message, 10.0).close()
        except Exception:
            pass

    def _tell_cancelled(self, request: Dict[str, Any]) -> None:
        self._send_quietly({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": request["id"], "reason": "timeout"}})

    def _notify(self, method: str, params: Dict[str, Any]) -> None:
        try:
            self._post({"jsonrpc": "2.0", "method": method, "params": params}, min(30.0, self._startup_timeout)).close()
        except urllib.error.HTTPError as exc:
            raise self._http_failure(exc, method)
        except (urllib.error.URLError, OSError) as exc:
            raise self._unreachable(exc, method, self._startup_timeout)

    def close(self) -> None:
        session, self._session, self._ready = self._session, None, False
        if not session:
            return
        try:  # ending the session is a courtesy the spec asks for; the server may refuse it
            headers = self._base_headers()
            headers["Mcp-Session-Id"] = session
            urllib.request.urlopen(urllib.request.Request(self._url, headers=headers, method="DELETE"), timeout=3).close()
        except Exception:
            pass


class McpSseConnector(_HttpBase):
    """HTTP with server-sent events: one long stream in, POSTs out."""

    transport = "sse"

    def __init__(self, name: str, spec: Dict[str, Any], startup_timeout_s: float) -> None:
        super().__init__(name, spec, startup_timeout_s)
        self._conn: Optional[http.client.HTTPConnection] = None
        self._box = Mailbox()
        self._post_url: Optional[str] = None
        self._endpoint = threading.Event()

    def _ensure(self) -> None:
        with self._state_lock:
            if self._conn is not None and not self._box.closed:
                return
            self._open()

    def _open(self) -> None:
        self.close()  # a stream that closed leaves its connection behind
        parts = urllib.parse.urlsplit(self._url)
        headers = self._base_headers()
        headers["Accept"] = "text/event-stream"
        headers["Cache-Control"] = "no-cache"
        target = (parts.path or "/") + (("?" + parts.query) if parts.query else "")
        try:
            if parts.scheme == "https":
                conn: http.client.HTTPConnection = http.client.HTTPSConnection(
                    parts.hostname or "", parts.port, timeout=self._startup_timeout, context=ssl.create_default_context()
                )
            else:
                conn = http.client.HTTPConnection(parts.hostname or "", parts.port, timeout=self._startup_timeout)
            conn.request("GET", target, headers=headers)
            response = conn.getresponse()
        except (OSError, http.client.HTTPException) as exc:
            raise self._unreachable(exc, "opening the event stream", self._startup_timeout)
        if response.status != 200:
            detail = response.read(300).decode("utf-8", "replace").strip()
            conn.close()
            message = "MCP server %r answered HTTP %d to the event stream request%s" % (self.name, response.status, (": " + detail) if detail else "")
            raise ToolFailure("ConnectorUnavailable", message, _AUTH_HINT if response.status in (401, 403) else "Check connectors.%s.url (%s)." % (self.name, self._url))
        if conn.sock is not None:
            conn.sock.settimeout(None)  # the stream stays open and idle between messages
        self._conn = conn
        self._box = Mailbox()
        self._post_url = None
        self._endpoint.clear()
        threading.Thread(target=self._read, args=(response, self._box), daemon=True).start()
        if not self._endpoint.wait(self._startup_timeout):
            self.close()
            raise ToolFailure(
                "ConnectorUnavailable",
                "MCP server %r opened an event stream but never said where to send messages (no endpoint event)" % self.name,
                'If the server speaks streamable HTTP, use "type": "mcp-http" for it.',
            )
        try:
            self._handshake()
        except ToolFailure as exc:
            self.close()
            raise ToolFailure("ConnectorUnavailable", "MCP server %r did not complete the handshake: %s" % (self.name, exc.message), exc.hint)

    def _read(self, response: Any, box: Mailbox) -> None:
        def lines() -> Iterator[bytes]:
            while True:
                try:
                    line = response.readline(MAX_MESSAGE_BYTES + 1)
                except (OSError, http.client.HTTPException, ValueError):
                    return
                if not line:
                    return
                yield line

        for event, data in iter_sse(lines()):
            if event == "endpoint":
                self._post_url = urllib.parse.urljoin(self._url, data.strip())
                self._endpoint.set()
                continue
            try:
                message = json.loads(data)
            except ValueError:
                box.note_noise(data.encode("utf-8", "replace"))
                continue
            reply = box.deliver(message)
            if reply is not None:
                try:
                    self._send(reply)
                except ToolFailure:
                    pass
        box.close()

    def _send(self, message: Dict[str, Any]) -> None:
        if not self._post_url:
            raise ToolFailure("ConnectorUnavailable", "MCP server %r is not connected" % self.name)
        headers = self._base_headers()
        headers["Content-Type"] = "application/json"
        request = urllib.request.Request(self._post_url, data=json.dumps(message).encode("utf-8"), headers=headers, method="POST")
        try:
            urllib.request.urlopen(request, timeout=30).close()
        except urllib.error.HTTPError as exc:
            raise self._http_failure(exc, str(message.get("method") or "a reply"))
        except (urllib.error.URLError, OSError) as exc:
            raise self._unreachable(exc, str(message.get("method") or "a reply"), 30)

    def _exchange(self, request: Dict[str, Any], timeout: float, on_progress: Progress) -> Dict[str, Any]:
        box = self._box
        slot = box.expect(request, on_progress)
        try:
            self._send(request)
        except ToolFailure:
            box.forget(request["id"])
            raise
        if not slot["event"].wait(timeout):
            box.forget(request["id"])
            try:
                self._notify("notifications/cancelled", {"requestId": request["id"], "reason": "timeout"})
            except ToolFailure:
                pass
            raise ToolFailure("ToolTimeout", "%s on MCP server %r did not answer within %gs" % (request["method"], self.name, timeout))
        if slot["message"] is None:
            raise ToolFailure(
                "ConnectorUnavailable",
                "the event stream of MCP server %r closed during %s" % (self.name, request["method"]),
                "It is opened again on the next call. If the call changed something, check the target before you repeat it.",
            )
        return slot["message"]

    def _notify(self, method: str, params: Dict[str, Any]) -> None:
        self._send({"jsonrpc": "2.0", "method": method, "params": params})

    def close(self) -> None:
        conn, self._conn = self._conn, None
        self._box.close()
        if conn is not None:
            try:
                if conn.sock is not None:
                    conn.sock.shutdown(socket.SHUT_RDWR)  # wakes the reader thread
            except OSError:
                pass
            try:
                conn.close()
            except OSError:
                pass
