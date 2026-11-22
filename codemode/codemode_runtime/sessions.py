# SPDX-License-Identifier: MIT
"""Session state and snippets.

``state`` is how a program keeps what it read or computed for the next
program, instead of fetching it again or carrying it through the model's
context. The sandbox process is thrown away after every pass, so state is
explicit and JSON: that is what lets a pass be replayed, and lets the sandbox
live in a container.

A snippet is code promoted for reuse. Promotion is a host command; no sandbox
function writes here.
"""

from __future__ import annotations

import json
import os
import re
import time
from pathlib import Path
from typing import Any, Dict, List

from . import launcher
from .errors import BusyError, NotFoundError, UsageError
from .records import file_lock

_SESSION_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$")
_SNIPPET_NAME = re.compile(r"^[a-z][a-z0-9_]{0,63}$")


def _write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_name("%s.%d.tmp" % (path.name, os.getpid()))  # one per process: two writers never share it
    tmp.write_text(json.dumps(value, ensure_ascii=True, indent=1), encoding="ascii")
    for attempt in range(20):
        try:
            os.replace(tmp, path)  # atomic: a reader sees the old file or the new one
            return
        except PermissionError:
            # Windows refuses to replace a file that another process has
            # open, for as long as it has it open: a reader, for a moment.
            if attempt == 19:
                raise
            time.sleep(0.01 * (attempt + 1))


def check_session_name(name: str) -> str:
    if not isinstance(name, str) or not _SESSION_NAME.match(name):
        raise UsageError(
            "session name %r is not valid" % (name,),
            hint="Use letters, digits, dot, dash or underscore, at most 64 characters.",
        )
    return name


class SessionStore:
    def __init__(self, home: Path) -> None:
        self._dir = Path(home) / "sessions"

    def _path(self, name: str) -> Path:
        return self._dir / (check_session_name(name) + ".json")

    def load(self, name: str) -> Dict[str, Any]:
        path = self._path(name)
        if not path.is_file():
            return {}
        return json.loads(path.read_text(encoding="ascii"))

    def apply_delta(self, name: str, set_keys: Dict[str, Any], delete_keys: List[str]) -> None:
        """Merge one execution's changes into the session.

        A delta, not a whole-state overwrite: an execution that was paused for
        an hour must not erase what other executions stored meanwhile.
        """
        if not set_keys and not delete_keys:
            return
        path = self._path(name)
        path.parent.mkdir(parents=True, exist_ok=True)
        # Read, change, write under a lock: two programs that finish at the
        # same moment in one session must not lose each other's keys.
        with file_lock(path.with_name(path.name + ".lock"), 15.0, lambda owner: BusyError(
            "the state of session %r is being written by another process (pid %s)" % (name, owner or "?"),
            hint="Run the program again. Agents that work in parallel are better off with a session each (--session NAME).",
        )):
            state = self.load(name)
            for key in delete_keys:
                state.pop(key, None)
            state.update(set_keys)
            _write_json(path, state)


class SnippetStore:
    def __init__(self, home: Path) -> None:
        self._dir = Path(home) / "snippets"

    def _path(self, name: str) -> Path:
        if not isinstance(name, str) or not _SNIPPET_NAME.match(name):
            raise UsageError(
                "snippet name %r is not valid" % (name,),
                hint="Use lowercase letters, digits and underscores, starting with a letter.",
            )
        return self._dir / (name + ".json")

    def save(self, name: str, code: str, description: str, source_execution: str) -> Dict[str, Any]:
        snippet = {
            "name": name,
            "description": description,
            "code": code,
            "sourceExecution": source_execution,
            "savedAt": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        }
        _write_json(self._path(name), snippet)
        return snippet

    def get(self, name: str) -> Dict[str, Any]:
        path = self._path(name)
        if not path.is_file():
            raise NotFoundError("no snippet %r" % name, hint="List them with: %s" % launcher.command("snippet", "list"))
        return json.loads(path.read_text(encoding="ascii"))

    def list(self) -> List[Dict[str, Any]]:
        if not self._dir.is_dir():
            return []
        return [json.loads(p.read_text(encoding="ascii")) for p in sorted(self._dir.glob("*.json"))]

    def delete(self, name: str) -> None:
        path = self._path(name)
        if not path.is_file():
            raise NotFoundError("no snippet %r" % name)
        path.unlink()
