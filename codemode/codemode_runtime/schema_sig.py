# SPDX-License-Identifier: MIT
"""JSON Schema to a Python-style signature.

Pure logic. A model reads ``demo.read_note(id: int) -> dict`` far faster than
it reads a schema, and it costs a fraction of the tokens. The signature is a
reading aid, not a validator: anything this module cannot type becomes ``Any``
instead of raising, because a tool with an odd schema must stay callable. The
full schema is always available through ``describe()``.
"""

from __future__ import annotations

import keyword
from typing import Any, List, Optional

_PRIMITIVES = {
    "string": "str",
    "integer": "int",
    "number": "float",
    "boolean": "bool",
    "null": "None",
}
MAX_DEPTH = 6  # deeper than this is not worth showing in one line
MAX_LITERALS = 6


def _resolve_ref(ref: str, root: Any) -> Any:
    """Follow a local JSON pointer. Remote refs are not fetched: resolving a
    schema must never make a network call."""
    if not ref.startswith("#/") or not isinstance(root, dict):
        return None
    node = root
    for part in ref[2:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def _literal(values: List[Any]) -> str:
    shown = []
    for value in values[:MAX_LITERALS]:
        if value is None or isinstance(value, (bool, int, float, str)):
            shown.append(repr(value))
        else:
            return "Any"
    if len(values) > MAX_LITERALS:
        shown.append("...")
    return "Literal[%s]" % ", ".join(shown)


def _union(parts: List[str]) -> str:
    seen: List[str] = []
    for part in parts:
        if part not in seen:
            seen.append(part)
    if "Any" in seen:
        return "Any"
    if "None" in seen:  # read as "X or nothing", so None goes last
        seen.remove("None")
        seen.append("None")
    return " | ".join(seen) if seen else "Any"


def type_of(schema: Any, root: Any = None, depth: int = 0, seen: frozenset = frozenset()) -> str:
    """Render one schema node as a type expression."""
    if depth > MAX_DEPTH or not isinstance(schema, dict):
        return "Any"
    root = schema if root is None else root
    ref = schema.get("$ref")
    if ref is not None:
        if not isinstance(ref, str) or ref in seen:
            return "Any"  # a schema that refers to itself must still terminate
        return type_of(_resolve_ref(ref, root), root, depth + 1, seen | {ref})
    if "const" in schema:
        return _literal([schema["const"]])
    enum = schema.get("enum")
    if isinstance(enum, list) and enum:
        return _literal(enum)
    for key in ("anyOf", "oneOf"):
        options = schema.get(key)
        if isinstance(options, list) and options:
            return _union([type_of(option, root, depth + 1, seen) for option in options])
    all_of = schema.get("allOf")
    if isinstance(all_of, list) and all_of:
        if len(all_of) == 1:
            return type_of(all_of[0], root, depth + 1, seen)
        return "dict" if any(isinstance(s, dict) and "properties" in s for s in all_of) else "Any"
    kind = schema.get("type")
    if isinstance(kind, list):
        return _union([_typed(schema, k, root, depth, seen) for k in kind])
    if kind is None:
        if "properties" in schema:
            kind = "object"
        elif "items" in schema:
            kind = "array"
    return _typed(schema, kind, root, depth, seen)


def _typed(schema: dict, kind: Any, root: Any, depth: int, seen: frozenset) -> str:
    if not isinstance(kind, str):
        return "Any"
    if kind == "array":
        return "list[%s]" % type_of(schema.get("items"), root, depth + 1, seen)
    if kind == "object":
        extra = schema.get("additionalProperties")
        if isinstance(extra, dict) and not schema.get("properties"):
            return "dict[str, %s]" % type_of(extra, root, depth + 1, seen)
        return "dict"
    return _PRIMITIVES.get(kind, "Any")


def _is_param_name(name: str) -> bool:
    return name.isidentifier() and not keyword.iskeyword(name)


def _default_text(prop: Any) -> str:
    """Optional parameters show their default when it is a plain scalar."""
    if isinstance(prop, dict) and "default" in prop:
        default = prop["default"]
        if default is None or isinstance(default, (bool, int, float, str)):
            return repr(default)
    return "..."


def signature(
    path: str,
    input_schema: Any,
    output_schema: Any = None,
    max_params: Optional[int] = None,
) -> str:
    """``path(required: T, optional: T = ...) -> R``.

    Required parameters come first. A property whose name cannot be a Python
    parameter is listed in a trailing ``**{...}``: it is passed with
    ``call(path, {"odd-name": value})``.
    """
    returns = type_of(output_schema) if isinstance(output_schema, dict) else "Any"
    if not isinstance(input_schema, dict):
        return "%s(**args: Any) -> %s" % (path, returns)
    props = input_schema.get("properties")
    props = props if isinstance(props, dict) else {}
    required_raw = input_schema.get("required")
    required = [r for r in required_raw if isinstance(r, str)] if isinstance(required_raw, list) else []
    order = [n for n in props if n in required]
    order += [n for n in required if n not in props and n not in order]
    order += [n for n in props if n not in required]
    if not order:
        open_ended = input_schema.get("additionalProperties", True) is not False and "properties" not in input_schema
        return "%s(%s) -> %s" % (path, "**args: Any" if open_ended else "", returns)

    named: List[str] = []
    odd: List[str] = []
    for name in order:
        prop = props.get(name)
        kind = type_of(prop, input_schema, 1) if name in props else "Any"
        if not isinstance(name, str) or not _is_param_name(name):
            odd.append("%r: %s" % (name, kind))
        elif name in required:
            named.append("%s: %s" % (name, kind))
        else:
            named.append("%s: %s = %s" % (name, kind, _default_text(prop)))
    if odd:
        named.append("**{%s}" % ", ".join(odd))
    if max_params is not None and len(named) > max_params:
        named = named[:max_params] + ["..."]
    return "%s(%s) -> %s" % (path, ", ".join(named), returns)
