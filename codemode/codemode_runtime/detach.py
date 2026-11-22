# SPDX-License-Identifier: MIT
"""Running an execution in a process that outlives its caller.

A program that takes minutes cannot be run in the foreground by a caller
whose shell gives a command two minutes, or by an MCP client whose tool call
has a time limit. Detached, the caller gets the execution id at once and
asks again later.

The worker is this same program, started with a hidden command
(``_worker ID``), with no console and no pipes to its parent. It loads the
same config, takes the execution's lock and runs its first pass. Everything
else (progress, waiting, cancelling, the outcome) goes through the execution
log, which any process can read.
"""

from __future__ import annotations

import os
import sys
import time
from typing import Any, Dict

from . import procutil
from .errors import UsageError
from .runtime import Runtime

_SETTLE_S = 5.0


def spawn_worker(runtime: Runtime, exec_id: str) -> Dict[str, Any]:
    """Start a worker for an execution that ``Runtime.start`` recorded.
    Returns the outcome so far, which is normally "running"."""
    package = os.path.dirname(os.path.abspath(__file__))
    argv = [sys.executable, os.path.join(package, "__main__.py")]
    if runtime.config.path is not None:
        argv += ["--config", str(runtime.config.path)]
    argv += ["--home", os.path.abspath(str(runtime.home)), "--agent=" + runtime.agent, "_worker", exec_id]
    try:
        # In the project root when the config was found from a folder below
        # it: the worker is given the config by name, and reads relative
        # paths from its own directory.
        root = str(runtime.config.root) if runtime.config.root is not None else None
        pid = procutil.spawn_detached(argv, str(runtime.store.worker_log(exec_id)), cwd=root)
    except OSError as exc:
        runtime.reject(exec_id, by="runtime", reason="the worker process could not be started: %s" % exc)
        raise UsageError("could not start a detached worker: %s" % exc, hint="Run the program in the foreground instead.")
    # Come back once the worker has the execution (or has already finished
    # it), so that "running" in the answer means a process is running it.
    deadline = time.monotonic() + _SETTLE_S
    while time.monotonic() < deadline:
        payload = runtime.get(exec_id)
        if payload.get("status") != "running" or payload.get("progress", {}).get("phase") == "running":
            return payload
        if not procutil.pid_alive(pid):
            # The worker exited without ever taking the execution. Say so
            # now, with what it printed, instead of leaving it "starting".
            return runtime.get(exec_id, worker_gone=True)
        time.sleep(0.05)
    return runtime.get(exec_id)
