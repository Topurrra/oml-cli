# SPDX-License-Identifier: MIT
"""The executor: where model-written code actually runs.

An executor starts one process that runs one block of code once and then is
thrown away. It keeps no state; the gateway and the log do. The interface is
deliberately tiny (start a process, exchange NDJSON frames, kill it) so that
the sandbox can be anything that can run ``python -c``: a plain subprocess, a
container, a jail, a remote shell.

WHAT THE DEFAULT IS AND IS NOT. The default runs the code as a child process
with a scrubbed environment and an empty working directory. That keeps host
credentials out of the code's environment and keeps the protocol clean. It is
not a security boundary: the child runs as the same OS user and can open
files and sockets. For code you do not trust, set ``executor.command`` to a
container or jail command; nothing else changes.
"""

from __future__ import annotations

import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
from pathlib import Path
from typing import Any, Dict, List, Optional

from .jsonutil import dumps_line
from .procutil import child_options, kill_tree

# The whole bootstrap is one argument with no double quotes, so it survives
# being passed through docker, ssh or a shell wrapper. It reads the real
# runner as the first frame: the runner never has to fit on a command line.
BOOTSTRAP = (
    "import sys,json;"
    "exec(compile(json.loads(sys.stdin.buffer.readline())['runner'],'codemode-runner','exec'))"
)
RUNNER_SOURCE = Path(__file__).with_name("sandbox_runner.py").read_text(encoding="utf-8")

MAX_FRAME_BYTES = 16 * 1024 * 1024
_STDERR_TAIL = 8000

# Variables a process needs to start at all. Everything else the host has
# (API keys, tokens, cloud credentials) stays out of the sandbox.
_SANDBOX_ENV = ("PATH", "SYSTEMROOT", "SYSTEMDRIVE", "WINDIR", "TEMP", "TMP", "TMPDIR", "LANG", "LC_ALL")


class SandboxEOF(Exception):
    """The sandbox closed its output without finishing."""


class SandboxProtocolError(Exception):
    """The sandbox sent something that is not a frame."""


class SandboxProcess:
    """One running sandbox. Frames in and out; nothing else."""

    def __init__(self, popen: subprocess.Popen, workdir: Optional[str]) -> None:
        self._popen = popen
        self._workdir = workdir
        self._frames: "queue.Queue[Any]" = queue.Queue()
        self._outbox: "queue.Queue[Optional[bytes]]" = queue.Queue()
        self._stderr = bytearray()
        self._readers = [
            threading.Thread(target=self._read_frames, daemon=True),
            threading.Thread(target=self._read_stderr, daemon=True),
        ]
        for reader in self._readers:
            reader.start()
        threading.Thread(target=self._write_frames, daemon=True).start()

    def _write_frames(self) -> None:
        """Writes happen here, never on the caller's thread. A sandbox that
        does not read its input fills the pipe; a write on the caller's thread
        would then block past every timeout the pass enforces."""
        stream = self._popen.stdin
        assert stream is not None
        while True:
            data = self._outbox.get()
            if data is None:
                return
            try:
                stream.write(data)
                stream.flush()
            except (OSError, ValueError):
                # The process is gone or closed its input. The reader sees the
                # end of its output and reports what actually happened.
                return

    def _read_frames(self) -> None:
        stream = self._popen.stdout
        assert stream is not None
        while True:
            line = stream.readline(MAX_FRAME_BYTES + 1)
            if not line:
                self._frames.put(SandboxEOF())
                return
            if len(line) > MAX_FRAME_BYTES and not line.endswith(b"\n"):
                self._frames.put(SandboxProtocolError("a frame exceeded %d bytes" % MAX_FRAME_BYTES))
                return
            try:
                frame = json.loads(line)
                if not isinstance(frame, dict):
                    raise ValueError("not an object")
            except ValueError:
                preview = line[:120].decode("ascii", "replace").strip()
                self._frames.put(SandboxProtocolError("expected a JSON frame, got: %s" % preview))
                return
            self._frames.put(frame)

    def _read_stderr(self) -> None:
        stream = self._popen.stderr
        assert stream is not None
        for chunk in iter(lambda: stream.read1(4096), b""):
            self._stderr.extend(chunk)
            del self._stderr[: max(0, len(self._stderr) - 4 * _STDERR_TAIL)]

    def send(self, frame: Dict[str, Any]) -> None:
        """Queue a frame. Never blocks and never raises: if the sandbox is
        gone, the next ``recv`` says so."""
        self._outbox.put(dumps_line(frame))

    def recv(self, timeout: float) -> Dict[str, Any]:
        """The next frame. Raises TimeoutError, SandboxEOF or
        SandboxProtocolError."""
        try:
            item = self._frames.get(timeout=max(0.0, timeout))
        except queue.Empty:
            raise TimeoutError()
        if isinstance(item, Exception):
            raise item
        return item

    def exit_code(self) -> Optional[int]:
        return self._popen.poll()

    def stderr_tail(self) -> str:
        return bytes(self._stderr[-_STDERR_TAIL:]).decode("utf-8", "replace")

    def kill(self) -> None:
        """Stop the process and everything it started. Safe to call twice."""
        popen = self._popen
        self._outbox.put(None)  # stop the writer
        kill_tree(popen)  # the whole tree: code that spawned children must not leave them running
        try:
            popen.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass
        # Let the readers reach end-of-file first, so the stderr tail is
        # complete and no thread is still inside a stream when it is closed.
        for reader in self._readers:
            reader.join(timeout=1.0)
        for stream in (popen.stdin, popen.stdout, popen.stderr):
            try:
                if stream:
                    stream.close()
            except OSError:
                pass
        if self._workdir:
            shutil.rmtree(self._workdir, ignore_errors=True)
            self._workdir = None

    def wait_exit(self, timeout: float) -> None:
        """Give a finished sandbox a moment to exit by itself, which is much
        cheaper than killing a process tree."""
        try:
            self._popen.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            pass


class SubprocessExecutor:
    """Runs the sandbox as a local process, or as whatever ``command`` wraps
    it in. ``{bootstrap}`` in the command is replaced by the bootstrap code
    and ``{python}`` by the host interpreter."""

    def __init__(self, command: Optional[List[str]] = None, env_passthrough: Optional[List[str]] = None) -> None:
        self._command = command
        self._passthrough = list(env_passthrough or [])

    def argv(self) -> List[str]:
        if self._command is None:
            # -I: ignore PYTHON* variables and the user site directory.
            # -u: unbuffered, so frames are never stuck in a buffer.
            return [sys.executable, "-I", "-u", "-c", BOOTSTRAP]
        return [part.replace("{bootstrap}", BOOTSTRAP).replace("{python}", sys.executable) for part in self._command]

    def start(self) -> SandboxProcess:
        """Raises OSError if the command cannot be started."""
        env = {k: os.environ[k] for k in _SANDBOX_ENV + tuple(self._passthrough) if k in os.environ}
        workdir = tempfile.mkdtemp(prefix="codemode-sbx-")
        argv = self.argv()
        argv[0] = shutil.which(argv[0]) or argv[0]
        try:
            popen = subprocess.Popen(
                argv,
                stdin=subprocess.PIPE,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                env=env,
                cwd=workdir,
                **child_options(),
            )
        except OSError:
            shutil.rmtree(workdir, ignore_errors=True)
            raise
        return SandboxProcess(popen, workdir)
