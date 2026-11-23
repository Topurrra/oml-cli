# SPDX-License-Identifier: MIT
"""The sandbox side: the API a model's program sees.

This file is not imported by the host. Its source is sent to the sandbox
process as the first frame and executed there, so it must stand alone: the
standard library only, and syntax that old interpreters accept, because the
sandbox may be a different Python than the host (a container image, say).

Protocol: newline-delimited JSON. The program's requests (``call``,
``search``, ``describe``, ``step``, ``step_set``, ``snippet``) each get one
reply with the same ``id``. ``log`` frames carry printed output and get no
reply. One ``done`` frame ends the run.

The program never sees credentials, connectors or policy. Every capability it
has is a request to the host, which decides. The helpers that need no host
(``grep``, ``tail``, ``summarize``, ``shape``) are plain functions over values
the program already holds.
"""

import ast
import builtins
import io
import json
import linecache
import os
import queue
import re
import sys
import threading
import traceback
import types

_RUNNER_FILE = "codemode-runner"
_REPR_CAP = 1000000
_LOG_FLUSH_AT = 8192
_LOG_FRAME = 32768


class ToolError(Exception):
    """Raised in the program when a call is denied, fails, or is unknown.

    ``kind`` is a short machine-readable reason (``PolicyDenied``,
    ``UnknownTool``, ``ToolTimeout``, ...); ``message`` is for reading;
    ``hint``, when there is one, says what to do next.
    """

    def __init__(self, kind, message, hint=""):
        Exception.__init__(self, "%s: %s" % (kind, message))
        self.kind = kind
        self.message = message
        self.hint = hint


def _tool_error(error):
    error = error or {}
    return ToolError(error.get("type", "ToolError"), error.get("message", ""), error.get("hint", ""))


class _Channel(object):
    """Frames to and from the host, on descriptors the program cannot reach
    by accident.

    The protocol arrives on fd 0 and leaves on fd 1. Before any program code
    runs, both are moved to private descriptors, fd 0 is pointed at the null
    device and fd 1 at stderr. After that a ``print``, an ``input()``, a
    subprocess or a C extension writing to stdout cannot corrupt a frame or
    swallow one.

    Frames from the host are read by a thread of their own. If the host dies
    (killed by a shell timeout, say) its end of the pipe closes and this
    process exits at once, whatever the program is doing: a loop that never
    makes a request must not outlive the process that was watching it.
    """

    def __init__(self):
        fd_in = os.dup(0)
        fd_out = os.dup(1)
        null = os.open(os.devnull, os.O_RDONLY)
        os.dup2(null, 0)
        os.close(null)
        os.dup2(2, 1)
        self._in = os.fdopen(fd_in, "rb")
        self._out = os.fdopen(fd_out, "wb", 0)
        self._lock = threading.RLock()
        self._inbox = queue.Queue()
        self._next = 0
        self.in_step = False
        reader = threading.Thread(target=self._read, name="codemode-reader")
        reader.daemon = True
        reader.start()

    def _read(self):
        while True:
            try:
                line = self._in.readline()
            except (OSError, ValueError):
                line = b""
            if not line:
                os._exit(70)  # the host is gone; there is nobody to report to
            try:
                frame = json.loads(line.decode("ascii"))
            except ValueError:
                os._exit(71)
            self._inbox.put(frame)

    def send(self, frame):
        data = (json.dumps(frame, separators=(",", ":"), allow_nan=False) + "\n").encode("ascii")
        view = memoryview(data)
        with self._lock:
            while len(view):
                written = self._out.write(view)
                view = view[written:]

    def recv(self):
        return self._inbox.get()

    def request(self, frame):
        if self.in_step and frame.get("op") != "step_set":
            # On replay the step returns its recorded value and fn never runs,
            # so a request made inside fn would be missing from the sequence.
            raise RuntimeError("no tool calls, search or step inside step(); do them outside and pass the values in")
        with self._lock:
            self._next += 1
            frame["id"] = self._next
            self.send(frame)
            reply = self.recv()
        if reply.get("id") != frame["id"]:
            raise RuntimeError("protocol error: reply does not match request")
        if not reply.get("ok"):
            raise _tool_error(reply.get("error"))
        return reply


class _LogStream(io.TextIOBase):
    """``sys.stdout`` and ``sys.stderr`` for the program: text becomes
    ``log`` frames. Buffered by line so a loop of prints is not a frame per
    character."""

    def __init__(self, channel, name):
        io.TextIOBase.__init__(self)
        self._channel = channel
        self._name = name
        self._parts = []
        self._size = 0

    def writable(self):
        return True

    def isatty(self):
        return False

    def write(self, text):
        if not isinstance(text, str):
            raise TypeError("write() argument must be str, not %s" % type(text).__name__)
        self._parts.append(text)
        self._size += len(text)
        if "\n" in text or self._size >= _LOG_FLUSH_AT:
            self.flush()
        return len(text)

    def flush(self):
        if not self._parts:
            return
        text = "".join(self._parts)
        self._parts = []
        self._size = 0
        for start in range(0, len(text), _LOG_FRAME):
            self._channel.send({"op": "log", "stream": self._name, "text": text[start:start + _LOG_FRAME]})


_channel = None


def _jsonable(value, what):
    """Round-trip through JSON so the program holds exactly what the host
    recorded. A replayed pass then sees the same types as the first one."""
    try:
        return json.loads(json.dumps(value, allow_nan=False))
    except (TypeError, ValueError) as exc:
        raise TypeError("%s must be JSON-serializable: %s" % (what, exc))


# -- tool calls -----------------------------------------------------------


def parallel(calls, return_errors=False, on_error=None):
    """Run independent tool calls at the same time.

    ``calls`` is a list of ``("namespace.tool", {arguments})`` pairs. Results
    come back in the same order. With ``on_error="return"`` a failed call
    yields its ``ToolError`` in the list instead of raising, as it does for
    ``call()``. ``return_errors=True`` is the older spelling of the same.
    """
    if on_error is not None:
        if on_error not in ("raise", "return"):
            raise ValueError("on_error must be 'raise' or 'return', not %r" % (on_error,))
        if return_errors and on_error == "raise":
            raise ValueError("return_errors=True and on_error='raise' contradict each other; give on_error alone")
        return_errors = on_error == "return"
    batch = []
    for entry in calls:
        if isinstance(entry, str):
            path, args = entry, {}
        else:
            entry = tuple(entry)
            if len(entry) == 1:
                path, args = entry[0], {}
            elif len(entry) == 2:
                path, args = entry
            else:
                raise TypeError("parallel() takes (path, arguments) pairs")
        if not isinstance(path, str):
            raise TypeError("a tool path must be a string like 'namespace.tool'")
        if args is None:
            args = {}
        if not isinstance(args, dict):
            raise TypeError("arguments of %s must be a dict" % path)
        batch.append({"tool": path, "args": _jsonable(args, "arguments of %s" % path)})
    if not batch:
        return []
    reply = _channel.request({"op": "call", "calls": batch})
    out = []
    for item in reply["results"]:
        if item.get("ok"):
            out.append(item.get("value"))
            continue
        failure = _tool_error(item.get("error"))
        if not return_errors:
            raise failure
        out.append(failure)
    return out


def call(path, args=None, on_error="raise", **kwargs):
    """Call one tool by path: ``call("demo.read_note", {"id": 1})``.

    ``on_error="return"`` gives a failed call's ``ToolError`` back as the
    value instead of raising it, so one failure does not end the program:
    check with ``isinstance(value, ToolError)``.
    """
    if on_error not in ("raise", "return"):
        raise ValueError("on_error must be 'raise' or 'return', not %r" % (on_error,))
    merged = dict(args or {})
    merged.update(kwargs)
    return parallel([(path, merged)], return_errors=(on_error == "return"))[0]


def _run_failed(value, ok):
    """Why a run of ``repeat`` counts as failed, or None."""
    if isinstance(value, ToolError):
        return {"error": value.kind, "message": value.message}
    if ok is not None:
        try:
            if ok(value):
                return None
        except Exception as exc:
            return {"result": value, "check_raised": "%s: %s" % (type(exc).__name__, exc)}
        return {"result": value}
    if isinstance(value, dict) and value.get("ok") is False:
        return {"result": value}
    return None


def repeat(path, args=None, n=5, ok=None, until_fail=False, concurrency=1, keep=5):
    """Run one tool call ``n`` times and return only what failed.

    A run fails when the call raises ``ToolError``, when ``ok(result)`` is
    false, or, with no ``ok``, when the result is a dict whose ``"ok"`` is
    false. ``until_fail=True`` stops at the first failure. ``concurrency``
    runs that many at a time (the default is one after the other).

    Returns ``{"runs", "passed", "failed", "failures": [{"run": i, ...}],
    "failures_omitted", "stopped_early"}``; ``failures`` holds the first
    ``keep`` of them.
    """
    n = int(n)
    concurrency = max(1, int(concurrency))
    if n < 1:
        raise ValueError("repeat(): n must be at least 1")
    runs = 0
    failures = []
    failed = 0
    while runs < n and not (until_fail and failed):
        size = min(concurrency, n - runs)
        for value in parallel([(path, dict(args or {}))] * size, return_errors=True):
            runs += 1
            why = _run_failed(value, ok)
            if why is not None:
                failed += 1
                if len(failures) < keep:
                    failures.append(dict(why, run=runs))
    return {
        "runs": runs,
        "passed": runs - failed,
        "failed": failed,
        "failures": failures,
        "failures_omitted": failed - len(failures),
        "stopped_early": runs < n,
    }


class _Tool(object):
    __slots__ = ("_path",)

    def __init__(self, path):
        self._path = path

    def __call__(self, *args, **kwargs):
        if args:
            if len(args) == 1 and isinstance(args[0], dict) and not kwargs:
                return call(self._path, args[0])
            raise TypeError(
                "%s takes keyword arguments; see describe(%r)" % (self._path, self._path)
            )
        return parallel([(self._path, kwargs)])[0]

    def __repr__(self):
        return "<tool %s>" % self._path


class _Namespace(object):
    """``tools.demo``. Any attribute is a tool; whether it exists is the
    host's call, so that an unknown name is recorded like any other outcome
    and a replayed pass behaves the same."""

    __slots__ = ("_name",)

    def __init__(self, name):
        self._name = name

    def __getattr__(self, tool):
        if tool.startswith("_"):
            raise AttributeError(tool)
        return _Tool("%s.%s" % (self._name, tool))

    def __repr__(self):
        return "<namespace %s; use describe(%r)>" % (self._name, self._name)


class _Tools(object):
    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        return _Namespace(name)

    def __repr__(self):
        return "<tools; use search('words') to find one>"


# -- discovery ------------------------------------------------------------


class _Listing(list):
    """A list that knows how much it left out: ``total`` is the full count,
    ``more`` is how many are not in the list."""

    total = 0
    more = 0


class _Info(dict):
    """What ``describe()`` returns: a dict that prints as short text."""

    def __str__(self):
        return _info_text(self)

    def __getitem__(self, key):
        if isinstance(key, slice):
            raise TypeError(
                "describe() returns a dict (keys: %s), not text; use str(...) for a short text, or a key such as "
                "['signature']" % ", ".join(sorted(self))
            )
        return dict.__getitem__(self, key)


def _info_text(info):
    kind = info.get("kind")
    if kind == "tool":
        lines = [str(info.get("signature", info.get("path", "")))]
        if info.get("description"):
            lines.append("  " + " ".join(str(info["description"]).split()))
        policy = str(info.get("policy", ""))
        if info.get("policyDependsOnArguments"):
            policy += " (some rules depend on the arguments)"
        lines.append("  policy: " + policy)
        if "outputSchema" not in info:
            lines.append("  result: no schema published; look at one result with shape(value)")
        return "\n".join(lines)
    if kind == "namespace":
        lines = ["%s: %s tools" % (info.get("namespace"), info.get("toolCount"))]
        lines += ["  " + str(sig) for sig in info.get("tools", [])]
        if info.get("toolsNotShown"):
            lines.append("  ... and %s more; use search('words')" % info["toolsNotShown"])
        return "\n".join(lines)
    return json.dumps(dict(info), indent=1, sort_keys=True)


def search(query="", limit=None, namespace=None):
    """Find tools, snippets and helpers. Returns a list of one-line entries.

    With no ``limit`` a query returns its 10 best matches and an empty query
    lists up to 50 entries. The list has ``.total`` (all matches) and
    ``.more`` (matches left out); when something was left out a note goes to
    the logs as well.

    ``namespace`` keeps the search to one server's tools:
    ``search(namespace="files")`` lists what that server offers, each with
    its description and its policy.
    """
    frame = {"op": "search", "query": str(query)}
    if limit is not None:
        frame["limit"] = int(limit)
    if namespace is not None:
        frame["namespace"] = str(namespace)
    value = _channel.request(frame)["value"]
    if isinstance(value, dict):
        hits = _Listing(value.get("hits") or [])
        hits.total = int(value.get("total", len(hits)))
    else:  # the shape recorded by an older runtime
        hits = _Listing(value or [])
        hits.total = len(hits)
    hits.more = max(0, hits.total - len(hits))
    if hits.more:
        print("search(%r): showing %d of %d matches; pass limit=%d for all of them, or narrow the query."
              % (str(query), len(hits), hits.total, hits.total))
    return hits


def describe(target):
    """Full detail for ``"namespace.tool"``, a namespace, or ``"snippet:name"``.

    Returns a dict (``signature``, ``description``, ``inputSchema``,
    ``policy``, ...); ``str(describe(x))`` is a short text.
    """
    return _Info(_channel.request({"op": "describe", "target": str(target)})["value"])


def step(name, fn):
    """Record the value of ``fn()`` under ``name``.

    Use it for anything that would differ if the program ran again: the time,
    a random choice, a file listing. When an execution resumes after a pause
    the program runs again from the top, and ``step`` returns the recorded
    value instead of calling ``fn``.
    """
    if not callable(fn):
        raise TypeError("step(name, fn): fn must be callable")
    reply = _channel.request({"op": "step", "name": str(name)})
    if reply.get("hit"):
        return reply.get("value")
    _channel.in_step = True
    try:
        value = _jsonable(fn(), "the value of step %r" % (name,))
    except Exception as exc:
        # Recorded as a failure so that a replayed pass fails the same way.
        message = "%s: %s" % (type(exc).__name__, exc)
        _channel.request({"op": "step_set", "seq": reply["seq"], "error": {"type": "StepError", "message": message}})
        raise ToolError("StepError", message)
    finally:
        _channel.in_step = False
    _channel.request({"op": "step_set", "seq": reply["seq"], "value": value})
    return value


# -- helpers over text and values (no host involved) ----------------------


def _rows(text):
    if text is None:
        return []
    if isinstance(text, (list, tuple)):
        return [str(row) for row in text]
    return str(text).splitlines()


def grep(text, pattern, context=0, limit=50, ignore_case=False, invert=False):
    """The lines of ``text`` that match a regular expression.

    ``text`` is a string or a list of lines. ``context`` adds that many lines
    around each match. The result is a list with ``.total`` (all matching
    lines) and ``.more`` (matches beyond ``limit``, not shown).
    """
    rows = _rows(text)
    matcher = re.compile(pattern, re.IGNORECASE if ignore_case else 0)
    matches = [i for i, row in enumerate(rows) if bool(matcher.search(row)) != bool(invert)]
    shown = matches[: max(0, int(limit))]
    picked = []
    last = -1
    for i in shown:
        for j in range(max(last + 1, i - int(context)), min(len(rows), i + int(context) + 1)):
            picked.append(rows[j])
            last = j
    out = _Listing(picked)
    out.total = len(matches)
    out.more = len(matches) - len(shown)
    return out


def tail(text, n=20):
    """The last ``n`` lines of ``text``, as one string."""
    rows = _rows(text)
    return "\n".join(rows[-int(n):] if int(n) > 0 else [])


def head(text, n=20):
    """The first ``n`` lines of ``text``, as one string."""
    return "\n".join(_rows(text)[: max(0, int(n))])


_SIGNALS = (
    ("error", r"\berrors?\b|\bfatal\b|\bexception\b|Traceback"),
    ("failure", r"\bfail(ed|ure|ures|s|ing)?\b"),
    ("warning", r"\bwarn(ing|ings)?\b"),
    ("timeout", r"\btime(d)?[ -]?out\b"),
)


def _clip(row, width):
    return row if len(row) <= width else row[:width] + " ...[+%d chars]" % (len(row) - width)


def summarize(text, patterns=None, head=3, tail=5, limit=5, width=200):
    """A small, structured summary of long text output.

    Returns ``{"chars", "lines", "head": [...], "tail": [...], "counts":
    {name: n}, "matches": {name: [first lines]}}``. ``patterns`` maps a name
    to a regular expression; by default it counts lines that mention errors,
    failures, warnings and timeouts (case-insensitive). ``matches`` holds the
    first ``limit`` lines per pattern, and every line is cut to ``width``.
    """
    body = "\n".join(text) if isinstance(text, (list, tuple)) else ("" if text is None else str(text))
    rows = [row for row in body.splitlines() if row.strip()]
    if patterns is None:
        wanted = list(_SIGNALS)
        flags = re.IGNORECASE
    else:
        wanted = list(patterns.items()) if isinstance(patterns, dict) else [(str(p), p) for p in patterns]
        flags = 0
    counts = {}
    matches = {}
    for name, pattern in wanted:
        matcher = re.compile(pattern, flags)
        found = [row for row in rows if matcher.search(row)]
        counts[name] = len(found)
        if found:
            matches[name] = [_clip(row, width) for row in found[: max(0, int(limit))]]
    head, tail = max(0, int(head)), max(0, int(tail))
    if head + tail >= len(rows):
        first, last = rows, []
    else:
        first, last = rows[:head], rows[len(rows) - tail:] if tail else []
    return {
        "chars": len(body),
        "lines": len(rows),
        "head": [_clip(row, width) for row in first],
        "tail": [_clip(row, width) for row in last],
        "counts": counts,
        "matches": matches,
    }


def shape(value, depth=3, keys=12):
    """The structure of a value without its content: keys, types and sizes.

    ``shape({"ok": True, "out": "x" * 900, "rows": [{"id": 1}]})`` is
    ``{"ok": "bool", "out": "str[900]", "rows": {"list": 1, "of": {"id":
    "int"}}}``. Use it on a tool result before writing code that indexes it.
    """
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "bool"
    if isinstance(value, int):
        return "int"
    if isinstance(value, float):
        return "float"
    if isinstance(value, str):
        return "str[%d]" % len(value)
    if isinstance(value, dict):
        if depth <= 0:
            return "dict[%d keys]" % len(value)
        out = {}
        for name in list(value)[: max(1, int(keys))]:
            out[str(name)] = shape(value[name], depth - 1, keys)
        if len(value) > len(out):
            out["..."] = "+%d keys" % (len(value) - len(out))
        return out
    if isinstance(value, (list, tuple)):
        if not value:
            return "list[0]"
        if depth <= 0:
            return "list[%d]" % len(value)
        return {"list": len(value), "of": shape(value[0], depth - 1, keys)}
    return type(value).__name__


# -- running the program --------------------------------------------------


def _lost_value(tree):
    """True when the program ends with a block (if, for, try, with) whose own
    last statement is a bare value. That value is not the result, and a
    program written that way expected it to be."""
    if not tree.body:
        return False
    last = tree.body[-1]
    if not isinstance(last, (ast.If, ast.For, ast.While, ast.With, ast.Try)):
        return False  # a def or a class ends in a body too, and nobody expects its value
    blocks = []
    for field in ("body", "orelse", "finalbody"):
        blocks.append(getattr(last, field, None))
    for handler in getattr(last, "handlers", None) or []:
        blocks.append(handler.body)
    for block in blocks:
        if isinstance(block, list) and block and isinstance(block[-1], ast.Expr):
            # A call is there for its effect (print, a tool call); a name, a
            # dict or a sum is there to be the result.
            if not isinstance(block[-1].value, ast.Call):
                return True
    return False


def _exec_block(code, namespace, filename, notes=None):
    """Run a block; the value of a trailing expression is the result."""
    linecache.cache[filename] = (len(code), None, code.splitlines(True), filename)
    tree = ast.parse(code, filename, "exec")
    if tree.body and isinstance(tree.body[-1], ast.Expr):
        last = tree.body.pop()
        exec(compile(tree, filename, "exec"), namespace)
        return eval(compile(ast.Expression(last.value), filename, "eval"), namespace)
    if notes is not None and _lost_value(tree):
        notes.append(
            "The result is null: the program ends with a block (if, for, try, with), and a value inside a "
            "block is not the result. Assign it to a name and end the program with that name on its own line."
        )
    exec(compile(tree, filename, "exec"), namespace)
    return None


_api = {}


def run(name, **args):
    """Run a saved snippet. The snippet reads its arguments from ``args``.

    ``args`` is also defined in an ordinary program (empty unless the caller
    passed arguments), so a program written as ``base = args.get("base", 0)``
    runs unchanged before and after it is promoted to a snippet.
    """
    reply = _channel.request({"op": "snippet", "name": str(name)})
    namespace = dict(_api)
    namespace["args"] = _jsonable(args, "snippet arguments")
    return _exec_block(reply["value"]["code"], namespace, "<snippet:%s>" % name)


class _Helpers(object):
    """``helpers.<module>.<function>()``: the project's own functions.

    A project keeps Python files in a folder named in its config. Their
    source arrives with the program and each file becomes a module here, in
    the sandbox, the first time it is used. A helper sees the same names a
    program sees (``tools``, ``call``, ``parallel``, ``state``, ...), so a
    tool call it makes is a request to the host like any other: same policy,
    same limits, same log. ``from helpers import name`` works too.
    """

    def __init__(self, sources):
        self._sources = sources
        self._modules = {}

    def __getattr__(self, name):
        if name.startswith("_"):
            raise AttributeError(name)
        if name in self._modules:
            return self._modules[name]
        if name not in self._sources:
            raise AttributeError(
                "no helper module %r; this project has: %s" % (name, ", ".join(sorted(self._sources)) or "none")
            )
        source = self._sources[name]
        filename = "<helper:%s>" % name
        linecache.cache[filename] = (len(source), None, source.splitlines(True), filename)
        module = types.ModuleType("helpers." + name)
        module.__dict__.update(_api)
        module.__dict__["__name__"] = "helpers." + name
        self._modules[name] = module  # before it runs: two helpers may use each other
        try:
            exec(compile(source, filename, "exec"), module.__dict__)
        except BaseException:
            del self._modules[name]
            raise
        sys.modules["helpers." + name] = module
        return module

    def __dir__(self):
        return sorted(self._sources)

    def __repr__(self):
        return "<helpers: %s>" % (", ".join(sorted(self._sources)) or "none")


def _result_default(value):
    if isinstance(value, (set, frozenset)):
        try:
            return sorted(value)
        except TypeError:
            return list(value)
    raise TypeError("not JSON-serializable")


def _encode_result(value):
    """(json value, was_repr). A value JSON cannot hold comes back as its
    repr, flagged, rather than failing a program that already did its work."""
    try:
        return json.loads(json.dumps(value, default=_result_default, allow_nan=False)), False
    except (TypeError, ValueError):
        text = repr(value)
        if len(text) > _REPR_CAP:
            text = text[:_REPR_CAP] + "... [repr cut at %d chars]" % _REPR_CAP
        return text, True


def _encode_state(state):
    if not isinstance(state, dict):
        raise TypeError("state must be a dict, not %s" % type(state).__name__)
    out = {}
    for key, value in state.items():
        if not isinstance(key, str):
            raise TypeError("state keys must be strings, got %r" % (key,))
        out[key] = _jsonable(value, "state[%r]" % key)
    return out


def _snapshot(namespace, initial, budget):
    """The top-level variables of a program that failed, as JSON.

    A program that dies on its last call has usually done its work already:
    the results sit in its variables. They are sent to the host, smallest
    first, up to ``budget`` characters; the host keeps them in the log, where
    they can be fetched, and shows only their shapes.
    """
    encoded = {}
    for name, value in list(namespace.items()):
        if not isinstance(name, str) or name.startswith("_"):
            continue
        if name in initial and initial[name] is value:
            continue  # part of the API, not the program's
        if isinstance(value, (types.ModuleType, types.FunctionType, types.BuiltinFunctionType, type)):
            continue
        try:
            encoded[name] = json.dumps(value, default=_result_default, allow_nan=False)
        except (TypeError, ValueError, RecursionError):
            continue
    kept = {}
    omitted = []
    used = 0
    for name in sorted(encoded, key=lambda key: (len(encoded[key]), key)):
        size = len(encoded[name])
        if used + size > budget:
            omitted.append({"name": name, "chars": size})
            continue
        used += size
        kept[name] = json.loads(encoded[name])
    return kept, omitted


_SHELL_HINT = (
    "If this program went through a shell (a heredoc, -c, echo), the shell may have changed its backslashes: "
    "\\n in a string becomes a real line break. Write the program to a file with your file tool and run it "
    "with -f, or pass it to the execute tool, where no shell is involved."
)
_SYNTAX_HINTS = (
    ("'return' outside function", "End the program with an expression; its value is the result."),
    ("'await' outside", "Calls are synchronous here: remove 'await'. Use parallel([...]) to run calls at the same time."),
    ("unterminated string literal", _SHELL_HINT),
    ("EOL while scanning string literal", _SHELL_HINT),
    ("unexpected character after line continuation", _SHELL_HINT),
    ("(unicode error)", "A backslash in a string started an escape such as \\U or \\x. Use forward slashes in "
                        "paths, or a raw string: r'C:\\Users\\name'."),
)
_SHAPE_HINT = (
    "The value is not shaped the way the program assumed. Look before indexing: shape(value) shows keys and "
    "types, and describe('namespace.tool') shows a tool's result schema when the server publishes one."
)
_UNCAUGHT_HINT = (
    "A failed call raised ToolError and ended the program. Wrap calls that may fail in try/except ToolError, "
    "or use call(path, args, on_error='return'), so the rest of the work is kept."
)
_SHAPE_ERRORS = ("string indices must be", "is not subscriptable", "'NoneType' object", "has no attribute",
                 "list indices must be", "object is not iterable")


def _relative_path_hint(exc):
    """What to say when a program opened a file by a relative path and it
    was not there: the program runs in an empty folder of its own."""
    name = getattr(exc, "filename", None)
    if not isinstance(name, str) or not name or os.path.isabs(name) or name.startswith("<"):
        return ""
    shown = name.replace("\\", "/")
    if shown.startswith("./"):
        shown = shown[2:]
    return (
        "A program runs in an empty folder of its own, so a relative path does not reach the project's files. "
        "The project's folder is project_root: open(project_root + '/%s')." % shown
    )


def _describe_error(exc):
    kind = type(exc).__name__
    message = str(exc)
    hint = ""
    if isinstance(exc, SyntaxError):
        message = "%s (line %s)" % (exc.msg, exc.lineno)
        for needle, text in _SYNTAX_HINTS:
            if needle in (exc.msg or ""):
                hint = text
    elif isinstance(exc, SystemExit):
        hint = "Do not call exit(); end the program with an expression."
    elif isinstance(exc, ToolError):
        kind = exc.kind
        message = exc.message
        hint = " ".join(part for part in (exc.hint, _UNCAUGHT_HINT) if part)
    elif isinstance(exc, (KeyError, IndexError)):
        hint = _SHAPE_HINT
    elif isinstance(exc, (TypeError, AttributeError)) and any(needle in message for needle in _SHAPE_ERRORS):
        hint = _SHAPE_HINT
    elif isinstance(exc, (FileNotFoundError, NotADirectoryError)):
        hint = _relative_path_hint(exc)
    frames =[f for f in traceback.extract_tb(exc.__traceback__) if f.filename != _RUNNER_FILE]
    if isinstance(exc, SyntaxError) and str(exc.filename or "").startswith("<"):
        # The program itself did not parse. The frames are the parser's, not
        # the program's; the exception line already shows the place.
        frames = []
    lines = ["Traceback (most recent call last):\n"] if frames else []
    lines += traceback.format_list(frames) + traceback.format_exception_only(type(exc), exc)
    error = {"type": kind, "message": message, "traceback": "".join(lines)[-6000:]}
    if hint:
        error["hint"] = hint
    return error


_PATH = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$")


def _check(code):
    """Compile the program without running it, and list the tools it names.

    ``calls`` are certain (``tools.a.b(...)``, ``call("a.b", ...)``);
    ``maybe`` are strings that look like tool paths at the head of a tuple or
    list, the way ``parallel`` takes them.
    """
    tree = ast.parse(code, "<code>", "exec")
    compile(tree, "<code>", "exec")
    calls = []
    maybe = []
    for node in ast.walk(tree):
        if isinstance(node, ast.Attribute) and isinstance(node.value, ast.Attribute):
            inner = node.value
            if isinstance(inner.value, ast.Name) and inner.value.id == "tools":
                calls.append("%s.%s" % (inner.attr, node.attr))
        elif isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id in ("call", "repeat"):
            if node.args and isinstance(node.args[0], ast.Constant) and isinstance(node.args[0].value, str):
                calls.append(node.args[0].value)
        elif isinstance(node, (ast.Tuple, ast.List)) and node.elts:
            first = node.elts[0]
            if isinstance(first, ast.Constant) and isinstance(first.value, str) and _PATH.match(first.value):
                maybe.append(first.value)
    return {
        "calls": sorted(set(calls)),
        "maybe": sorted(set(maybe) - set(calls)),
        "endsWithExpression": bool(tree.body) and isinstance(tree.body[-1], ast.Expr),
        "lostValue": _lost_value(tree),
    }


def _main():
    global _channel
    channel = _Channel()
    _channel = channel
    channel.send({"op": "ready", "python": "%d.%d.%d" % sys.version_info[:3]})
    job = channel.recv()
    out = _LogStream(channel, "stdout")
    err = _LogStream(channel, "stderr")
    sys.stdout, sys.stderr = out, err
    sys.stdin = open(os.devnull)
    namespace = {
        "__name__": "__codemode__",
        "__builtins__": builtins,
        "tools": _Tools(),
        "call": call,
        "parallel": parallel,
        "repeat": repeat,
        "search": search,
        "describe": describe,
        "step": step,
        "run": run,
        "grep": grep,
        "tail": tail,
        "head": head,
        "summarize": summarize,
        "shape": shape,
        "state": job.get("state") or {},
        "args": job.get("args") or {},
        # The project's folder, as text. The program itself runs in an empty
        # folder, so this is how it names a file of the project.
        "project_root": str(job.get("project_root") or ""),
        "helpers": _Helpers(job.get("helpers") or {}),
        "ToolError": ToolError,
    }
    sys.modules["helpers"] = namespace["helpers"]  # so that "from helpers import name" works
    _api.update(namespace)
    initial = dict(namespace)
    notes = []
    try:
        if job.get("check"):
            frame = {"op": "done", "ok": True, "result": _check(job["code"]), "result_repr": False, "state": {}}
        else:
            value = _exec_block(job["code"], namespace, "<code>", notes)
            result, was_repr = _encode_result(value)
            frame = {
                "op": "done",
                "ok": True,
                "result": result,
                "result_repr": was_repr,
                "state": _encode_state(namespace.get("state")),
                "notes": notes if result is None else [],
            }
    except BaseException as exc:  # SystemExit and KeyboardInterrupt are outcomes too
        frame = {"op": "done", "ok": False, "error": _describe_error(exc)}
        try:
            frame["vars"], frame["vars_omitted"] = _snapshot(namespace, initial, int(job.get("vars_budget") or 0))
        except Exception:
            pass  # the snapshot is a courtesy; the error is the outcome
    try:
        out.flush()
        err.flush()
    except Exception:
        pass
    channel.send(frame)
    os._exit(0)  # do not wait for threads the program may have left running


if __name__ == "__main__":
    _main()
