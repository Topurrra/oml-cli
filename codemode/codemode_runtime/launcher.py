# SPDX-License-Identifier: MIT
"""How this process was started, so a hint can print a command that works.

A hint that says ``python main.py approve <id>`` is wrong three ways on a real
machine: the interpreter may not be called ``python``, the script is not in
the current directory, and the ``--config`` and ``--home`` the execution was
run with are missing, so the command would look in another data directory.
Every hint therefore goes through ``command()``, which repeats the launcher
and the global options of the current process.

A project may start the runtime through a command of its own: a wrapper
script, a task runner, a tool that ships a copy. Then the interpreter and
the script are a command nobody types, and only the project knows the real
one. It says so in ``CODEMODE_COMMAND``, and hints print that instead.
"""

from __future__ import annotations

import os
import re
import sys
from typing import List, Optional

COMMAND_VARIABLE = "CODEMODE_COMMAND"
_OPTIONS = re.compile(r"\s*\{options\}")

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


def own_command() -> Optional[str]:
    """The project's own command line for this runtime, from the
    environment, or None. ``{options}`` in it stands for ``--config`` and
    ``--home``: a command that takes them says where they go, and one that
    chooses the config and the data folder itself leaves it out, so that
    nothing is added to it."""
    text = " ".join((os.environ.get(COMMAND_VARIABLE) or "").split())
    if not text:
        return None
    options = " ".join(_quote(part) for part in _GLOBAL)
    return _OPTIONS.sub(lambda match: (" " + options) if options else "", text).strip()


def chooses_config_itself() -> bool:
    """Is the runtime started through a command of the project's own that
    picks the config and the data folder by itself? That is what
    ``CODEMODE_COMMAND`` with no ``{options}`` in it says. A person then has
    no ``--config`` to remember, and must not be told to pass one."""
    text = (os.environ.get(COMMAND_VARIABLE) or "").strip()
    return bool(text) and not _OPTIONS.search(text)


def is_own_command(program: str, args: List[str]) -> bool:
    """Does this command line start the runtime through the project's own
    command? ``mytool code serve`` does when ``CODEMODE_COMMAND`` is
    ``mytool code``, wherever ``mytool`` is installed and whatever its
    extension. Used to recognise the runtime's own server in a server list,
    under whatever name the project gave the runtime."""
    text = _OPTIONS.sub("", " ".join((os.environ.get(COMMAND_VARIABLE) or "").split())).strip()
    if not text:
        return False
    # Not shlex in POSIX mode: it would eat the backslashes of a Windows path.
    words = [word.strip('"') for word in re.findall(r'"[^"]*"|\S+', text)]

    def name(word: str) -> str:
        return os.path.splitext(word.replace("\\", "/").rsplit("/", 1)[-1])[0].lower()

    return name(str(program)) == name(words[0]) and [str(a) for a in args[: len(words) - 1]] == words[1:]


def command(*args: str) -> str:
    """A command line a human can paste: launcher, global options, ``args``."""
    own = own_command()
    if own is not None:
        return " ".join([own] + [_quote(part) for part in args])
    return " ".join(_quote(part) for part in argv(*args))


def program_name() -> str:
    """Short name for ``--help``."""
    if (os.environ.get(COMMAND_VARIABLE) or "").strip():
        return _OPTIONS.sub("", " ".join(os.environ[COMMAND_VARIABLE].split())).strip()
    parts = launcher()
    if parts == ["codemode"]:
        return "codemode"
    if parts[1:2] == ["-m"]:
        return "python -m codemode_runtime"
    return "python %s" % os.path.basename(parts[-1])
