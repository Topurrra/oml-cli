#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Write the generated parts of the docs.

    python tests/sync_docs.py

``docs/ADOPTING.md`` carries the instructions for agents that
``codemode instructions`` prints. The text lives in
``codemode_runtime/instructions.py``; this script copies it between the
markers in the guide, and a unit case fails when the two differ.
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT))

from codemode_runtime.instructions import instructions  # noqa: E402

GUIDE = ROOT / "docs" / "ADOPTING.md"
START, END = "<!-- instructions:start -->", "<!-- instructions:end -->"


def block() -> str:
    """The instructions as they appear in the guide: fenced as Markdown
    text, so that the guide shows them instead of rendering them."""
    return "%s\n````markdown\n%s````\n%s" % (START, instructions("codemode", "cli"), END)


def current() -> str:
    text = GUIDE.read_text(encoding="utf-8")
    return text[text.index(START): text.index(END) + len(END)]


def main() -> int:
    text = GUIDE.read_text(encoding="utf-8")
    head, tail = text[: text.index(START)], text[text.index(END) + len(END):]
    with open(GUIDE, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(head + block() + tail)
    print("wrote the instructions block into %s" % os.path.relpath(GUIDE, ROOT))
    return 0


if __name__ == "__main__":
    sys.exit(main())
