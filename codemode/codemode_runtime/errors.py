# SPDX-License-Identifier: MIT
"""Typed errors and the CLI exit-code contract.

Agents branch on exit codes and on ``error.type``, so both are fixed here in
one place instead of being decided where an error happens to be raised.
"""

from __future__ import annotations

EXIT_OK = 0  # completed
EXIT_FAILED = 1  # the execution ran and failed, or was rejected
EXIT_USAGE = 2  # bad arguments, bad config, or a command that does not apply
EXIT_PAUSED = 3  # waiting for a human approval
EXIT_VERIFY_MISMATCH = 4  # replay did not reproduce the recorded result
EXIT_NOT_FOUND = 5  # unknown execution, snippet or tool
EXIT_RUNNING = 6  # wait: the time given ran out and the execution is still running


class CodemodeError(Exception):
    """Base class. ``hint`` says what to do next, not what went wrong."""

    exit_code = EXIT_FAILED
    kind = "Error"

    def __init__(self, message: str, hint: str = "") -> None:
        super().__init__(message)
        self.message = message
        self.hint = hint

    def to_dict(self) -> dict:
        out = {"type": self.kind, "message": self.message}
        if self.hint:
            out["hint"] = self.hint
        return out


class ConfigError(CodemodeError):
    exit_code = EXIT_USAGE
    kind = "ConfigError"


class UsageError(CodemodeError):
    exit_code = EXIT_USAGE
    kind = "UsageError"


class StateError(CodemodeError):
    """The command is valid but does not apply to this execution's status."""

    exit_code = EXIT_USAGE
    kind = "StateError"


class TerminalRequired(CodemodeError):
    """An approval was attempted from a process with no terminal. Approving
    is for a person; an agent's shell tool has no terminal on stdin."""

    exit_code = EXIT_USAGE
    kind = "TerminalRequired"


class NotFoundError(CodemodeError):
    exit_code = EXIT_NOT_FOUND
    kind = "NotFound"


class BusyError(CodemodeError):
    """Another process is running a pass of the same execution."""

    exit_code = EXIT_FAILED
    kind = "Busy"


class ModelEndpointError(CodemodeError):
    kind = "ModelEndpointError"


class ToolFailure(Exception):
    """A tool call failed. Raised by connectors and by the gateway.

    It is recorded in the execution log and re-raised inside the sandbox as
    ``ToolError(kind, message)``, so the program can catch it. It is not a
    ``CodemodeError`` because it never ends the CLI by itself.
    """

    def __init__(self, kind: str, message: str, hint: str = "") -> None:
        super().__init__("%s: %s" % (kind, message))
        self.kind = kind
        self.message = message
        self.hint = hint

    def to_dict(self) -> dict:
        out = {"type": self.kind, "message": self.message}
        if self.hint:
            out["hint"] = self.hint
        return out
