# SPDX-License-Identifier: MIT
"""Canonical JSON, hashes and wire lines.

Replay compares what a resumed pass asks for with what the log holds, so two
equal values must always serialize to the same bytes: sorted keys, compact
separators, ASCII only, and no NaN (which is not JSON and compares unequal to
itself). ASCII also means the bytes are the same under any console or file
encoding.
"""

from __future__ import annotations

import hashlib
import json
from typing import Any, Tuple


def canonical(value: Any) -> str:
    """Serialize deterministically. Raises ValueError on NaN or infinity and
    TypeError on values JSON cannot hold."""
    return json.dumps(
        value, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False
    )


def sha256_hex(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8", "surrogatepass")).hexdigest()


def fingerprint(kind: str, name: str, args: Any) -> str:
    """Identity of one host interaction: what kind, which name, which input."""
    return sha256_hex(canonical([kind, name, args]))


def byte_len(value: Any) -> int:
    """Size of a value as it is stored and sent (canonical JSON is ASCII, so
    characters are bytes)."""
    return len(canonical(value))


def lookup(value: Any, dotted: str) -> Tuple[bool, Any]:
    """Follow ``a.b.0`` into nested dicts and lists. Returns (found, value).
    A list takes an integer index; a negative one counts from the end."""
    node = value
    for part in dotted.split("."):
        if isinstance(node, dict):
            if part not in node:
                return False, None
            node = node[part]
        elif isinstance(node, list):
            try:
                index = int(part)
            except ValueError:
                return False, None
            if not -len(node) <= index < len(node):
                return False, None
            node = node[index]
        else:
            return False, None
    return True, node


def dumps_line(obj: Any) -> bytes:
    """One NDJSON frame."""
    text = json.dumps(obj, separators=(",", ":"), ensure_ascii=True, allow_nan=False)
    return (text + "\n").encode("ascii")


def pretty(value: Any) -> str:
    """Human-facing JSON. Still ASCII: a Windows console in a legacy code page
    must not be able to make printing a result fail."""
    return json.dumps(value, indent=2, ensure_ascii=True, sort_keys=False)
