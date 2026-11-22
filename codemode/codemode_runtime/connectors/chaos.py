# SPDX-License-Identifier: MIT
"""The chaos connector: tools that misbehave on purpose.

A test fixture, not a product feature. The end-to-end suite uses it to stall a
call, fail a call, return too much, and count how many times a side effect
really ran. That last one is what proves replay does not repeat work: the
counter is a file, so it is visible from outside the process.
"""

from __future__ import annotations

import threading
import time
from pathlib import Path
from typing import Any, Dict, List

from ..errors import ToolFailure
from . import CallContext, Connector

_TOOLS: List[Dict[str, Any]] = [
    {
        "name": "echo",
        "description": "Return the value it was given.",
        "inputSchema": {"type": "object", "properties": {"value": {}}, "required": ["value"]},
    },
    {
        "name": "dash-echo",
        "description": "Return the value it was given. The dash in the name is on purpose: programs call it dash_echo.",
        "inputSchema": {"type": "object", "properties": {"value": {}}, "required": ["value"]},
    },
    {
        "name": "sleep",
        "description": "Wait for a number of seconds, then return it.",
        "inputSchema": {"type": "object", "properties": {"seconds": {"type": "number"}}, "required": ["seconds"]},
    },
    {
        "name": "fail",
        "description": "Always fail with the given message.",
        "inputSchema": {"type": "object", "properties": {"message": {"type": "string"}}},
    },
    {
        "name": "big",
        "description": "Return a string of the given length.",
        "inputSchema": {"type": "object", "properties": {"chars": {"type": "integer"}}, "required": ["chars"]},
    },
    {
        "name": "flaky",
        "description": "Count calls under a name; every N-th call fails, the others return {ok, n}. A flaky test in miniature.",
        "inputSchema": {
            "type": "object",
            "properties": {"name": {"type": "string"}, "every": {"type": "integer", "default": 3}},
            "required": ["name"],
        },
    },
    {
        "name": "counter",
        "description": "Add one to a named counter kept in a file and return the new value. A real side effect.",
        "inputSchema": {"type": "object", "properties": {"name": {"type": "string", "default": "default"}}},
    },
]


class ChaosConnector(Connector):
    def __init__(self, name: str, home: Path) -> None:
        self.name = name
        self._dir = Path(home) / "chaos"
        self._lock = threading.Lock()

    def list_tools(self) -> List[Dict[str, Any]]:
        return _TOOLS

    def call(self, tool: str, args: Dict[str, Any], ctx: CallContext) -> Any:
        if tool in ("echo", "dash-echo"):
            return args.get("value")
        if tool == "sleep":
            seconds = float(args.get("seconds", 0))
            time.sleep(seconds)
            return seconds
        if tool == "fail":
            raise ToolFailure("ChaosFailure", str(args.get("message", "failed on purpose")))
        if tool == "big":
            return "x" * int(args.get("chars", 0))
        if tool in ("counter", "flaky"):
            name = str(args.get("name", "default"))
            if not name.isidentifier():
                raise ToolFailure("BadArguments", "counter name must be an identifier")
            with self._lock:
                self._dir.mkdir(parents=True, exist_ok=True)
                path = self._dir / ("counter-%s.txt" % name)
                value = int(path.read_text()) + 1 if path.is_file() else 1
                path.write_text(str(value))
            if tool == "flaky":
                if value % max(1, int(args.get("every", 3))) == 0:
                    raise ToolFailure("ChaosFailure", "call %d of %s failed on purpose" % (value, name))
                return {"ok": True, "n": value}
            return value
        raise ToolFailure("UnknownTool", "the chaos connector has no tool %r" % tool)
