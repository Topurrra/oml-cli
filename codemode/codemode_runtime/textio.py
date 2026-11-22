# SPDX-License-Identifier: MIT
"""Reading text files that other tools wrote.

Pure logic plus one read. A config or a program is often written by an editor
or a shell that adds a byte-order mark: Notepad writes UTF-8 with a BOM, and
Windows PowerShell 5 redirects as UTF-16. Reading such a file as plain UTF-8
fails with an error that says nothing about the cause, so the encoding is
taken from the BOM when there is one.
"""

from __future__ import annotations

import codecs
from pathlib import Path
from typing import Union

_BOMS = (
    (codecs.BOM_UTF8, "utf-8-sig"),
    (codecs.BOM_UTF32_LE, "utf-32"),  # before UTF-16: its BOM starts with the UTF-16 LE one
    (codecs.BOM_UTF32_BE, "utf-32"),
    (codecs.BOM_UTF16_LE, "utf-16"),
    (codecs.BOM_UTF16_BE, "utf-16"),
)


def decode_text(data: bytes) -> str:
    """Decode by BOM, else as UTF-8, with every line end as ``\\n``. Raises
    UnicodeDecodeError.

    Line ends are normalized because the text is source or JSON, where they
    carry no meaning, and because a program is recorded with its execution:
    the same file checked out with CRLF on one machine and LF on another
    must be the same program.
    """
    text = None
    for bom, encoding in _BOMS:
        if data.startswith(bom):
            text = data.decode(encoding)
            break
    if text is None:
        text = data.decode("utf-8")
    return text.replace("\r\n", "\n").replace("\r", "\n")


def read_text(path: Union[str, Path]) -> str:
    """Raises OSError or UnicodeDecodeError."""
    return decode_text(Path(path).read_bytes())
