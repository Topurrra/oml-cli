# SPDX-License-Identifier: MIT
"""The ``commands`` connector: a project's own command lines as tools.

Many projects have no MCP server. Their work is a build, a test runner, a
linter, version control: commands. This connector turns the commands a
project declares in its config into tools, so that such a project can adopt
code mode without writing a server first::

    "ci": {"type": "commands", "tools": {
        "test": {"description": "Run the unit tests.",
                 "command": ["pytest", "-q", "{path}", {"if": "only", "then": ["-k", "{only}"]}],
                 "params": {"path": {"type": "string", "default": "tests"}, "only": {"type": "string"}},
                 "timeout_s": 900}}}

Only declared commands run, and only with declared parameters: there is no
tool here that takes an arbitrary command line. A command is started
directly, never through a shell, so a parameter cannot add a second command.
Each tool is its own name for the policy (``ci.test``).

Every tool returns the same structured result (``RESULT_SCHEMA``): an ``ok``
flag, the exit code, how long it took, and the output, cut to a budget that
keeps both ends, with honest sizes and the path of a file that holds all of
it. That shape is the advice in ``docs/SERVER_AUTHORS.md``, applied.
"""

from __future__ import annotations

import locale
import os
import re
import shutil
import subprocess
import threading
import time
from fnmatch import fnmatchcase
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from ..errors import ConfigError, ToolFailure
from ..procutil import child_options, kill_tree
from . import CallContext, Connector
from .mcp_stdio import check_inherit_env, host_env, resolve_env

TOOL_KEYS = {"description", "command", "params", "env", "allow_env", "timeout_s", "cwd", "ok_exit_codes"}
PARAM_KEYS = {"type", "description", "default", "enum"}
PARAM_TYPES = ("string", "integer", "number", "boolean", "array")
DEFAULT_OUTPUT_CHARS = 20000
_PLACEHOLDER = re.compile(r"\{\{|\}\}|\{([A-Za-z_][A-Za-z0-9_]*)\}")

RESULT_SCHEMA: Dict[str, Any] = {
    "type": "object",
    "properties": {
        "ok": {"type": "boolean", "description": "True when the command exited with an expected code and was not stopped."},
        "exit": {"type": ["integer", "null"], "description": "The exit code; null when the command was stopped at its time limit."},
        "timed_out": {"type": "boolean"},
        "seconds": {"type": "number"},
        "stdout": {"type": "string", "description": "Standard output, cut in the middle when it is over the budget."},
        "stderr": {"type": "string"},
        "stdout_chars": {"type": "integer", "description": "The size of the whole standard output."},
        "stderr_chars": {"type": "integer"},
        "truncated": {"type": "boolean", "description": "True when stdout or stderr was cut."},
        "output_file": {"type": ["string", "null"], "description": "A file on the host with the whole output, when it was cut."},
    },
    "required": ["ok", "exit", "timed_out", "seconds", "stdout", "stderr", "truncated"],
}


# -- validation and expansion: pure -------------------------------------------


def _fail(where: str, message: str, hint: str = "") -> ConfigError:
    return ConfigError("%s %s" % (where, message), hint=hint)


def _references(element: str) -> List[str]:
    return [m.group(1) for m in _PLACEHOLDER.finditer(element) if m.group(1)]


def validate_spec(name: str, spec: Dict[str, Any]) -> None:
    """Check a ``commands`` connector at config load. A command that names a
    parameter nobody declared must be an error now, not a broken command
    line at three in the morning."""
    where = "connectors.%s" % name
    tools = spec.get("tools")
    if not isinstance(tools, dict) or not tools:
        raise _fail(where + ".tools", "must be a non-empty object of tools", 'Example: {"test": {"command": ["pytest", "-q"]}}')
    own_env = spec.get("env", {})
    if not isinstance(own_env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in own_env.items()):
        raise _fail(where + ".env", "must map strings to strings")
    check_inherit_env(spec.get("inherit_env", []), where + ".inherit_env")
    limit = spec.get("max_output_chars", DEFAULT_OUTPUT_CHARS)
    if isinstance(limit, bool) or not isinstance(limit, int) or limit < 200:
        raise _fail(where + ".max_output_chars", "must be a whole number, 200 or more; got %r" % (limit,))
    if "cwd" in spec and not isinstance(spec["cwd"], str):
        raise _fail(where + ".cwd", "must be a string")
    for tool, raw in tools.items():
        at = "%s.tools.%s" % (where, tool)
        if not isinstance(tool, str) or not tool or not isinstance(raw, dict):
            raise _fail(at, "must be an object")
        unknown = sorted(set(raw) - TOOL_KEYS)
        if unknown:
            raise _fail(at, "has unknown key(s): %s" % ", ".join(unknown), "Valid keys: %s" % ", ".join(sorted(TOOL_KEYS)))
        params = raw.get("params", {})
        if not isinstance(params, dict):
            raise _fail(at + ".params", "must be an object")
        for param, schema in params.items():
            if not isinstance(param, str) or not param.isidentifier() or param == "env":
                raise _fail(at + ".params", "has a parameter name that is not usable: %r" % (param,), "Use a plain identifier; env is taken.")
            if not isinstance(schema, dict) or set(schema) - PARAM_KEYS:
                raise _fail("%s.params.%s" % (at, param), "must be an object with keys from: %s" % ", ".join(sorted(PARAM_KEYS)))
            if schema.get("type", "string") not in PARAM_TYPES:
                raise _fail("%s.params.%s.type" % (at, param), "must be one of %s" % ", ".join(PARAM_TYPES))
            if "enum" in schema and (not isinstance(schema["enum"], list) or not schema["enum"]):
                raise _fail("%s.params.%s.enum" % (at, param), "must be a non-empty list")
            if "default" in schema:
                problem = _check_value(schema, schema["default"])
                if problem:
                    raise _fail("%s.params.%s.default" % (at, param), problem)
        command = raw.get("command")
        if not isinstance(command, list) or not command or not isinstance(command[0], str) or not command[0]:
            raise _fail(at + ".command", "must be a list that starts with the program to run", 'Example: ["git", "status", "--short"]')
        if _references(command[0]):
            # Which program runs is the project's decision, made here. A
            # caller that could choose it would have a tool for any command.
            raise _fail(at + ".command", "starts with a parameter; the program to run must be written out, not passed by the caller")
        for element in command:
            parts = [element]
            if isinstance(element, dict):
                if set(element) != {"if", "then"} or element["if"] not in params or not isinstance(element["then"], list) \
                        or not all(isinstance(p, str) for p in element["then"]):
                    raise _fail(at + ".command", "has a conditional part that is not {\"if\": PARAMETER, \"then\": [strings]}: %r" % (element,))
                parts = element["then"]
            for part in parts:
                if not isinstance(part, str):
                    raise _fail(at + ".command", "must hold strings and conditional parts only; got %r" % (part,))
                for reference in _references(part):
                    if reference not in params:
                        raise _fail(at + ".command", "uses {%s}, which is not declared under params" % reference)
        tool_env = raw.get("env", {})
        if not isinstance(tool_env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in tool_env.items()):
            raise _fail(at + ".env", "must map strings to strings")
        allow = raw.get("allow_env", [])
        if not isinstance(allow, list) or not all(isinstance(v, str) and v for v in allow):
            raise _fail(at + ".allow_env", "must be a list of variable names the caller may set (globs are allowed)")
        if "timeout_s" in raw and (isinstance(raw["timeout_s"], bool) or not isinstance(raw["timeout_s"], (int, float)) or raw["timeout_s"] <= 0):
            raise _fail(at + ".timeout_s", "must be a positive number")
        codes = raw.get("ok_exit_codes", [0])
        if not isinstance(codes, list) or not codes or not all(isinstance(c, int) and not isinstance(c, bool) for c in codes):
            raise _fail(at + ".ok_exit_codes", "must be a non-empty list of whole numbers")
        if "cwd" in raw and not isinstance(raw["cwd"], str):
            raise _fail(at + ".cwd", "must be a string")


def _required(params: Dict[str, Any], name: str, command: List[Any]) -> bool:
    """A parameter is required when it has no default and the command uses
    it outside a conditional part that tests it."""
    if "default" in params.get(name, {}):
        return False
    for element in command:
        if isinstance(element, str) and name in _references(element):
            return True
        if isinstance(element, dict) and element.get("if") != name and any(name in _references(p) for p in element.get("then", [])):
            return True
    return False


def required_params(tool: Dict[str, Any]) -> List[str]:
    params = tool.get("params", {})
    return [name for name in params if _required(params, name, tool["command"])]


def _check_value(schema: Dict[str, Any], value: Any) -> str:
    """Why ``value`` does not fit a parameter, or an empty string."""
    kind = schema.get("type", "string")
    ok = {
        "string": isinstance(value, str),
        "integer": isinstance(value, int) and not isinstance(value, bool),
        "number": isinstance(value, (int, float)) and not isinstance(value, bool),
        "boolean": isinstance(value, bool),
        "array": isinstance(value, list) and all(isinstance(v, (str, int, float)) and not isinstance(v, bool) for v in value),
    }[kind]
    if not ok:
        return "must be %s, got %s" % ("a list of strings or numbers" if kind == "array" else kind, type(value).__name__)
    if "enum" in schema and value not in schema["enum"]:
        return "must be one of %s, got %r" % (", ".join(repr(v) for v in schema["enum"]), value)
    return ""


def _text(value: Any) -> str:
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def expand_command(tool: Dict[str, Any], args: Dict[str, Any]) -> List[str]:
    """The argument list for one call. Pure.

    ``{name}`` inside an element is replaced by the parameter's value. An
    element that is exactly ``{name}`` for a list parameter becomes one
    argument per item. ``{"if": name, "then": [...]}`` adds its elements when
    the parameter was given a truthy value. ``{{`` and ``}}`` are literal
    braces. No shell reads the result: a value with spaces, quotes or ``;``
    is one argument and nothing more.
    """
    params = tool.get("params", {})
    values = {name: schema["default"] for name, schema in params.items() if "default" in schema}
    values.update(args)

    def fill(element: str) -> List[str]:
        whole = _PLACEHOLDER.fullmatch(element)
        if whole and whole.group(1) and isinstance(values.get(whole.group(1)), list):
            return [_text(item) for item in values[whole.group(1)]]

        def replace(match: "re.Match[str]") -> str:
            if match.group(0) == "{{":
                return "{"
            if match.group(0) == "}}":
                return "}"
            value = values.get(match.group(1), "")
            return " ".join(_text(item) for item in value) if isinstance(value, list) else _text(value)

        return [_PLACEHOLDER.sub(replace, element)]

    argv: List[str] = []
    for element in tool["command"]:
        if isinstance(element, dict):
            if values.get(element["if"]) not in (None, False, "", [], 0):
                for part in element["then"]:
                    argv.extend(fill(part))
        else:
            argv.extend(fill(element))
    return argv


def check_args(tool_name: str, tool: Dict[str, Any], args: Dict[str, Any]) -> Tuple[Dict[str, Any], Dict[str, str]]:
    """Validate the arguments of a call. Returns (parameters, environment
    the caller asked for). Raises ``ToolFailure("BadArguments")``."""
    params = tool.get("params", {})
    allowed = tool.get("allow_env", [])
    values = dict(args)
    env = values.pop("env", None)
    unknown = sorted(set(values) - set(params))
    if unknown:
        raise ToolFailure(
            "BadArguments", "%s does not take %s" % (tool_name, ", ".join(unknown)),
            "Its parameters: %s." % (", ".join(sorted(params) + (["env"] if allowed else [])) or "none"),
        )
    missing = [name for name in required_params(tool) if name not in values]
    if missing:
        raise ToolFailure("BadArguments", "%s needs %s" % (tool_name, ", ".join(missing)))
    for name, value in values.items():
        problem = _check_value(params[name], value)
        if problem:
            raise ToolFailure("BadArguments", "%s: %s %s" % (tool_name, name, problem))
    extra: Dict[str, str] = {}
    if env is not None:
        if not isinstance(env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in env.items()):
            raise ToolFailure("BadArguments", "%s: env must map variable names to strings" % tool_name)
        refused = sorted(k for k in env if not any(fnmatchcase(k, pattern) for pattern in allowed))
        if refused:
            raise ToolFailure(
                "BadArguments", "%s does not let the caller set %s" % (tool_name, ", ".join(refused)),
                "The project decides which variables a caller may set: allow_env on the tool in the config (now: %s)."
                % (", ".join(allowed) or "none"),
            )
        extra = dict(env)
    return values, extra


def decode_output(data: bytes) -> str:
    """Output of a command as text with ``\\n`` line ends. UTF-8 when it is
    valid UTF-8; otherwise the console encoding of this machine, replacing
    what cannot be decoded: a compiler that prints in a legacy code page must
    not cost the program its output."""
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        text = data.decode(locale.getpreferredencoding(False) or "latin-1", "replace")
    return text.replace("\r\n", "\n").replace("\r", "\n")


def cut_middle(text: str, budget: int) -> Tuple[str, bool]:
    """Keep the first quarter and the rest from the end, with a marker that
    says how much is gone. The end of a build log is where the failure is."""
    if len(text) <= budget:
        return text, False
    head = budget // 4
    tail = budget - head
    return "%s\n[... %d chars not shown; the whole output is in output_file ...]\n%s" % (text[:head], len(text) - budget, text[len(text) - tail:]), True


# -- the connector --------------------------------------------------------------


class CommandsConnector(Connector):
    def __init__(self, name: str, spec: Dict[str, Any], home: Path, base_dir: Optional[Path] = None) -> None:
        self.name = name
        self._spec = spec
        self._home = Path(home)
        self._base_dir = base_dir
        self._running: Dict[int, subprocess.Popen] = {}
        self._lock = threading.Lock()
        self._secrets: List[str] = []

    def list_tools(self) -> List[Dict[str, Any]]:
        out = []
        for tool_name, tool in self._spec["tools"].items():
            properties = {name: dict(schema, type=schema.get("type", "string")) for name, schema in tool.get("params", {}).items()}
            for schema in properties.values():
                if schema["type"] == "array":
                    schema["items"] = {"type": "string"}
            if tool.get("allow_env"):
                properties["env"] = {
                    "type": "object", "additionalProperties": {"type": "string"},
                    "description": "Environment variables for this run. Allowed names: %s." % ", ".join(tool["allow_env"]),
                }
            shown = " ".join(part if isinstance(part, str) else "[%s]" % " ".join(part["then"]) for part in tool["command"])
            out.append({
                "name": tool_name,
                "description": ("%s Runs: %s" % (tool.get("description", "").strip(), shown)).strip(),
                "inputSchema": {"type": "object", "properties": properties, "required": required_params(tool), "additionalProperties": False},
                "outputSchema": RESULT_SCHEMA,
            })
        return out

    def _folder(self, tool: Dict[str, Any]) -> Optional[str]:
        folder = self._base_dir
        for part in (self._spec.get("cwd"), tool.get("cwd")):
            if part:
                folder = (folder / part) if folder is not None else Path(part)
        return str(folder) if folder is not None else None

    def _environment(self, tool: Dict[str, Any], extra: Dict[str, str]) -> Dict[str, str]:
        env, inherited = host_env(self._spec.get("inherit_env"))
        own, secrets = resolve_env(self.name, self._spec)
        theirs, more = resolve_env(self.name, tool)
        self._secrets = secrets + more + inherited
        env.update(own)
        env.update(theirs)
        env.update(extra)
        return env

    def call(self, tool: str, args: Dict[str, Any], ctx: CallContext) -> Any:
        spec = self._spec["tools"].get(tool)
        if spec is None:
            raise ToolFailure("UnknownTool", "the %s connector has no tool %r" % (self.name, tool))
        path = "%s.%s" % (self.name, tool)
        values, extra = check_args(path, spec, args)
        argv = expand_command(spec, values)
        env = self._environment(spec, extra)
        cwd = self._folder(spec)
        program = shutil.which(argv[0], path=env.get("PATH")) or shutil.which(argv[0])
        if program is None and not os.path.isfile(os.path.join(cwd or ".", argv[0])):
            raise ToolFailure(
                "CommandNotFound", "%s: the program %r was not found" % (path, argv[0]),
                "Put it on PATH, or give its full path in the config. A program that needs more of the host "
                "environment than PATH gets it through inherit_env on the connector.",
            )
        argv[0] = program or argv[0]
        limit = min(float(spec.get("timeout_s") or ctx.timeout_s), max(0.5, ctx.timeout_s - 0.4))
        folder = self._home / "outputs"
        sink_path = folder / ("%s-%d.txt" % (ctx.execution_id, ctx.seq))
        try:
            folder.mkdir(parents=True, exist_ok=True)
            sink = open(sink_path, "wb")
        except OSError:
            sink = None  # the output is then only what fits in the result
        started = time.monotonic()
        try:
            proc = subprocess.Popen(
                argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env, cwd=cwd, **child_options()
            )
        except OSError as exc:
            if sink is not None:
                sink.close()
                _remove(sink_path)
            hint = "Check the command and cwd (%s) in the config." % (cwd or ".")
            if getattr(exc, "winerror", None) == 206 or exc.errno == 7:  # the command line is too long
                hint = "The command line is too long for this system. Pass long lists through a file the command reads."
            raise ToolFailure("CommandFailedToStart", "%s: could not start %s: %s" % (path, argv[0], exc), hint)
        with self._lock:
            self._running[ctx.seq] = proc
        captured = {"stdout": _Capture(), "stderr": _Capture()}
        write_lock = threading.Lock()
        readers = [
            threading.Thread(target=_pump, args=(proc.stdout, captured["stdout"], sink, write_lock), daemon=True),
            threading.Thread(target=_pump, args=(proc.stderr, captured["stderr"], sink, write_lock), daemon=True),
        ]
        for reader in readers:
            reader.start()
        timed_out = False
        try:
            deadline = started + limit
            reported = started
            while True:
                try:
                    proc.wait(timeout=0.2)
                    break
                except subprocess.TimeoutExpired:
                    pass
                now = time.monotonic()
                if now >= deadline:
                    timed_out = True
                    kill_tree(proc)  # the command and everything it started
                    break
                if ctx.progress is not None and now - reported >= 2.0:
                    reported = now
                    last = captured["stdout"].last_line() or captured["stderr"].last_line()
                    ctx.progress("%ds: %s" % (now - started, last[:200]) if last else "%ds, no output yet" % (now - started))
            try:
                proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                pass
            for reader in readers:
                reader.join(timeout=5)
        finally:
            with self._lock:
                self._running.pop(ctx.seq, None)
            if sink is not None:
                sink.close()
        budget = int(self._spec.get("max_output_chars", DEFAULT_OUTPUT_CHARS))
        whole_out, whole_err = decode_output(captured["stdout"].data()), decode_output(captured["stderr"].data())
        stdout, cut_out = cut_middle(whole_out, budget)
        stderr, cut_err = cut_middle(whole_err, budget)
        truncated = cut_out or cut_err or captured["stdout"].lost or captured["stderr"].lost
        if not truncated or sink is None:
            _remove(sink_path)
        exit_code = None if timed_out else proc.returncode
        return {
            "ok": (not timed_out) and exit_code in spec.get("ok_exit_codes", [0]),
            "exit": exit_code,
            "timed_out": timed_out,
            "seconds": round(time.monotonic() - started, 2),
            "stdout": stdout,
            "stderr": stderr,
            # In characters of the text as returned (line ends normalized),
            # plus whatever was too much to hold in memory at all.
            "stdout_chars": len(whole_out) + captured["stdout"].lost,
            "stderr_chars": len(whole_err) + captured["stderr"].lost,
            "truncated": bool(truncated),
            "output_file": str(sink_path) if (truncated and sink is not None) else None,
        }

    def cancel(self, ctx: CallContext) -> None:
        with self._lock:
            proc = self._running.get(ctx.seq)
        if proc is not None:
            kill_tree(proc)

    def secrets(self) -> List[str]:
        return list(self._secrets)

    def close(self) -> None:
        with self._lock:
            running = list(self._running.values())
        for proc in running:
            kill_tree(proc)


class _Capture:
    """The first and the last megabyte of a stream, and its full size.
    Memory stays bounded however much a command prints."""

    _KEEP = 1_000_000

    def __init__(self, keep: Optional[int] = None) -> None:
        self._keep = self._KEEP if keep is None else keep
        self._head = bytearray()
        self._tail = bytearray()
        self.total = 0
        self.lost = 0  # bytes that are in neither end

    def add(self, chunk: bytes) -> None:
        self.total += len(chunk)
        room = self._keep - len(self._head)
        if room > 0:
            self._head.extend(chunk[:room])
            chunk = chunk[room:]
        if chunk:
            self._tail.extend(chunk)
            excess = len(self._tail) - self._keep
            if excess > 0:
                self.lost += excess
                del self._tail[:excess]

    def data(self) -> bytes:
        return bytes(self._head) + bytes(self._tail)

    def last_line(self) -> str:
        tail = bytes(self._tail[-2000:] or self._head[-2000:])
        lines = [line.strip() for line in decode_output(tail).splitlines() if line.strip()]
        return lines[-1] if lines else ""


def _pump(stream: Any, capture: _Capture, sink: Any, lock: threading.Lock) -> None:
    try:
        for chunk in iter(lambda: stream.read1(65536), b""):
            capture.add(chunk)
            if sink is not None:
                with lock:
                    try:
                        sink.write(chunk)
                    except (OSError, ValueError):
                        pass
    except (OSError, ValueError):
        pass


def _remove(path: Path) -> None:
    try:
        path.unlink()
    except OSError:
        pass
