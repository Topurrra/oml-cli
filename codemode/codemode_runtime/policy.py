# SPDX-License-Identifier: MIT
"""Policy: which tool calls run, which wait for a human, which never run.

Pure logic. A decision is made per call, on the path the program used
(``connector.tool``) and the arguments it passed, because only the individual
call is concrete: a review of the program text cannot see the values it will
pass at run time.

Precedence is deny, then ask, then allow, then the default. The default is
``ask`` when the config does not set one, so a tool nobody thought about is
never run silently.

A rule is a glob over ``connector.tool``, or an object that also constrains
arguments::

    "deny": ["*.delete_*", {"tool": "git.run", "args": {"argv.0": "push"}}]

A tool is matched under two spellings: the sanitized name a program writes
(``files.delete_file``) and the name the server gave it
(``files.delete-file``). A rule written from the server's documentation must
not silently miss.
"""

from __future__ import annotations

import json
from dataclasses import dataclass
from fnmatch import fnmatchcase
from typing import Any, Dict, Iterable, Optional, Sequence, Tuple, Union

from .errors import ConfigError
from .jsonutil import lookup

ACTIONS = ("allow", "ask", "deny")
MODES = ("enforce", "observe")
_KEYS = {"mode", "default", "allow", "ask", "deny"}
_RULE_KEYS = {"tool", "args"}
_RULE_HINT = (
    'A rule is a glob over "connector.tool" such as "demo.read_*", or an object such as '
    '{"tool": "git.run", "args": {"argv.0": "push"}}.'
)


def arg_text(value: Any) -> str:
    """The text an argument pattern is matched against: a string as it is,
    anything else as compact JSON (``true``, ``null``, ``3``, ``["a","b"]``)."""
    if isinstance(value, str):
        return value
    try:
        return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True)
    except (TypeError, ValueError):
        return repr(value)


@dataclass(frozen=True)
class Rule:
    tool: str  # glob over "connector.tool"
    args: Tuple[Tuple[str, str], ...] = ()  # (dotted argument path, glob); all must match

    def matches(self, names: Iterable[str], args: Any) -> bool:
        if not any(fnmatchcase(name, self.tool) for name in names):
            return False
        for path, pattern in self.args:
            found, value = lookup(args, path)
            # A missing argument never matches: a rule that names an argument
            # says something about its value, and there is no value.
            if not found or not fnmatchcase(arg_text(value), pattern):
                return False
        return True

    @property
    def label(self) -> str:
        """What the audit log shows as the rule that decided."""
        if not self.args:
            return self.tool
        return "%s %s" % (self.tool, json.dumps(dict(self.args), sort_keys=True, ensure_ascii=True))

    def to_json(self) -> Any:
        return self.tool if not self.args else {"tool": self.tool, "args": dict(self.args)}


RuleLike = Union[str, Rule]


def _rule(value: RuleLike) -> Rule:
    return value if isinstance(value, Rule) else Rule(value)


def _names(path: str, alias: Optional[str]) -> Sequence[str]:
    """Every spelling a rule may use for one tool."""
    return (path,) if not alias or alias == path else (path, alias)


@dataclass(frozen=True)
class Decision:
    action: str  # allow | ask | deny
    rule: str  # the rule that matched, or "default"
    enforced: bool  # False in observe mode: logged, not applied

    @property
    def effective(self) -> str:
        """What the gateway actually does with the call."""
        return self.action if self.enforced else "allow"


@dataclass(frozen=True)
class Policy:
    mode: str = "enforce"
    default: str = "ask"
    allow: Tuple[RuleLike, ...] = ()
    ask: Tuple[RuleLike, ...] = ()
    deny: Tuple[RuleLike, ...] = ()

    def decide(self, path: str, args: Any = None, alias: Optional[str] = None) -> Decision:
        """Decide one call. ``alias`` is the same tool under the server's own
        name; a rule that matches either spelling applies."""
        enforced = self.mode == "enforce"
        names = _names(path, alias)
        # fnmatchcase, not fnmatch: fnmatch folds case on Windows, which would
        # make the same policy file mean different things on different hosts.
        for action, rules in (("deny", self.deny), ("ask", self.ask), ("allow", self.allow)):
            for raw in rules:
                rule = _rule(raw)
                if rule.matches(names, args):
                    return Decision(action, rule.label, enforced)
        return Decision(self.default, "default", enforced)

    def depends_on_args(self, path: str, alias: Optional[str] = None) -> bool:
        """True when a rule for this tool looks at arguments, so the decision
        shown for the tool alone may differ for a particular call."""
        names = _names(path, alias)
        for rules in (self.deny, self.ask, self.allow):
            for raw in rules:
                rule = _rule(raw)
                if rule.args and any(fnmatchcase(name, rule.tool) for name in names):
                    return True
        return False

    def to_dict(self) -> dict:
        return {
            "mode": self.mode,
            "default": self.default,
            "allow": [_rule(r).to_json() for r in self.allow],
            "ask": [_rule(r).to_json() for r in self.ask],
            "deny": [_rule(r).to_json() for r in self.deny],
        }


def _parse_rule(raw: Any, where: str) -> Rule:
    if isinstance(raw, str) and raw:
        return Rule(raw)
    if not isinstance(raw, dict):
        raise ConfigError("%s must be a list of rules; %r is not one" % (where, raw), hint=_RULE_HINT)
    unknown = sorted(set(raw) - _RULE_KEYS)
    if unknown:
        raise ConfigError("unknown key(s) in a %s rule: %s" % (where, ", ".join(str(k) for k in unknown)), hint=_RULE_HINT)
    tool = raw.get("tool")
    if not isinstance(tool, str) or not tool:
        raise ConfigError('a %s rule needs a "tool" pattern' % where, hint=_RULE_HINT)
    args = raw.get("args", {})
    if not isinstance(args, dict) or not all(isinstance(k, str) and k and isinstance(v, str) for k, v in args.items()):
        raise ConfigError(
            'the "args" of a %s rule must map argument paths to glob patterns, both strings' % where, hint=_RULE_HINT
        )
    return Rule(tool, tuple(sorted(args.items())))


def policy_from_dict(raw: Any) -> Policy:
    """Validate strictly: a typo here must be an error, never a silent allow."""
    if raw is None:
        return Policy()
    if not isinstance(raw, dict):
        raise ConfigError("policy must be an object", hint='Example: {"default": "ask", "allow": ["demo.read_*"]}')
    unknown = sorted(set(raw) - _KEYS)
    if unknown:
        raise ConfigError(
            "unknown policy key(s): %s" % ", ".join(unknown),
            hint="Valid keys: %s" % ", ".join(sorted(_KEYS)),
        )
    mode = raw.get("mode", "enforce")
    if mode not in MODES:
        raise ConfigError("policy.mode must be one of %s, got %r" % (", ".join(MODES), mode))
    default = raw.get("default", "ask")
    if default not in ACTIONS:
        raise ConfigError("policy.default must be one of %s, got %r" % (", ".join(ACTIONS), default))
    lists: Dict[str, Tuple[Rule, ...]] = {}
    for key in ACTIONS:
        rules = raw.get(key, [])
        if not isinstance(rules, list):
            raise ConfigError("policy.%s must be a list of rules" % key, hint=_RULE_HINT)
        lists[key] = tuple(_parse_rule(rule, "policy.%s" % key) for rule in rules)
    return Policy(mode=mode, default=default, **lists)
