# SPDX-License-Identifier: MIT
"""Setting a project up, and checking that it is set up.

``init`` writes a ``codemode.json`` for the project in the current folder.
It starts from what the project already declares for its agents: the MCP
server list that MCP clients read, ``.mcp.json`` in the project root or an
``mcp.json`` in one of its dot-folders, where several clients keep theirs.
It then connects to each server once, to check the connection and to
propose a starting policy, and adds the data folders to ``.gitignore``.

``doctor`` answers "why does it not work here?" without anyone reading a
traceback: the interpreter, the config, the data folder, the sandbox, every
connector, the policy and the records, each as one line that says what is
wrong and what to do.

Neither writes or prints a credential. A literal secret found in a server
list is replaced by a ``${NAME}`` reference in the config that is written,
and the person is told which variable to export.
"""

from __future__ import annotations

import json
import os
import re
import sys
import threading
import time
from fnmatch import fnmatchcase
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

from . import launcher
from .config import CONFIG_NAME, DEFAULT_WORK_DIR, RESERVED_NAMES, Config, config_from_dict
from .connectors import build_connector, looks_secret, redact, value_looks_secret
from .engine import check_program
from .errors import CodemodeError, ConfigError, ToolFailure, UsageError
from .helpers import describe_helpers, load_helpers
from .policy import Rule
from .textio import read_text

SERVER_LISTS = ".mcp.json, or mcp.json in a dot-folder of the project"


def find_server_list(folder: Path) -> Optional[Path]:
    """The project's MCP server list: ``.mcp.json`` in the root, else the
    first ``mcp.json`` in a dot-folder, by name. No client is named here:
    whichever tool the project uses, this is where such tools keep it."""
    root = folder / ".mcp.json"
    if root.is_file():
        return root
    return next((path for path in sorted(folder.glob(".*/mcp.json")) if path.is_file()), None)


IGNORE_ENTRIES = (".codemode/", ".codemode-work/")
READ_PREFIXES = ("list_", "get_", "search_", "read_")
_REFERENCE = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}")
_INPUT = re.compile(r"\$\{input:([A-Za-z0-9_.-]+)\}")
_SLOW_START_S = 5.0
_BIG_SCHEMAS = 200_000
_BIG_RECORDS = 200 * 1024 * 1024


# -- reading a server list: pure --------------------------------------------------


def connector_name(name: str, used: List[str]) -> str:
    """A server's name as a namespace a program can write: an identifier,
    not one of the sandbox's own names, and not taken."""
    cleaned = re.sub(r"[^0-9A-Za-z]+", "_", str(name)).strip("_") or "server"
    if cleaned[0].isdigit():
        cleaned = "s_" + cleaned
    if cleaned in RESERVED_NAMES or not cleaned.isidentifier():
        cleaned += "_server"
    candidate, n = cleaned, 1
    while candidate in used:
        n += 1
        candidate = "%s_%d" % (cleaned, n)
    return candidate


def _variable(*parts: str) -> str:
    return re.sub(r"[^0-9A-Za-z]+", "_", "_".join(parts)).strip("_").upper()


def _convert_values(values: Any, owner: str, export: List[str], notes: List[str], what: str) -> Dict[str, str]:
    """``env`` or ``headers`` of one server, with no literal secret left in
    them. A reference stays a reference; a literal under a name that says
    credential becomes a reference to a variable the person must export."""
    out: Dict[str, str] = {}
    if not isinstance(values, dict):
        return out
    for key, raw in values.items():
        if not isinstance(key, str) or not isinstance(raw, (str, int, float, bool)):
            continue
        value = str(raw)
        if _INPUT.search(value):
            name = _variable(_INPUT.search(value).group(1))  # type: ignore[union-attr]
            out[key] = "${%s}" % name
            export.append(name)
            notes.append("%s: %s %s asked the editor for a value; it now reads the variable %s." % (owner, what, key, name))
        elif _REFERENCE.search(value):
            if ":-" in value:
                notes.append("%s: %s %s had a default value in its reference; the default was dropped." % (owner, what, key))
            out[key] = _REFERENCE.sub(lambda m: "${%s}" % m.group(1), value)
        elif looks_secret(key) or value_looks_secret(value):
            scheme, _, rest = value.partition(" ")
            name = _variable(owner, "TOKEN" if rest else key)
            out[key] = ("%s ${%s}" % (scheme, name)) if rest else "${%s}" % name
            export.append(name)
            notes.append(
                "%s: %s %s held a literal value that looks like a credential. It was not copied; the config reads "
                "the variable %s instead." % (owner, what, key, name)
            )
        else:
            out[key] = value
    return out


def convert_server(name: str, raw: Any, export: List[str], notes: List[str]) -> Optional[Dict[str, Any]]:
    """One entry of an MCP server list as a connector spec, or None when it
    is not something this runtime can connect to (the reason goes to
    ``notes``)."""
    if not isinstance(raw, dict):
        notes.append("%s: skipped; its entry is not an object." % name)
        return None
    if raw.get("disabled") is True:
        notes.append("%s: skipped; it is disabled in the server list." % name)
        return None
    kind = str(raw.get("type") or raw.get("transport") or "").lower()
    url = raw.get("url") or raw.get("serverUrl")
    if isinstance(url, str) and url:
        spec: Dict[str, Any] = {"type": "mcp-sse" if kind == "sse" else "mcp-http", "url": url}
        headers = _convert_values(raw.get("headers"), name, export, notes, "header")
        if headers:
            spec["headers"] = headers
        return spec
    command = raw.get("command")
    if isinstance(command, str) and command:
        args = raw.get("args") if isinstance(raw.get("args"), list) else []
        spec = {"type": "mcp-stdio", "command": [command] + [str(a) for a in args]}
        env = _convert_values(raw.get("env"), name, export, notes, "variable")
        if env:
            spec["env"] = env
        if isinstance(raw.get("cwd"), str) and raw["cwd"]:
            spec["cwd"] = raw["cwd"]
        return spec
    notes.append("%s: skipped; it has neither a command nor a url." % name)
    return None


def read_server_list(text: str, source: str) -> Dict[str, Any]:
    """The servers in an MCP client's config: ``mcpServers`` (most clients)
    or ``servers`` (one editor's spelling). Raises ``ConfigError``."""
    try:
        data = json.loads(text)
    except ValueError as exc:
        raise ConfigError("%s is not readable JSON: %s" % (source, exc))
    if not isinstance(data, dict):
        raise ConfigError("%s is not a JSON object" % source)
    for key in ("mcpServers", "servers"):
        if isinstance(data.get(key), dict):
            return data[key]
    raise ConfigError("%s has no server list" % source, hint='Expected {"mcpServers": {"name": {"command": ..., "args": [...]}}}.')


def connectors_from_servers(servers: Dict[str, Any]) -> Tuple[Dict[str, Dict[str, Any]], List[str], List[str]]:
    """(connectors, variables to export, notes). A server that points back at
    this runtime is left out: a program must not reach the runtime through
    itself."""
    connectors: Dict[str, Dict[str, Any]] = {}
    export: List[str] = []
    notes: List[str] = []
    for name, raw in servers.items():
        if isinstance(raw, dict) and _is_this_runtime(raw):
            notes.append("%s: skipped; it is this runtime's own MCP server." % name)
            continue
        spec = convert_server(str(name), raw, export, notes)
        if spec is None:
            continue
        key = connector_name(str(name), list(connectors))
        if key != name:
            notes.append("%s: its tools are under the namespace %s (a namespace is written in programs, so it must be an identifier)." % (name, key))
        connectors[key] = spec
    return connectors, sorted(set(export)), notes


def _is_this_runtime(raw: Dict[str, Any]) -> bool:
    parts = [str(raw.get("command") or "")] + [str(a) for a in (raw.get("args") if isinstance(raw.get("args"), list) else [])]
    joined = " ".join(parts)
    if "serve" not in parts:
        return False
    # By its own name, or by the name the project starts it under.
    return "codemode" in joined.lower() or "codemode_runtime" in joined or launcher.is_own_command(parts[0], parts[1:])


def starting_policy(tools_by_connector: Dict[str, Optional[List[Dict[str, Any]]]]) -> Dict[str, Any]:
    """A policy for a project that has none: reads run, everything else
    asks, deletes never run.

    A tool counts as a read when its server says so (``readOnlyHint``) or,
    for a server that annotates nothing, when its name starts with a reading
    verb. A tool marked destructive is listed under ``ask`` even if its name
    looks like a read. The person reviews the result: a hint from a server is
    a hint.
    """
    allow: List[str] = []
    ask: List[str] = []
    for namespace, tools in tools_by_connector.items():
        tools = tools or []
        annotated = [t for t in tools if isinstance(t.get("annotations"), dict) and "readOnlyHint" in t["annotations"]]
        if annotated:
            allow.extend("%s.%s" % (namespace, t["name"]) for t in annotated if t["annotations"].get("readOnlyHint") is True)
        else:
            allow.extend("%s.%s*" % (namespace, prefix) for prefix in READ_PREFIXES)
        ask.extend(
            "%s.%s" % (namespace, t["name"]) for t in tools
            if isinstance(t.get("annotations"), dict) and t["annotations"].get("destructiveHint") is True
            and t["annotations"].get("readOnlyHint") is not True
        )
    policy: Dict[str, Any] = {"mode": "enforce", "default": "ask", "allow": allow, "deny": ["*.delete_*"]}
    if ask:
        policy["ask"] = ask
    return policy


def ignore_entries(root: Path, home: Optional[Path] = None, work_dir: Optional[str] = None) -> List[str]:
    """What a project keeps out of version control: the data folder and the
    folder agents write their programs into, as paths below ``root``. A
    project may keep either one somewhere else; the entry follows it, and a
    folder outside the project needs none."""
    entries: List[str] = []
    data = home if home is not None else Path(root) / ".codemode"  # a relative --home is relative to where the command runs
    work = Path(os.path.abspath(str(root))) / (work_dir or DEFAULT_WORK_DIR)
    for folder in (data, work):
        inside = folder_in_project(folder, root)
        if inside and inside + "/" not in entries:
            entries.append(inside + "/")
    return entries


def folder_in_project(folder: Path, root: Path) -> Optional[str]:
    """``folder`` as a path below the project root, with forward slashes,
    or None when it is the root itself or not inside it."""
    # abspath, not resolve(): the names as the project writes them, and no
    # trip to the disk for a folder that may not exist yet.
    try:
        inside = Path(os.path.abspath(str(folder))).relative_to(Path(os.path.abspath(str(root)))).as_posix()
    except ValueError:
        return None
    return None if inside in ("", ".") else inside


def gitignore_lines(existing: str, entries: Any = IGNORE_ENTRIES) -> List[str]:
    """The entries that have to be added to a ``.gitignore`` text."""
    present = {line.strip().rstrip("/") for line in existing.splitlines()}
    return [entry for entry in entries if entry.rstrip("/") not in present and "/" + entry.rstrip("/") not in present]


# -- probing connectors --------------------------------------------------------------


def probe(config: Config, home: Path) -> Dict[str, Dict[str, Any]]:
    """Connect to every connector once, side by side, and list its tools.
    Returns, per namespace, either ``tools`` (the raw descriptors) or
    ``error`` and ``hint``, with the seconds it took."""
    results: Dict[str, Dict[str, Any]] = {}

    def one(name: str, spec: Dict[str, Any]) -> None:
        started = time.monotonic()
        connector = None
        try:
            connector = build_connector(name, spec, home, float(spec.get("startup_timeout_s") or config.limits["startup_timeout_s"]), config.root)
            tools = connector.list_tools()
            results[name] = {"tools": tools}
        except ToolFailure as failure:
            results[name] = {"error": failure.message, "hint": failure.hint}
        except Exception as exc:  # a connector bug is a failed check, not a crashed command
            results[name] = {"error": "%s: %s" % (type(exc).__name__, exc), "hint": ""}
        finally:
            if "error" in results[name] and connector is not None:
                # What a server printed while failing may hold what it was given.
                results[name]["error"] = redact(results[name]["error"], connector.secrets())[0]
            results[name]["seconds"] = round(time.monotonic() - started, 2)
            if connector is not None:
                try:
                    connector.close()
                except Exception:
                    pass

    threads = [threading.Thread(target=one, args=item, daemon=True) for item in config.connectors.items()]
    for thread in threads:
        thread.start()
    for thread in threads:
        thread.join()
    return results


# -- init ------------------------------------------------------------------------------


def init(
    folder: Path,
    source: Optional[str] = None,
    force: bool = False,
    home: Optional[Path] = None,
    out: Optional[str] = None,
    work_dir: Optional[str] = None,
) -> Dict[str, Any]:
    """Write the config for the project in ``folder``: ``codemode.json``
    there, or the file ``out`` names. Returns a report: what was written,
    what each server answered, which variables to export.

    ``folder`` stays the project root wherever the config is written: the
    server list is looked for there, relative paths in the config are read
    from there, and ``.gitignore`` there is the one that is updated.
    """
    folder = Path(folder)
    target = Path(out) if out else folder / CONFIG_NAME
    if target.exists() and not force:
        raise UsageError(
            "%s already exists" % target,
            hint="Check it with: %s    To write it again from the server list, add --force." % launcher.command("doctor"),
        )
    if source:
        found: Optional[Path] = Path(source)
        if not found.is_file():  # type: ignore[union-attr]
            raise UsageError("the server list %s does not exist" % source)
    else:
        found = find_server_list(folder)
    notes: List[str] = []
    export: List[str] = []
    if found is not None:
        connectors, export, notes = connectors_from_servers(read_server_list(read_text(found), str(found)))
        if not connectors:
            raise UsageError(
                "%s lists no server this runtime can connect to" % found,
                hint=" ".join(notes) or "Add a server to it, or see docs/ADOPTING.md for a project with no MCP server.",
            )
    elif (folder / ".git").exists():
        # No server list: a project whose tools are commands. Start it with
        # two read-only ones, as an example of the shape.
        connectors = {"repo": {"type": "commands", "tools": {
            "status": {"description": "Files changed in the working tree.", "command": ["git", "status", "--short"]},
            "log": {"description": "The last commits, one line each.", "command": ["git", "log", "--oneline", "-n", "{count}"],
                    "params": {"count": {"type": "integer", "default": 10}}},
        }}}
        notes.append(
            "No MCP server list was found (%s), so the config starts with a commands connector: two read-only git "
            "commands as an example. Add the project's own commands next to them." % SERVER_LISTS
        )
    else:
        raise UsageError(
            "no MCP server list was found in %s" % folder,
            hint="Looked for %s. Name one with --from FILE, or write %s by hand: docs/ADOPTING.md shows a commands "
            "connector for a project with no MCP server." % (SERVER_LISTS, CONFIG_NAME),
        )
    raw: Dict[str, Any] = {"connectors": connectors, "policy": starting_policy({name: None for name in connectors})}
    if (folder / "codemode_helpers").is_dir():
        raw["helpers"] = "codemode_helpers"
    if work_dir is not None:
        raw["work_dir"] = work_dir
    # Validate before anything is written, then look at the servers.
    config = config_from_dict(raw, str(target), {})
    if work_dir is not None:
        raw["work_dir"] = config.work_dir  # as the config reads it: forward slashes, no trailing one
    config.path, config.root = target.resolve(), folder.resolve()
    data_home = home or (folder / ".codemode")
    answers = probe(config, data_home)
    if "repo" in connectors and connectors["repo"]["type"] == "commands":
        raw["policy"] = {"mode": "enforce", "default": "ask", "allow": ["repo.status", "repo.log"], "deny": ["*.delete_*"]}
    else:
        raw["policy"] = starting_policy({name: answers[name].get("tools") for name in connectors})
    try:
        target.parent.mkdir(parents=True, exist_ok=True)
        # open(), not Path.write_text(newline=...): that argument is newer than
        # the oldest Python this runs on.
        with open(target, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(json.dumps(raw, indent=2, ensure_ascii=True) + "\n")
    except OSError as exc:
        raise UsageError("cannot write %s: %s" % (target, exc), hint="Name a file you can write with --out FILE.")
    report: Dict[str, Any] = {
        "status": "completed",
        "config": str(target),
        "from": str(found) if found is not None else None,
        "connectors": {
            name: ({"type": connectors[name]["type"], "tools": len(answers[name]["tools"]), "seconds": answers[name]["seconds"]}
                   if "tools" in answers[name] else
                   {"type": connectors[name]["type"], "error": answers[name]["error"], "hint": answers[name].get("hint", "")})
            for name in connectors
        },
        "policy": {"default": "ask", "allow": raw["policy"]["allow"], "ask": raw["policy"].get("ask", []), "deny": raw["policy"]["deny"]},
        "export": export,
        "notes": notes,
        "gitignore": _update_gitignore(folder, ignore_entries(folder, data_home, config.work_dir)),
    }
    if out and not launcher.chooses_config_itself():
        # Not under a command of the project's own that finds the config by
        # itself: there the person has nothing to pass and nothing to remember.
        notes.append(
            "The config is not where the runtime looks by itself (%s in the project root or above the folder a command runs in). "
            "Name it on every command with --config, as the commands below do, or in CODEMODE_CONFIG. Relative paths in it "
            "are read from the folder a command runs in: run from the project root." % CONFIG_NAME
        )
    report["next"] = [
        "Read the policy in %s: reads are allowed, everything else asks first. Allow the writes you are happy to leave to the agent." % target.name,
        "Check the setup at any time with: %s" % launcher.command("doctor"),
        "Give the agent its instructions: %s prints a block for the project's instructions file (AGENTS.md or the like)."
        % launcher.command("instructions"),
    ]
    if export:
        report["next"].insert(0, "Export these variables before running: %s" % ", ".join(export))
    return report


def _update_gitignore(folder: Path, entries: List[str]) -> str:
    path = folder / ".gitignore"
    if not path.exists() and not (folder / ".git").exists():
        return "skipped: this folder is not a git repository"
    if not entries:
        return "nothing to add: the data folder and the work folder are outside the project"
    try:
        # As bytes: reading as text would turn CRLF into LF and hide what
        # the file's own line endings are.
        existing = path.read_bytes().decode("utf-8", "replace") if path.exists() else ""
        missing = gitignore_lines(existing, entries)
        if not missing:
            return "already has %s" % ", ".join(entries)
        newline = "\r\n" if "\r\n" in existing else "\n"  # keep the file's own line endings
        block = ("" if not existing or existing.endswith(("\n", "\r")) else newline) + newline.join(
            ["# codemode: run records and working files"] + missing) + newline
        with open(path, "a", encoding="utf-8", newline="") as handle:
            handle.write(block)
        return "added %s" % ", ".join(missing)
    except OSError as exc:
        return "could not update .gitignore: %s" % exc


# -- doctor ------------------------------------------------------------------------------


def _check(name: str, status: str, detail: str, hint: str = "") -> Dict[str, Any]:
    out = {"check": name, "status": status, "detail": detail}
    if hint:
        out["hint"] = hint
    return out


def dead_rules(policy: Any, names: List[str], unknown_namespaces: List[str]) -> List[Tuple[str, str]]:
    """Policy rules that match no tool: (list, rule). A typo in an allow rule
    makes the tool ask; a typo in a deny rule makes it not denied, which is
    worse and just as silent. A rule for a namespace whose server could not
    be listed is left alone: nobody knows what it would match."""
    dead = []
    for action in ("deny", "ask", "allow"):
        for raw in getattr(policy, action):
            rule = raw if isinstance(raw, Rule) else Rule(raw)
            if any(fnmatchcase(name, rule.tool) for name in names):
                continue
            namespace = rule.tool.partition(".")[0]
            if any(char in namespace for char in "*?["):
                continue  # a rule for every namespace states an intent ("never delete"); it is not a misspelt name
            if any(fnmatchcase(unknown, namespace) for unknown in unknown_namespaces):
                continue
            dead.append((action, rule.label))
    return dead


def doctor(config: Config, home: Path, executor: Any, guidance: Any = None) -> Dict[str, Any]:
    """Check everything a program needs. Returns ``{"ok", "checks"}``; ``ok``
    is false when a check failed (a warning does not fail it). ``guidance``
    is the runtime's reader of the project's own text for its agents."""
    checks: List[Dict[str, Any]] = []
    version = "%d.%d.%d" % sys.version_info[:3]
    checks.append(_check("python", "ok" if sys.version_info >= (3, 9) else "fail", "%s at %s" % (version, sys.executable)))
    if config.path is None:
        checks.append(_check("config", "warn", "no %s in this folder or above it; the built-in demo config is in use" % CONFIG_NAME,
                             "Write one for this project with: %s" % launcher.command("init")))
    else:
        where = " (found from a folder below it; relative paths are read from %s)" % config.root if config.root else ""
        checks.append(_check("config", "ok", "%s%s" % (config.path, where)))

    try:
        home.mkdir(parents=True, exist_ok=True)
        probe_file = home / (".doctor-%d" % os.getpid())
        probe_file.write_text("ok", encoding="ascii")
        probe_file.unlink()
        checks.append(_check("data folder", "ok", str(home.resolve())))
    except OSError as exc:
        checks.append(_check("data folder", "fail", "%s cannot be written: %s" % (home, exc), "Pass another one with --home, or fix its permissions."))

    found = check_program(executor, "1 + 1", float(config.limits["startup_timeout_s"]))
    if found.get("error"):
        checks.append(_check("sandbox", "fail", "%s: %s" % (found["error"].get("type"), found["error"].get("message")), found["error"].get("hint", "")))
    else:
        checks.append(_check("sandbox", "ok", "a program compiles in the sandbox" + (" (custom executor.command)" if config.executor_command else "")))

    answers = probe(config, home)
    names: List[str] = []
    unknown: List[str] = []
    for namespace in config.connectors:  # in the config's order, whichever answered first
        answer = answers[namespace]
        kind = config.connectors[namespace]["type"]
        if "error" in answer:
            unknown.append(namespace)
            checks.append(_check("connector %s" % namespace, "fail", "%s: %s" % (kind, answer["error"]), answer.get("hint") or ""))
            continue
        tools = answer["tools"]
        from .catalog import assign_names  # here, not at the top: catalog imports connectors

        mapping = assign_names(t["name"] for t in tools)
        names.extend("%s.%s" % (namespace, sanitized) for sanitized in mapping.values())
        names.extend("%s.%s" % (namespace, original) for original in mapping)
        detail = "%s: %d tools in %.1fs" % (kind, len(tools), answer["seconds"])
        status, hint = "ok", ""
        renamed = sorted("%s is called as %s" % (o, s) for o, s in mapping.items() if o != s)
        size = sum(len(json.dumps(t.get("inputSchema"), default=str)) for t in tools)
        if answer["seconds"] > _SLOW_START_S:
            status, hint = "warn", "It is slow to start. Programs pay for that on every run from the command line; the MCP server mode starts it once."
        if size > _BIG_SCHEMAS:
            status = "warn"
            hint = (hint + " " if hint else "") + "Its schemas are %d kB together; describe() shortens the largest." % (size // 1000)
        if not tools:
            status, hint = "warn", "The server answered but lists no tools."
        if renamed:
            detail += "; " + "; ".join(renamed[:5]) + (" and %d more" % (len(renamed) - 5) if len(renamed) > 5 else "")
        checks.append(_check("connector %s" % namespace, status, detail, hint))

    policy = config.policy
    rules = len(policy.allow) + len(policy.ask) + len(policy.deny)
    if policy.mode == "observe":
        checks.append(_check("policy", "warn", "observe mode: decisions are logged and not enforced", 'Set "mode": "enforce" once the log shows what the rules would do.'))
    elif policy.default == "allow":
        checks.append(_check("policy", "warn", "default allow with %d rule(s): a tool that no rule names runs without asking" % rules,
                             'Safer: "default": "ask", and allow what the agent may do on its own.'))
    else:
        checks.append(_check("policy", "ok", "default %s, %d rule(s)" % (policy.default, rules)))
    for action, label in dead_rules(policy, names, unknown):
        worse = action == "deny"
        checks.append(_check(
            "policy rule", "warn", "the %s rule %r matches no tool" % (action, label),
            "A typo? %s Tool names are listed by: %s" % (
                "A deny rule that matches nothing denies nothing." if worse else "The tool it was meant for falls under another rule or the default.",
                launcher.command("search"),
            ),
        ))
    if not config.approval["require_terminal"]:
        checks.append(_check("approval", "warn", "approve works without a terminal: an agent with a shell could approve its own request",
                             'Remove "require_terminal": false unless approvals come from a script on purpose.'))

    if config.helpers_dir is not None:
        try:
            described = describe_helpers(load_helpers(config.helpers_dir, int(config.limits["max_value_bytes"])))
            broken = ["%s (%s)" % (item["module"], item["error"]) for item in described if item.get("error")]
            count = sum(len(item["functions"]) for item in described)
            if broken:
                checks.append(_check("helpers", "fail", "%s: does not parse: %s" % (config.helpers_dir, "; ".join(broken))))
            else:
                checks.append(_check("helpers", "ok", "%s: %d module(s), %d function(s)" % (config.helpers_dir, len(described), count)))
        except CodemodeError as exc:
            checks.append(_check("helpers", "fail", exc.message, exc.hint))

    if config.guidance:
        try:
            own = guidance() if guidance is not None else {"chars": 0, "source": None}
            budget = int(config.limits["max_guidance_chars"])
            detail = "%d chars from %s, added to the tool description and the instructions" % (own["chars"], own["source"])
            if own["chars"] > budget:
                checks.append(_check("guidance", "warn", "%s; cut to %d (limits.max_guidance_chars)" % (detail, budget),
                                     "Every agent reads this on every start. Keep it to what an agent needs before its first program, and "
                                     "leave the rest in a file the text points to."))
            else:
                checks.append(_check("guidance", "ok", detail))
        except CodemodeError as exc:
            checks.append(_check("guidance", "fail", exc.message, exc.hint))

    executions = home / "executions"
    logs = list(executions.glob("ex_*.jsonl")) if executions.is_dir() else []
    size = 0
    for folder in (executions, home / "blobs", home / "outputs"):
        if folder.is_dir():
            size += sum(p.stat().st_size for p in folder.iterdir() if p.is_file())
    detail = "%d execution(s), %.1f MB" % (len(logs), size / 1e6)
    if size > _BIG_RECORDS:
        checks.append(_check("records", "warn", detail, "Remove old ones with: %s" % launcher.command("prune", "--older-than", "7")))
    else:
        checks.append(_check("records", "ok", detail))

    root = config.root or Path.cwd()
    if (root / ".git").exists():
        try:
            listed = (root / ".gitignore").read_text(encoding="utf-8") if (root / ".gitignore").exists() else ""
            missing = gitignore_lines(listed, ignore_entries(root, home, config.work_dir))
        except OSError:
            missing = []
        if missing:
            checks.append(_check("gitignore", "warn", "%s not ignored" % ", ".join(missing), "Add them to .gitignore: run records do not belong in the repository."))
    failed = [c for c in checks if c["status"] == "fail"]
    return {"status": "error" if failed else "completed", "ok": not failed, "checks": checks}
