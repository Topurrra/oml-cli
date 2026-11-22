# SPDX-License-Identifier: MIT
"""How this process was started, so a hint can print a command that works.

A hint that says ``python main.py approve <id>`` is wrong three ways on a real
machine: the interpreter may not be called ``python``, the script is not in
the current directory, and the ``--config`` and ``--home`` the execution was
run with are missing, so the command would look in another data directory.
Every hint therefore goes through ``command()``, which repeats the launcher
and the global options of the current process.
"""

from __future__ import annotations

import os
import sys
from typing import List, Optional

_PREFIX: Optional[List[str]] = None
_GLOBAL: List[str] = []


def _portable(path: str) -> str:
    """Forward slashes: Python and every Windows shell accept them, and a
    POSIX-style shell on Windows would eat the backslashes of a pasted path."""
    return path.replace("\\", "/") if os.sep == "\\" else path


def _quote(part: str) -> str:
    if part and all(c.isalnum() or c in "-_./:=" for c in part):
        return part
    return '"' + part.replace('"', '\\"') + '"'


def launcher() -> List[str]:
    """The argv prefix that starts this program again."""
    global _PREFIX
    if _PREFIX is None:
        script = sys.argv[0] if sys.argv and sys.argv[0] else ""
        base = os.path.basename(script).lower()
        if base in ("codemode", "codemode.exe", "codemode-script.py", "codemode.cmd"):
            _PREFIX = ["codemode"]  # the installed entry point, found on PATH
        elif base == "__main__.py" or not script or script == "-c":
            _PREFIX = [_portable(sys.executable), "-m", "codemode_runtime"]
        else:
            _PREFIX = [_portable(sys.executable), _portable(os.path.abspath(script))]
    return list(_PREFIX)


def set_global_options(config: Optional[str], home: Optional[str]) -> None:
    """Remember the ``--config`` and ``--home`` this process was given.
    Paths are made absolute: the human may paste the hint in another folder."""
    del _GLOBAL[:]
    if config:
        _GLOBAL.extend(["--config", _portable(os.path.abspath(config))])
    if home:
        _GLOBAL.extend(["--home", _portable(os.path.abspath(home))])


def argv(*args: str) -> List[str]:
    return launcher() + list(_GLOBAL) + list(args)


def command(*args: str) -> str:
    """A command line a human can paste: launcher, global options, ``args``."""
    return " ".join(_quote(part) for part in argv(*args))


def program_name() -> str:
    """Short name for ``--help``."""
    parts = launcher()
    if parts == ["codemode"]:
        return "codemode"
    if parts[1:2] == ["-m"]:
        return "python -m codemode_runtime"
    return "python %s" % os.path.basename(parts[-1])
