#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""oml's entry point to the vendored code-mode runtime. `oml code <command>` starts it as

    python -I <oml>/codemode/oml_code.py --config <project>/.oml/codemode/config.json --home <project>/.oml/codemode <command> ...

and passes stdin, stdout, stderr and the exit code through. Nothing here changes how the runtime decides, runs or
records a call. It only adds what is oml's:

- hints print `oml code ...` (the full path of the oml.exe that started it), not `python main.py --config ... --home ...`;
- `oml code prompt`, the `execute` tool of `oml code serve` and `oml code instructions` describe the oml connector;
- `oml code init` writes `.oml/codemode.json` (the project's additions: other MCP servers, rules, limits), not
  `codemode.json`, and adds `.oml/codemode/` and `.oml/code/` to .gitignore.

The runtime itself (codemode_runtime/) is a copy of the standalone project; see VENDORED.md.
"""

import io
import os
import sys
from pathlib import Path

if sys.version_info < (3, 9):
    sys.stderr.write("oml code needs Python 3.9 or newer; this is %s\n" % sys.version.split()[0])
    sys.exit(2)

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)  # python -I leaves the script's folder off sys.path

from codemode_runtime import cli, errors, instructions as _instructions, launcher, project, runtime  # noqa: E402
import oml_guide  # noqa: E402


def _portable(path):
    return path.replace("\\", "/")


def _patch():
    oml = os.environ.get("OML_EXE")
    if oml:
        # `oml code` passes --config and --home itself, so a hint is the oml command and the subcommand.
        launcher._PREFIX = [_portable(oml), "code"]
        launcher.set_global_options = lambda config, home: None
        launcher.program_name = lambda: "oml code"

    # approve and run --interactive need a person at a terminal. On Windows isatty() is also true for the NUL device,
    # which is what an agent's shell tool often gives a command as stdin (`< /dev/null` in Git Bash): ask Windows
    # whether stdin is a console instead.
    require = cli._require_terminal

    def require_terminal(rt, what, give_user):
        if rt.config.approval["require_terminal"] and not _console_stdin():
            stdin, sys.stdin = sys.stdin, io.StringIO()  # not a terminal: the runtime refuses with its own message
            try:
                require(rt, what, give_user)
            except errors.TerminalRequired as refusal:
                # oml writes the runtime's config; the person's own settings go in .oml/codemode.json.
                refusal.hint = (refusal.hint or "").replace("in the config.", "in .oml/codemode.json.")
                raise
            finally:
                sys.stdin = stdin
        require(rt, what, give_user)

    cli._require_terminal = require_terminal

    describe = runtime.Runtime.tool_description

    def tool_description(self):
        text = describe(self)
        if "oml" in self.config.connectors:
            text += "\n\n" + oml_guide.prompt(_oml_tools(self))
        return text

    runtime.Runtime.tool_description = tool_description

    block = _instructions.instructions

    def instructions(command="codemode", surface="cli"):
        return oml_guide.instructions(block(command, surface), surface)

    _instructions.instructions = instructions

    # oml code init: the project's additions go to .oml/codemode.json, which oml merges over its own config.
    project.CONFIG_NAME = ".oml/codemode.json"
    project.IGNORE_ENTRIES = [".oml/codemode/", ".oml/code/"]
    init = project.init

    def init_for_oml(folder, source=None, force=False, home=None):
        folder = os.environ.get("OML_CODE_PROJECT") or str(folder)  # the project root, wherever oml was started
        os.makedirs(os.path.join(folder, ".oml"), exist_ok=True)
        report = init(Path(folder), source=source, force=force, home=home)
        oml_guide.drop_own_connector(report["config"], report)
        return report

    project.init = init_for_oml


def _console_stdin():
    """Is stdin a console a person types in? On Windows: GetConsoleMode succeeds on it (it fails on NUL, a pipe, a file)."""
    try:
        if os.name != "nt":
            return sys.stdin.isatty()
        import ctypes
        import msvcrt

        handle = msvcrt.get_osfhandle(sys.stdin.fileno())
        mode = ctypes.c_uint32()
        return bool(ctypes.windll.kernel32.GetConsoleMode(ctypes.c_void_p(handle), ctypes.byref(mode)))
    except (AttributeError, OSError, ValueError):
        return False


def _oml_tools(rt):
    """The oml tools as the server describes them: [(name, description, policy)], or [] when it is not reachable."""
    try:
        rt.catalog.namespaces()
        entries = rt.catalog._entries.get("oml") or {}
        policy = rt.config.policy
        return [(e.name, e.description or "", policy.decide(e.path, None, e.original_path).action) for e in entries.values()]
    except Exception:  # the prompt still helps without the server's own words
        return []


if __name__ == "__main__":
    _patch()
    sys.exit(cli.main())
