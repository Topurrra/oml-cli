# SPDX-License-Identifier: MIT
"""codemode-runtime: a portable code-mode gateway.

One capability for any agent: ``execute(code)``. The program runs in a
separate process; every tool call it makes crosses a host-side gateway that
applies policy, limits and an audit log. See ``docs/DESIGN.md`` for why each
part exists.
"""

__version__ = "0.3.0"
