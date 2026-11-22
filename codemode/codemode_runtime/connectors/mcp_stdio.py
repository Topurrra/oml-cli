# SPDX-License-Identifier: MIT
"""A connector for any MCP server that speaks the stdio transport.

The server is a host-side process: one JSON-RPC message per line on its
stdin and stdout. Its credentials come from the connector's ``env`` in the
config (``${NAME}`` references resolved here) and never reach the sandbox.

What a real server does, and what this client does about it:

- It may be slow to start (a package runner that downloads first): the
  handshake has its own time limit, ``startup_timeout_s``.
- It may exit: the next call starts it again, and the failed call says the
  server exited and shows its stderr.
- It may print things that are not protocol messages to stdout: they are
  skipped, counted, and named in the error if a call then times out.
- It may ignore the end of its input: it is then stopped together with
  everything it started.
"""

from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import threading
from pathlib import Path
from typing import Any, Dict, List, Optional

from ..config import expand_env
from ..errors import CodemodeError, ToolFailure
from ..jsonutil import dumps_line
from ..procutil import child_options, kill_tree
from . import looks_secret
from .mcp_common import MAX_MESSAGE_BYTES, Mailbox, McpConnector, Progress, unwrap_result  # noqa: F401  (unwrap_result: public here since 0.1)

# The server starts with a short allowlist from the host environment plus what
# the config names. Passing the whole environment would hand every secret the
# host has to every server.
BASE_ENV = (
    "PATH", "HOME", "USER", "LOGNAME", "SHELL", "LANG", "LC_ALL", "TERM", "TMPDIR",
    "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "PROGRAMFILES", "SYSTEMROOT",
    "SYSTEMDRIVE", "WINDIR", "COMSPEC", "PATHEXT", "TEMP", "TMP", "USERNAME",
)
_STDERR_TAIL = 4000
_REFERENCE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")


def resolve_env(name: str, spec: Dict[str, Any], section: str = "env") -> "tuple[Dict[str, str], List[str]]":
    """Expand a connector's ``env`` or ``headers``. Returns the values and
    the ones that are credentials: an entry is one if the config lists it
    under ``secrets``, or if it is a ``${NAME}`` reference and its own name or
    the referenced name says so (TOKEN, KEY, SECRET, AUTH, ...)."""
    values: Dict[str, str] = {}
    secrets: List[str] = []
    listed = set(spec.get("secrets") or [])
    try:
        for key, raw in (spec.get(section) or {}).items():
            values[key] = expand_env(raw, "connectors.%s.%s.%s" % (name, section, key))
            referenced = _REFERENCE.findall(raw)
            if key in listed or (referenced and (looks_secret(key) or any(looks_secret(ref) for ref in referenced))):
                secrets.append(values[key])
                # "Bearer <token>": the token alone must go too.
                secrets.extend(part for part in values[key].split() if len(part) >= 12)
    except CodemodeError as exc:
        raise ToolFailure("ConnectorUnavailable", exc.message, exc.hint)
    return values, secrets


class McpStdioConnector(McpConnector):
    transport = "stdio"

    def __init__(self, name: str, spec: Dict[str, Any], startup_timeout_s: float, base_dir: Optional[Path] = None) -> None:
        super().__init__(name, spec, startup_timeout_s)
        self._base_dir = base_dir
        self._proc: Optional[subprocess.Popen] = None
        self._box = Mailbox()
        self._write_lock = threading.Lock()
        self._state_lock = threading.Lock()
        self._stderr = bytearray()
        self.starts = 0

    def _file_stamps(self) -> List[Any]:
        """Every part of the command that is a file here: the server script
        of a project is one of them, and editing it must be noticed."""
        base = Path(self._cwd() or ".")
        stamps: List[Any] = []
        for part in self._spec.get("command", []):
            try:
                path = Path(part) if os.path.isabs(part) else base / part
                if path.is_file():
                    info = path.stat()
                    stamps.append([str(part), info.st_size, info.st_mtime_ns])
            except (OSError, ValueError):
                continue
        return stamps

    # -- process ----------------------------------------------------------

    def _ensure(self) -> None:
        with self._state_lock:
            proc = self._proc
            # The end of its output is the first sign that a server is gone;
            # the process itself may take a moment longer to be reported as
            # exited. Either one means: start it again.
            if proc is not None and proc.poll() is None and not self._box.closed:
                return
            if proc is not None:
                try:
                    proc.wait(timeout=1)
                except (OSError, subprocess.TimeoutExpired):
                    kill_tree(proc)
            self._start()

    def _ensure_after_failure(self) -> None:
        """A write to the server failed, so it is gone whatever ``poll``
        says. Mark the connection dead and connect again."""
        self._box.close()
        self._ensure()

    def _cwd(self) -> Optional[str]:
        """Where the server runs: ``cwd`` from the config, relative to the
        config file's folder, which is also the default. A project's servers
        are written to be started from the project root."""
        cwd = self._spec.get("cwd")
        if self._base_dir is None:
            return cwd
        return str((self._base_dir / cwd) if cwd else self._base_dir)

    def _start(self) -> None:
        command = list(self._spec["command"])
        env = {k: os.environ[k] for k in BASE_ENV if k in os.environ}
        extra, self._secrets = resolve_env(self.name, self._spec)
        env.update(extra)
        cwd = self._cwd()
        # shutil.which honours PATHEXT, so "npx" finds npx.cmd on Windows.
        command[0] = shutil.which(command[0], path=env.get("PATH")) or shutil.which(command[0]) or command[0]
        try:
            self._proc = subprocess.Popen(
                command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, cwd=cwd,
                **child_options(),
            )
        except OSError as exc:
            raise ToolFailure(
                "ConnectorUnavailable",
                "could not start MCP server %r (%s): %s" % (self.name, " ".join(self._spec["command"]), exc),
                "Check connectors.%s.command and cwd (%s); the command must be on PATH or a path that exists." % (self.name, cwd or "."),
            )
        self.starts += 1
        self._stderr = bytearray()
        self._box = Mailbox()
        proc, box = self._proc, self._box
        threading.Thread(target=self._read_stdout, args=(proc, box), daemon=True).start()
        threading.Thread(target=self._read_stderr, args=(proc,), daemon=True).start()
        try:
            self._handshake()
        except ToolFailure as exc:
            note = self._stderr_note() + box.noise_note()
            self.close()
            hint = "Run the command by hand to see what it prints."
            if exc.kind == "ToolTimeout":
                hint = (
                    "The server did not answer the handshake in %gs. If it is slow to start (a package runner that "
                    "downloads first), raise startup_timeout_s on this connector." % self._startup_timeout
                )
            raise ToolFailure(
                "ConnectorUnavailable", "MCP server %r did not complete the handshake: %s%s" % (self.name, exc.message, note), hint
            )

    def _stderr_note(self) -> str:
        tail = bytes(self._stderr[-_STDERR_TAIL:]).decode("utf-8", "replace").strip()
        return ("; server stderr: " + tail) if tail else ""

    def _read_stderr(self, proc: subprocess.Popen) -> None:
        for chunk in iter(lambda: proc.stderr.read1(4096), b""):  # type: ignore[union-attr]
            self._stderr.extend(chunk)
            del self._stderr[: max(0, len(self._stderr) - 4 * _STDERR_TAIL)]

    def _read_stdout(self, proc: subprocess.Popen, box: Mailbox) -> None:
        stream = proc.stdout
        assert stream is not None
        while True:
            line = stream.readline(MAX_MESSAGE_BYTES + 1)
            if not line:
                break
            if not line.strip():
                continue
            try:
                message = json.loads(line)
            except ValueError:
                box.note_noise(line)  # a server that logs to stdout must not take the client down
                continue
            reply = box.deliver(message)
            if reply is not None:
                try:
                    self._send(reply, proc)
                except ToolFailure:
                    pass
        box.close()  # end of output: the server is gone

    def _send(self, message: Dict[str, Any], proc: Optional[subprocess.Popen] = None) -> None:
        proc = proc or self._proc
        if proc is None or proc.stdin is None:
            raise ToolFailure("ConnectorUnavailable", "MCP server %r is not running" % self.name)
        try:
            with self._write_lock:
                proc.stdin.write(dumps_line(message))
                proc.stdin.flush()
        except (OSError, ValueError):
            raise ToolFailure(
                "ConnectorUnavailable", "MCP server %r closed its input%s" % (self.name, self._stderr_note())
            )

    # -- the transport ----------------------------------------------------

    def _exchange(self, request: Dict[str, Any], timeout: float, on_progress: Progress) -> Dict[str, Any]:
        method = request["method"]
        for attempt in (1, 2):
            box = self._box
            slot = box.expect(request, on_progress)
            try:
                self._send(request)
                break
            except ToolFailure:
                box.forget(request["id"])
                if attempt == 2 or method == "initialize":
                    raise
                # The request never reached the server: it died between two
                # calls. Nothing ran, so start it again and send once more.
                self._ensure_after_failure()
        if not slot["event"].wait(timeout):
            box.forget(request["id"])
            try:
                self._notify("notifications/cancelled", {"requestId": request["id"], "reason": "timeout"})
            except ToolFailure:
                pass
            raise ToolFailure(
                "ToolTimeout", "%s on MCP server %r did not answer within %gs%s" % (method, self.name, timeout, box.noise_note())
            )
        message = slot["message"]
        if message is None:
            raise ToolFailure(
                "ConnectorUnavailable",
                "MCP server %r exited during %s%s%s" % (self.name, method, self._stderr_note(), box.noise_note()),
                "The server process is gone; it is started again on the next call. If the call changed something, "
                "check the target before you repeat it.",
            )
        return message

    def _notify(self, method: str, params: Dict[str, Any]) -> None:
        self._send({"jsonrpc": "2.0", "method": method, "params": params})

    def close(self) -> None:
        proc, self._proc = self._proc, None
        if proc is None:
            return
        try:
            if proc.stdin:
                proc.stdin.close()  # the polite way to stop a stdio server
            proc.wait(timeout=2)
        except (OSError, subprocess.TimeoutExpired):
            # It ignored the end of its input. Take everything it started
            # with it: a wrapper script would otherwise leave the real
            # server running.
            kill_tree(proc)
            try:
                proc.wait(timeout=5)
            except (OSError, subprocess.TimeoutExpired):
                pass
        if proc.poll() is not None:
            # Only once it has exited: the reader threads are then at the end
            # of their streams, and closing cannot wait on a read in progress.
            for stream in (proc.stdout, proc.stderr):
                try:
                    if stream:
                        stream.close()
                except (OSError, ValueError):
                    pass
