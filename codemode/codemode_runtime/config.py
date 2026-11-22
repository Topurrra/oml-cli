# SPDX-License-Identifier: MIT
"""Configuration: one JSON file, validated strictly.

Unknown keys are errors that list the valid ones. A config that is accepted
but half ignored is the worst failure for an agent editing it: nothing tells
it the edit did nothing.

Resolution order for the file: ``--config``, then ``$CODEMODE_CONFIG``, then
``./codemode.json``, then the built-in demo config, so the application runs
with no setup at all.
"""

from __future__ import annotations

import json
import os
import re
from dataclasses import dataclass, field
from fnmatch import fnmatchcase
from pathlib import Path
from typing import Any, Dict, List, Mapping, Optional, Tuple

from . import launcher
from .errors import ConfigError
from .policy import Policy, policy_from_dict
from .textio import read_text

TOP_KEYS = {"connectors", "policy", "approval", "limits", "executor", "llm", "agent", "helpers", "retention"}

# How long execution records are kept. Every execution is a file, and a
# project with busy agents writes hundreds a day. Records past either limit
# are removed now and then; 0 turns a limit off.
DEFAULT_RETENTION: Dict[str, float] = {"max_age_days": 30, "max_executions": 2000}

# Approvals. ``require_terminal`` refuses ``approve`` and ``--interactive``
# when stdin is not a terminal: an agent that drives the CLI through a shell
# tool has none, so it cannot approve its own request by accident. ``ttl_s``
# is how long a paused execution can still be approved (0: for ever).
DEFAULT_APPROVAL: Dict[str, Any] = {"require_terminal": True, "ttl_s": 86400}

DEFAULT_LIMITS: Dict[str, float] = {
    "timeout_s": 60,  # wall clock for one pass of one execution
    "call_timeout_s": 30,  # one tool call
    "startup_timeout_s": 20,  # executor start, connector start
    "max_calls": 200,  # tool calls per execution
    "max_value_bytes": 1_000_000,  # one argument object, tool result or step value
    "max_result_chars": 24_000,  # result text returned to the caller
    "max_log_chars": 8_000,  # log text returned to the caller
    "max_state_bytes": 1_000_000,  # one session's state
    "catalog_cache_s": 300,  # how long a server's tool list is trusted from disk; 0 turns the cache off
}
_MAY_BE_ZERO = {"catalog_cache_s"}

# Names the sandbox defines. A connector with one of these names would be
# unreachable or would shadow the API.
RESERVED_NAMES = {
    "tools", "call", "parallel", "search", "describe", "state", "step", "run",
    "snippets", "args", "ToolError",
}

# Keys every connector type takes: how long one of its calls may run, how
# long it may take to start, and how many of its calls may run at once (for a
# server that handles one request at a time, 1 keeps the timeout of a queued
# call from running while it waits).
COMMON_CONNECTOR_KEYS = {"type", "call_timeout_s", "startup_timeout_s", "max_concurrency"}
_MCP_KEYS = COMMON_CONNECTOR_KEYS | {"secrets", "parse_json_text"}
CONNECTOR_KEYS = {
    "demo": COMMON_CONNECTOR_KEYS,
    "chaos": COMMON_CONNECTOR_KEYS,
    "mcp-stdio": _MCP_KEYS | {"command", "env", "cwd"},
    "mcp-http": _MCP_KEYS | {"url", "headers"},  # streamable HTTP
    "mcp-sse": _MCP_KEYS | {"url", "headers"},  # HTTP with server-sent events (the 2024-11-05 transport)
    # A project's own command lines as tools, for a project with no server.
    "commands": COMMON_CONNECTOR_KEYS | {"tools", "cwd", "env", "inherit_env", "max_output_chars", "secrets"},
}
EXECUTOR_KEYS = {"command", "env_passthrough"}
LLM_KEYS = {"base_url", "model", "api_key", "max_turns", "timeout_s"}

DEMO_CONFIG: Dict[str, Any] = {
    "connectors": {"demo": {"type": "demo"}},
    "policy": {
        "mode": "enforce",
        "default": "ask",
        "allow": ["demo.list_notes", "demo.read_note", "demo.search_notes"],
        "deny": ["demo.delete_note"],
    },
    # The demo store is a throwaway notes file; its quick start approves from
    # scripts and tests. A project config keeps the default, which is true.
    "approval": {"require_terminal": False},
}

_ENV_REF = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")


@dataclass
class Config:
    connectors: Dict[str, Dict[str, Any]]
    policy: Policy
    limits: Dict[str, float]
    approval: Dict[str, Any] = field(default_factory=lambda: dict(DEFAULT_APPROVAL))
    retention: Dict[str, float] = field(default_factory=lambda: dict(DEFAULT_RETENTION))
    call_timeouts: List[Tuple[str, float]] = field(default_factory=list)
    executor_command: Optional[List[str]] = None
    executor_env_passthrough: List[str] = field(default_factory=list)
    llm: Dict[str, Any] = field(default_factory=dict)
    agent: Optional[str] = None
    source: str = "built-in demo config"
    path: Optional[Path] = None  # the file this came from; None for the built-in config
    # The project root, when the config was found by searching upward from a
    # folder below it. Relative paths in the config (a server's cwd, its
    # script) and the default data directory are then read from here, because
    # the caller's own directory is somewhere inside the project. None when
    # the config was named or sits in the current directory: paths are then
    # relative to the current directory, as they always were.
    root: Optional[Path] = None
    helpers: Optional[str] = None  # a folder of Python files that programs call as helpers.<file>.<function>()

    @property
    def helpers_dir(self) -> Optional[Path]:
        if not self.helpers:
            return None
        return (self.root / self.helpers) if self.root is not None else Path(self.helpers)

    def call_timeout(self, connector: str, path: str, alias: str = "") -> float:
        """How long one call of this tool may run: the first matching
        ``limits.call_timeouts`` pattern, else the connector's
        ``call_timeout_s``, else ``limits.call_timeout_s``. One global limit
        cannot fit both a status query and a half-hour build."""
        for pattern, seconds in self.call_timeouts:
            if fnmatchcase(path, pattern) or (alias and fnmatchcase(alias, pattern)):
                return seconds
        spec = self.connectors.get(connector) or {}
        return float(spec.get("call_timeout_s") or self.limits["call_timeout_s"])

    def describe(self) -> dict:
        """The resolved config for the ``config`` command. Credentials are
        never printed: an API key is reported as set, and a literal value in
        a connector's ``env`` is hidden (a ``${NAME}`` reference is shown,
        because it is not the secret)."""
        llm = dict(self.llm)
        if llm.get("api_key"):
            llm["api_key"] = "(set)"
        connectors = {}
        for name, spec in self.connectors.items():
            shown = dict(spec)
            for key in ("env", "headers"):
                if isinstance(spec.get(key), dict):
                    shown[key] = {k: (v if _ENV_REF.search(v) else "(hidden)") for k, v in spec[key].items()}
            connectors[name] = shown
        limits: Dict[str, Any] = dict(self.limits)
        if self.call_timeouts:
            limits["call_timeouts"] = dict(self.call_timeouts)
        return {
            "source": self.source,
            "connectors": connectors,
            "policy": self.policy.to_dict(),
            "approval": self.approval,
            "retention": self.retention,
            "limits": limits,
            "executor": {
                "command": self.executor_command,
                "env_passthrough": self.executor_env_passthrough,
            },
            "llm": llm,
            "agent": self.agent,
            "helpers": str(self.helpers_dir) if self.helpers_dir is not None else None,
        }


def expand_env(value: str, where: str, env: Optional[Mapping[str, str]] = None) -> str:
    """Replace ``${NAME}`` from the host environment. Secrets are written in
    the config as references so the file can be committed and so they are
    resolved on the host, never inside the sandbox."""
    env = os.environ if env is None else env

    def replace(match: "re.Match[str]") -> str:
        name = match.group(1)
        if name not in env:
            raise ConfigError(
                "%s refers to ${%s}, which is not set" % (where, name),
                hint="Export %s in the environment of the process that runs the runtime." % name,
            )
        return env[name]

    return _ENV_REF.sub(replace, value)


def _check_keys(section: str, raw: Mapping[str, Any], valid: set) -> None:
    unknown = sorted(set(raw) - valid)
    if unknown:
        raise ConfigError(
            "unknown key(s) in %s: %s" % (section, ", ".join(unknown)),
            hint="Valid keys: %s" % ", ".join(sorted(valid)),
        )


def _string_list(value: Any, where: str) -> List[str]:
    if not isinstance(value, list) or not value or not all(isinstance(v, str) and v for v in value):
        raise ConfigError("%s must be a non-empty list of strings" % where)
    return list(value)


def _connectors(raw: Any) -> Dict[str, Dict[str, Any]]:
    if not isinstance(raw, dict) or not raw:
        raise ConfigError(
            "connectors must be a non-empty object",
            hint='Example: {"connectors": {"demo": {"type": "demo"}}}',
        )
    out: Dict[str, Dict[str, Any]] = {}
    for name, spec in raw.items():
        if not name.isidentifier() or name.startswith("_") or name in RESERVED_NAMES:
            raise ConfigError(
                "connector name %r is not usable" % name,
                hint="Use a plain identifier such as 'github'. Reserved: %s." % ", ".join(sorted(RESERVED_NAMES)),
            )
        if not isinstance(spec, dict):
            raise ConfigError("connectors.%s must be an object" % name)
        kind = spec.get("type")
        if kind not in CONNECTOR_KEYS:
            raise ConfigError(
                "connectors.%s.type is %r" % (name, kind),
                hint="Valid types: %s" % ", ".join(sorted(CONNECTOR_KEYS)),
            )
        _check_keys("connectors.%s" % name, spec, CONNECTOR_KEYS[kind])
        for key in ("call_timeout_s", "startup_timeout_s"):
            if key in spec and (isinstance(spec[key], bool) or not isinstance(spec[key], (int, float)) or spec[key] <= 0):
                raise ConfigError("connectors.%s.%s must be a positive number, got %r" % (name, key, spec[key]))
        if "max_concurrency" in spec and (isinstance(spec["max_concurrency"], bool) or not isinstance(spec["max_concurrency"], int) or spec["max_concurrency"] < 1):
            raise ConfigError("connectors.%s.max_concurrency must be a whole number, 1 or more; got %r" % (name, spec["max_concurrency"]))
        if "secrets" in spec and (not isinstance(spec["secrets"], list) or not all(isinstance(v, str) and v for v in spec["secrets"])):
            raise ConfigError(
                "connectors.%s.secrets must be a list of env names" % name,
                hint="Name the entries of env whose values must never appear in a result or a log.",
            )
        if "parse_json_text" in spec and not isinstance(spec["parse_json_text"], bool):
            raise ConfigError("connectors.%s.parse_json_text must be true or false" % name)
        if kind == "mcp-stdio":
            _string_list(spec.get("command"), "connectors.%s.command" % name)
            env = spec.get("env", {})
            if not isinstance(env, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in env.items()):
                raise ConfigError("connectors.%s.env must map strings to strings" % name)
            if "cwd" in spec and not isinstance(spec["cwd"], str):
                raise ConfigError("connectors.%s.cwd must be a string" % name)
        if kind in ("mcp-http", "mcp-sse"):
            url = spec.get("url")
            if not isinstance(url, str) or not url.lower().startswith(("http://", "https://")):
                raise ConfigError(
                    "connectors.%s.url must be an http:// or https:// address, got %r" % (name, url),
                    hint='Example: {"type": "%s", "url": "https://example.com/mcp", "headers": {"Authorization": "Bearer ${TOKEN}"}}' % kind,
                )
            headers = spec.get("headers", {})
            if not isinstance(headers, dict) or not all(isinstance(k, str) and isinstance(v, str) for k, v in headers.items()):
                raise ConfigError("connectors.%s.headers must map strings to strings" % name)
        if kind == "commands":
            from .connectors.commands import validate_spec

            validate_spec(name, spec)
        out[name] = dict(spec)
    return out


def _limits(raw: Any) -> "tuple[Dict[str, float], List[Tuple[str, float]]]":
    """The numeric limits, and ``call_timeouts``: per-tool time limits as
    (glob over "connector.tool", seconds), first match wins."""
    limits = dict(DEFAULT_LIMITS)
    if raw is None:
        return limits, []
    if not isinstance(raw, dict):
        raise ConfigError("limits must be an object")
    _check_keys("limits", raw, set(DEFAULT_LIMITS) | {"call_timeouts"})
    timeouts: List[Tuple[str, float]] = []
    for key, value in raw.items():
        if key == "call_timeouts":
            if not isinstance(value, dict):
                raise ConfigError(
                    "limits.call_timeouts must be an object", hint='Example: {"call_timeouts": {"ci.run_build": 1800, "ci.*": 300}}'
                )
            for pattern, seconds in value.items():
                if not pattern or isinstance(seconds, bool) or not isinstance(seconds, (int, float)) or seconds <= 0:
                    raise ConfigError("limits.call_timeouts[%r] must be a positive number of seconds, got %r" % (pattern, seconds))
                timeouts.append((pattern, float(seconds)))
            continue
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0 or (value == 0 and key not in _MAY_BE_ZERO):
            raise ConfigError("limits.%s must be a positive number, got %r" % (key, value))
        limits[key] = value
    return limits, timeouts


def _approval(raw: Any) -> Dict[str, Any]:
    approval = dict(DEFAULT_APPROVAL)
    if raw is None:
        return approval
    if not isinstance(raw, dict):
        raise ConfigError("approval must be an object", hint='Example: {"approval": {"require_terminal": true, "ttl_s": 86400}}')
    _check_keys("approval", raw, set(DEFAULT_APPROVAL))
    if "require_terminal" in raw:
        if not isinstance(raw["require_terminal"], bool):
            raise ConfigError("approval.require_terminal must be true or false, got %r" % (raw["require_terminal"],))
        approval["require_terminal"] = raw["require_terminal"]
    if "ttl_s" in raw:
        ttl = raw["ttl_s"]
        if isinstance(ttl, bool) or not isinstance(ttl, (int, float)) or ttl < 0:
            raise ConfigError("approval.ttl_s must be a number of seconds, 0 for no limit; got %r" % (ttl,))
        approval["ttl_s"] = ttl
    return approval


def _retention(raw: Any) -> Dict[str, float]:
    retention = dict(DEFAULT_RETENTION)
    if raw is None:
        return retention
    if not isinstance(raw, dict):
        raise ConfigError("retention must be an object", hint='Example: {"retention": {"max_age_days": 30, "max_executions": 2000}}')
    _check_keys("retention", raw, set(DEFAULT_RETENTION))
    for key, value in raw.items():
        if isinstance(value, bool) or not isinstance(value, (int, float)) or value < 0:
            raise ConfigError("retention.%s must be a number, 0 for no limit; got %r" % (key, value))
        retention[key] = value
    return retention


def _executor(raw: Any) -> "tuple[Optional[List[str]], List[str]]":
    if raw is None:
        return None, []
    if not isinstance(raw, dict):
        raise ConfigError("executor must be an object")
    _check_keys("executor", raw, EXECUTOR_KEYS)
    command = raw.get("command")
    if command is not None:
        command = _string_list(command, "executor.command")
        if not any("{bootstrap}" in part for part in command):
            raise ConfigError(
                "executor.command has no {bootstrap} placeholder",
                hint='The sandbox must run "python -I -u -c {bootstrap}" somewhere. Example: '
                '["docker", "run", "--rm", "-i", "--network", "none", "python:3.12-alpine", '
                '"python", "-I", "-u", "-c", "{bootstrap}"]',
            )
    passthrough = raw.get("env_passthrough", [])
    if not isinstance(passthrough, list) or not all(isinstance(v, str) for v in passthrough):
        raise ConfigError("executor.env_passthrough must be a list of variable names")
    return command, list(passthrough)


def _llm(raw: Any, env: Mapping[str, str]) -> Dict[str, Any]:
    """The model endpoint. Anything that speaks the OpenAI chat-completions
    wire format works: hosted APIs, gateways, local servers. Environment
    variables win over the file so one config serves several endpoints."""
    if raw is None:
        raw = {}
    if not isinstance(raw, dict):
        raise ConfigError("llm must be an object")
    _check_keys("llm", raw, LLM_KEYS)
    llm = dict(raw)
    # Only CODEMODE_* names are read. Falling back to another provider's
    # variable would send that provider's key to whatever endpoint is
    # configured here.
    for key, name in (("base_url", "CODEMODE_BASE_URL"), ("model", "CODEMODE_MODEL"), ("api_key", "CODEMODE_API_KEY")):
        if env.get(name):
            llm[key] = env[name]
    for key in ("base_url", "model", "api_key"):
        if key in llm and not isinstance(llm[key], str):
            raise ConfigError("llm.%s must be a string" % key)
    # llm.api_key may be a ${NAME} reference. It is expanded when the agent
    # loop starts, not here, so a missing key cannot break unrelated commands.
    for key in ("max_turns", "timeout_s"):
        if key in llm and (isinstance(llm[key], bool) or not isinstance(llm[key], (int, float)) or llm[key] <= 0):
            raise ConfigError("llm.%s must be a positive number" % key)
    return llm


def config_from_dict(raw: Any, source: str, env: Optional[Mapping[str, str]] = None) -> Config:
    env = os.environ if env is None else env
    if not isinstance(raw, dict):
        raise ConfigError("%s: the config must be a JSON object" % source)
    _check_keys("the config", raw, TOP_KEYS)
    command, passthrough = _executor(raw.get("executor"))
    agent = raw.get("agent")
    if agent is not None and not isinstance(agent, str):
        raise ConfigError("agent must be a string")
    limits, call_timeouts = _limits(raw.get("limits"))
    helpers = raw.get("helpers")
    if helpers is not None and (not isinstance(helpers, str) or not helpers):
        raise ConfigError(
            "helpers must be the name of a folder", hint='Example: "helpers": "codemode_helpers", a folder of .py files next to the config.'
        )
    return Config(
        helpers=helpers,
        retention=_retention(raw.get("retention")),
        connectors=_connectors(raw.get("connectors")),
        policy=policy_from_dict(raw.get("policy")),
        limits=limits,
        call_timeouts=call_timeouts,
        approval=_approval(raw.get("approval")),
        executor_command=command,
        executor_env_passthrough=passthrough,
        llm=_llm(raw.get("llm"), env),
        agent=agent,
        source=source,
    )


CONFIG_NAME = "codemode.json"


def find_config_upward(start: Path, home: Optional[Path] = None) -> Optional[Path]:
    """Look for the project's config in the folders above ``start``, the way
    version control finds its repository. The search does not leave the
    repository (it stops at the folder that holds ``.git``) and does not go
    above the user's home folder: a config found further up belongs to
    something else."""
    try:
        folder = start.resolve()
        home = (home or Path.home()).resolve()
    except (OSError, RuntimeError):
        return None
    while True:
        if (folder / ".git").exists() or folder == home or folder.parent == folder:
            return None
        folder = folder.parent
        if (folder / CONFIG_NAME).is_file():
            return folder / CONFIG_NAME


def load_config(path: Optional[str] = None, env: Optional[Mapping[str, str]] = None) -> Config:
    env = os.environ if env is None else env
    explicit = path or env.get("CODEMODE_CONFIG")
    candidate = Path(explicit) if explicit else Path(CONFIG_NAME)
    root: Optional[Path] = None
    if not candidate.is_file() and not explicit:
        found = find_config_upward(Path.cwd())
        if found is not None:
            candidate, root = found, found.parent
    if not candidate.is_file():
        if explicit:
            raise ConfigError(
                "config file not found: %s" % candidate,
                hint="Check the path, or write a config for this project with: %s" % launcher.command("init"),
            )
        return config_from_dict(DEMO_CONFIG, "built-in demo config", env)
    try:
        raw = json.loads(read_text(candidate))  # a BOM from an editor or a shell redirect is not an error
    except (OSError, ValueError) as exc:
        raise ConfigError(
            "%s is not readable JSON: %s" % (candidate, exc),
            hint="Fix the file, or write a new one with: %s" % launcher.command("init"),
        )
    config = config_from_dict(raw, str(candidate), env)
    config.path = candidate.resolve()
    config.root = root.resolve() if root is not None else None
    return config


def resolve_home(home: Optional[str] = None, env: Optional[Mapping[str, str]] = None, config: Optional[Config] = None) -> Path:
    """The data directory: executions, session state, snippets. By default
    ``.codemode`` in the project root: the current directory, or the folder
    the config was found in when the caller is below it."""
    env = os.environ if env is None else env
    explicit = home or env.get("CODEMODE_HOME")
    if explicit:
        return Path(explicit)
    if config is not None and config.root is not None:
        return config.root / ".codemode"
    return Path(".codemode")


def resolve_agent(flag: Optional[str], config: Config, env: Optional[Mapping[str, str]] = None) -> str:
    """Who is acting. Always decided on the host; sandbox code cannot set it."""
    env = os.environ if env is None else env
    return flag or env.get("CODEMODE_AGENT") or config.agent or "anonymous"
