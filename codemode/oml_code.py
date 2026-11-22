#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""oml's entry point to the vendored code-mode runtime. `oml code <command>` starts it as

    python -I <oml>/codemode/oml_code.py --config <project>/.oml/codemode/config.json --home <project>/.oml/codemode <command> ...

and passes stdin, stdout, stderr and the exit code through. Nothing here changes how the runtime decides, runs or
records a call. Most of what is oml's goes through the runtime's own settings:

- hints print `oml code ...` (the full path of the oml.exe that started it): CODEMODE_COMMAND, set below;
- programs go in `.oml/code/`: `work_dir` in the config oml writes;
- `oml code init` writes `.oml/codemode.json`: init's `out` and `work_dir`.

Three names are still wrapped: the tool description and the instructions (the oml section, with the server's own
tool descriptions), init (the project root, and a server called oml is left out), and the terminal check's hint
(a person's settings go in .oml/codemode.json, not in the generated config).

The runtime itself (codemode_runtime/) is a copy of the author's standalone code-mode project.
"""

import os
import sys
from pathlib import Path

if sys.version_info < (3, 9):
    sys.stderr.write("oml code needs Python 3.9 or newer; this is %s\n" % sys.version.split()[0])
    sys.exit(2)

_HERE = os.path.dirname(os.path.abspath(__file__))
if _HERE not in sys.path:
    sys.path.insert(0, _HERE)  # python -I leaves the script's folder off sys.path

from codemode_runtime import cli, errors, instructions as _instructions, project, runtime  # noqa: E402
import oml_guide  # noqa: E402

OWN_CONFIG = ".oml/codemode.json"
WORK_DIR = ".oml/code"


def _patch():
    oml = os.environ.get("OML_EXE")
    if oml:
        # `oml code` passes --config and --home itself, so a hint is the oml command and the subcommand. Set in the
        # environment, so a detached worker prints the same hints.
        path = oml.replace("\\", "/")
        os.environ["CODEMODE_COMMAND"] = ('"%s"' % path if " " in path else path) + " code"

    require = cli._require_terminal

    def require_terminal(rt, what, give_user):
        try:
            require(rt, what, give_user)
        except errors.TerminalRequired as refusal:
            # oml writes the runtime's config; the person's own settings go in .oml/codemode.json.
            refusal.hint = (refusal.hint or "").replace("in the config.", "in %s." % OWN_CONFIG)
            raise

    cli._require_terminal = require_terminal

    describe = runtime.Runtime.tool_description

    def tool_description(self):
        text = describe(self)
        if "oml" in self.config.connectors:
            text += "\n\n" + oml_guide.prompt(_oml_tools(self))
        return text

    runtime.Runtime.tool_description = tool_description

    block = _instructions.instructions

    def instructions(*args, **kwargs):
        return oml_guide.instructions(block(*args, **kwargs))

    _instructions.instructions = instructions

    init = project.init

    def init_for_oml(folder, source=None, force=False, home=None, out=None, work_dir=None):
        folder = os.environ.get("OML_CODE_PROJECT") or str(folder)  # the project root, wherever oml was started
        report = init(Path(folder), source=source, force=force, home=home,
                      out=out or os.path.join(folder, OWN_CONFIG), work_dir=work_dir or WORK_DIR)
        # oml finds .oml/codemode.json and sets work_dir itself: neither needs saying in the file or the notes.
        if not work_dir:
            oml_guide.drop_key(report["config"], "work_dir")
        report["notes"] = [n for n in report.get("notes", []) if not n.startswith("The config is not where the runtime looks")]
        oml_guide.drop_own_connector(report["config"], report)
        return report

    project.init = init_for_oml


def _oml_tools(rt):
    """The oml tools as the server describes them: [(name, description, policy)], or [] when it is not reachable."""
    try:
        hits = rt.search("", 50, "oml")  # names and policies; descriptions there are cut to one line
        return [(h["path"].partition(".")[2], rt.describe(h["path"]).get("description") or h.get("description") or "", h.get("policy"))
                for h in hits]
    except Exception:  # the prompt still helps without the server's own words
        return []


if __name__ == "__main__":
    _patch()
    sys.exit(cli.main())
