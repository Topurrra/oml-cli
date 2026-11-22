# SPDX-License-Identifier: MIT
"""``python -m codemode_runtime``, and the file a detached worker is started
from. Run as a plain script it has no package, so it puts the project folder
on ``sys.path`` itself: that also works under ``python -I``."""

import os
import sys

if __package__ in (None, ""):
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from codemode_runtime.cli import main  # noqa: E402  (after the path fix on purpose)

if __name__ == "__main__":
    sys.exit(main())
