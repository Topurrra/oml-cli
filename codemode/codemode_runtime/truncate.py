# SPDX-License-Identifier: MIT
"""Output budgets.

Pure logic. The point of code mode is that only what the program chooses to
return reaches the model, so the runtime caps what comes back and always says
when it cut something and by how much. A silent cut would let a model reason
over half a value as if it were whole.
"""

from __future__ import annotations

import json
from typing import Any, List, Tuple


def _marker(dropped: int) -> str:
    return "\n[... %d chars dropped ...]\n" % dropped


def truncate_text(text: str, budget: int, keep: str = "head") -> Tuple[str, int]:
    """Cut ``text`` to about ``budget`` characters. Returns (text, dropped).

    ``keep="head"`` keeps the start; ``keep="both"`` keeps a quarter from the
    start and the rest from the end, because the end of a log is usually where
    the failure is. The marker is added on top of the budget, so the output is
    at most ``budget`` plus one short marker.
    """
    budget = max(0, int(budget))
    if len(text) <= budget:
        return text, 0
    dropped = len(text) - budget
    if keep == "both":
        head = budget // 4
        tail = budget - head
        return text[:head] + _marker(dropped) + (text[len(text) - tail :] if tail else ""), dropped
    return text[:budget] + _marker(dropped), dropped


def budget_result(value: Any, budget: int) -> Tuple[Any, bool, int]:
    """Return (shown, truncated, total_chars) for a result value.

    Under budget the value passes through untouched. Over budget the caller
    gets a string preview of the JSON text, flagged as truncated: never a
    partial structure that still looks like the value.
    """
    text = json.dumps(value, ensure_ascii=True)
    if len(text) <= max(0, int(budget)):
        return value, False, len(text)
    preview, _ = truncate_text(text, budget, keep="head")
    return preview, True, len(text)


def shape_of(value: Any, depth: int = 3, keys: int = 12) -> Any:
    """The structure of a value without its content: keys, types and sizes.

    The same function the sandbox offers as ``shape()``. The host uses it to
    say what is in a value it will not print in full: a result over the
    budget, the variables of a program that failed.
    """
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, int):
        return "int"
    if isinstance(value, float):
        return "float"
    if isinstance(value, str):
        return "str[%d]" % len(value)
    if isinstance(value, dict):
        if depth <= 0:
            return "dict[%d keys]" % len(value)
        out = {}
        for name in list(value)[: max(1, int(keys))]:
            out[str(name)] = shape_of(value[name], depth - 1, keys)
        if len(value) > len(out):
            out["..."] = "+%d keys" % (len(value) - len(out))
        return out
    if isinstance(value, (list, tuple)):
        if not value:
            return "list[0]"
        if depth <= 0:
            return "list[%d]" % len(value)
        return {"list": len(value), "of": shape_of(value[0], depth - 1, keys)}
    return type(value).__name__


class BoundedLog:
    """Collects log text in bounded memory: the first ``head`` characters and
    the last ``tail`` characters, with a count of what fell out between."""

    def __init__(self, budget: int) -> None:
        budget = max(0, int(budget))
        self._head_cap = budget // 4
        self._tail_cap = budget - self._head_cap
        self._head: List[str] = []
        self._head_len = 0
        self._tail: List[str] = []
        self._tail_len = 0
        self.total = 0

    def add(self, text: str) -> None:
        self.total += len(text)
        room = self._head_cap - self._head_len
        if room > 0:
            self._head.append(text[:room])
            self._head_len += len(text[:room])
            text = text[room:]
        if not text or self._tail_cap <= 0:
            return
        self._tail.append(text)
        self._tail_len += len(text)
        # Drop whole chunks from the left, then trim the first one, so a flood
        # of small writes cannot grow the list without bound.
        while self._tail and self._tail_len - len(self._tail[0]) >= self._tail_cap:
            self._tail_len -= len(self._tail.pop(0))
        if self._tail_len > self._tail_cap:
            excess = self._tail_len - self._tail_cap
            self._tail[0] = self._tail[0][excess:]
            self._tail_len -= excess

    @property
    def dropped(self) -> int:
        return self.total - self._head_len - self._tail_len

    def render(self) -> str:
        head = "".join(self._head)
        tail = "".join(self._tail)
        return head + (_marker(self.dropped) if self.dropped else "") + tail
