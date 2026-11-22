# SPDX-License-Identifier: MIT
"""oml commands from a program, with the output already parsed: oml = helpers.oml; oml.read("inspect", "A.oml").

These run in the sandbox like the program itself: every call goes through the policy and the log."""

import json


def J(value):
    """JSON text as a Python value; anything else (a dict, a list, plain text) as it is."""
    if isinstance(value, str):
        try:
            return json.loads(value)
        except ValueError:
            return value
    return value


def out(result):
    """The parsed output of one oml tool result {exit, output, stderr}. Raises ToolError when the command failed."""
    result = J(result)
    if not isinstance(result, dict) or "exit" not in result:
        return result
    value = J(result.get("output"))
    if result["exit"] != 0 and (not isinstance(value, dict) or "error" in value):
        error = value.get("error") if isinstance(value, dict) else value
        hint = value.get("hint", "") if isinstance(value, dict) else ""
        message = str(error or result.get("stderr") or "no output")[:1000]
        raise ToolError("OmlFailed", "oml exited %s: %s" % (result["exit"], message), str(hint))
    return value


def read(*args):
    """A command that only reads, such as read("inspect", "A.oml", "--own"). Returns its parsed output."""
    return out(tools.oml.read(args=[str(a) for a in args]))


def write(*args):
    """A command that writes a new file: new, apply, migrate, memory add. Returns its parsed output."""
    return out(tools.oml.write(args=[str(a) for a in args]))


def gated(*args):
    """install, publish, references, pull, lifetime, server: pauses the program until the user approves."""
    return out(tools.oml.gated(args=[str(a) for a in args]))


def many(calls):
    """Independent commands in parallel: many([("read", ["inspect", "A.oml"]), ("write", ["new", ...])]).
    Returns their parsed outputs in order."""
    results = parallel([("oml." + tool, {"args": [str(a) for a in args]}) for tool, args in calls])
    return [out(r) for r in results]
