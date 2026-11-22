# SPDX-License-Identifier: MIT
"""Project helpers: Python a project keeps for its own programs.

Agents rebuild the same small functions in program after program: run this
suite and keep the failures, merge that branch and report the conflicts. A
project puts such functions in a folder, names the folder in its config
(``"helpers": "codemode_helpers"``), and every program can call them as
``helpers.<file>.<function>()`` without a fork of the runtime.

Helper code is not trusted more than a program is. Its source is sent to the
sandbox and runs there, next to the program, with the same API: a tool call
made by a helper crosses the same gateway and meets the same policy, limits
and log. The host never executes it; it only reads it, to send it and to
list what is in it for ``search()`` and ``describe()``.

The sources are part of an execution's input. They are recorded with it, so
a resumed or replayed pass runs the helpers the first pass ran, whatever the
files say by then.
"""

from __future__ import annotations

import ast
import re
from pathlib import Path
from typing import Any, Dict, List

from .errors import ConfigError
from .textio import read_text

_MODULE_NAME = re.compile(r"^[a-z][a-z0-9_]*$")
_DOC_CHARS = 300


def load_helpers(folder: Path, max_bytes: int) -> Dict[str, str]:
    """The source of every helper module in ``folder``: ``name.py`` becomes
    ``helpers.name``. Files whose name starts with an underscore are private
    to the project and are skipped. Raises ``ConfigError``."""
    if not folder.is_dir():
        raise ConfigError(
            "the helpers folder %s does not exist" % folder,
            hint='Create it and put .py files in it, or remove "helpers" from the config.',
        )
    sources: Dict[str, str] = {}
    total = 0
    for path in sorted(folder.glob("*.py")):
        name = path.stem
        if name.startswith("_"):
            continue
        if not _MODULE_NAME.match(name):
            raise ConfigError(
                "the helper file %s cannot be used: its name must be lowercase letters, digits and underscores" % path.name,
                hint="Rename it, for example to %s.py." % (re.sub(r"[^a-z0-9_]+", "_", name.lower()).strip("_") or "helper"),
            )
        try:
            sources[name] = read_text(path)
        except (OSError, UnicodeDecodeError) as exc:
            raise ConfigError("the helper file %s cannot be read: %s" % (path, exc))
        total += len(sources[name].encode("utf-8", "replace"))
    if total > max_bytes:
        raise ConfigError(
            "the helpers in %s are %d bytes together; the limit is %d (limits.max_value_bytes)" % (folder, total, max_bytes),
            hint="Helpers are sent to the sandbox with every program. Keep them small, or raise the limit.",
        )
    return sources


def _first_line(text: Any) -> str:
    line = " ".join(str(text or "").strip().split("\n\n")[0].split())
    return line if len(line) <= _DOC_CHARS else line[: _DOC_CHARS - 3] + "..."


def describe_helpers(sources: Dict[str, str]) -> List[Dict[str, Any]]:
    """What is in the helper modules, read without running them: for each
    module its docstring and its public functions with their parameters. A
    file that does not parse is listed with the error, so that ``search()``
    says why a helper is missing instead of not showing it."""
    out: List[Dict[str, Any]] = []
    for name, source in sorted(sources.items()):
        entry: Dict[str, Any] = {"module": name, "doc": "", "functions": []}
        try:
            tree = ast.parse(source, "<helper:%s>" % name)
        except SyntaxError as exc:
            entry["error"] = "SyntaxError: %s (line %s)" % (exc.msg, exc.lineno)
            out.append(entry)
            continue
        entry["doc"] = _first_line(ast.get_docstring(tree))
        for node in tree.body:
            if isinstance(node, ast.FunctionDef) and not node.name.startswith("_"):
                entry["functions"].append({
                    "name": node.name,
                    "signature": "helpers.%s.%s(%s)" % (name, node.name, ast.unparse(node.args)),
                    "doc": _first_line(ast.get_docstring(node)),
                })
        out.append(entry)
    return out
