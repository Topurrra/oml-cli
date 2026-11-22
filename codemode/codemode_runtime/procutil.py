# SPDX-License-Identifier: MIT
"""Processes, on every platform the runtime supports.

Three things differ between Windows and POSIX and are easy to get subtly
wrong: stopping a process together with everything it started, asking
whether a process is still running, and starting one that outlives its
parent. They are written once, here.
"""

from __future__ import annotations

import os
import signal
import subprocess
from typing import Any, Dict, List, Optional

IS_WINDOWS = os.name == "nt"

_CREATE_NEW_PROCESS_GROUP = 0x00000200
_CREATE_NO_WINDOW = 0x08000000
_CREATE_BREAKAWAY_FROM_JOB = 0x01000000


def child_options() -> Dict[str, Any]:
    """``Popen`` keyword arguments for a child this process owns.

    POSIX: its own session, so ``kill_tree`` can signal the whole group and a
    Ctrl-C in the terminal does not reach it behind our back. Windows: no
    console window. A host without a console (a detached worker, a GUI app)
    would otherwise open a visible one for every console child it starts.
    """
    if IS_WINDOWS:
        return {"creationflags": _CREATE_NO_WINDOW}
    return {"start_new_session": True}


def kill_tree(popen: subprocess.Popen) -> None:
    """Stop a child started with ``child_options`` and everything under it.
    Safe to call on a process that has already exited."""
    if popen.poll() is not None:
        return
    try:
        if IS_WINDOWS:
            # /T takes the whole tree: a command that spawned children must
            # not leave them running after a timeout.
            subprocess.run(
                ["taskkill", "/F", "/T", "/PID", str(popen.pid)],
                stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10, creationflags=_CREATE_NO_WINDOW,
            )
        else:
            os.killpg(popen.pid, signal.SIGKILL)
    except (OSError, subprocess.SubprocessError):
        pass
    try:
        popen.kill()
    except OSError:
        pass


def pid_alive(pid: Any) -> bool:
    """Is a process with this id running? A wrong "yes" (the id was reused)
    is possible and harmless here: callers treat "alive" as "do not touch"."""
    try:
        pid = int(pid)
    except (TypeError, ValueError):
        return False
    if pid <= 0:
        return False
    if IS_WINDOWS:
        # os.kill(pid, 0) is not a probe on Windows: it terminates the process.
        import ctypes

        kernel32 = ctypes.windll.kernel32  # type: ignore[attr-defined]
        handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
        if not handle:
            return kernel32.GetLastError() == 5  # access denied: it exists, it is just not ours
        try:
            code = ctypes.c_ulong()
            if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
                return True
            return code.value == 259  # STILL_ACTIVE
        finally:
            kernel32.CloseHandle(handle)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    except OSError:
        return False
    return not _is_zombie(pid)


def _is_zombie(pid: int) -> bool:
    """A process that has exited and has not been collected by its parent
    still has an id, and a signal to it still succeeds. Where the system
    says so (Linux), such a process counts as gone: in a container with no
    init process it can stay that way for as long as the container runs."""
    try:
        with open("/proc/%d/stat" % pid, "rb") as handle:
            stat = handle.read()
    except OSError:
        return False  # no /proc here, or the process is gone: nothing more to learn
    # "pid (name) state ...": the name may hold spaces and brackets, so the
    # state is what follows the last closing bracket.
    return stat.rpartition(b")")[2].split()[:1] == [b"Z"]


def spawn_detached(argv: List[str], log_path: Optional[str] = None, cwd: Optional[str] = None) -> int:
    """Start a process that keeps running after this one exits, with no
    console and no inherited pipes. Returns its pid. Raises OSError.

    Its stdout and stderr go to ``log_path``: a worker that dies at startup
    must leave a reason somewhere.
    """
    sink = open(log_path, "ab") if log_path else open(os.devnull, "wb")
    try:
        options: Dict[str, Any] = dict(stdin=subprocess.DEVNULL, stdout=sink, stderr=sink, cwd=cwd, close_fds=True)
        if not IS_WINDOWS:
            return subprocess.Popen(argv, start_new_session=True, **options).pid
        flags = _CREATE_NO_WINDOW | _CREATE_NEW_PROCESS_GROUP
        try:
            # Leave the job object of the launching shell if it lets us:
            # a shell that kills its job on exit would take the worker along.
            return subprocess.Popen(argv, creationflags=flags | _CREATE_BREAKAWAY_FROM_JOB, **options).pid
        except OSError:
            return subprocess.Popen(argv, creationflags=flags, **options).pid
    finally:
        sink.close()
