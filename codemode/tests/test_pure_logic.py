# SPDX-License-Identifier: MIT
"""Unit cases for the pure modules: many inputs, no I/O.

Run from the project root:  python -m unittest discover -s tests -v

Every case comes from ``tests/FAILURE_MODES.md`` and quotes the ID it covers.
The cases were listed there before the modules were written; they are the
ways these functions can go wrong, not a walk through the code. Everything
with a process, a file or a pipe in it is tested end to end in ``e2e.py``
instead.
"""

from __future__ import annotations

import ast
import json
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))  # for sync_docs, however the cases are started

from codemode_runtime.catalog import assign_names, fit_schema, sanitize_name  # noqa: E402
from codemode_runtime.config import config_from_dict, expand_env, find_config_upward  # noqa: E402
from codemode_runtime.connectors import looks_secret, redact, value_looks_secret  # noqa: E402
from codemode_runtime.connectors.commands import (  # noqa: E402
    _Capture, check_args, cut_middle, decode_output, expand_command, required_params, validate_spec,
)
from codemode_runtime.connectors.mcp_common import Mailbox, progress_text, unwrap_result  # noqa: E402
from codemode_runtime.connectors.mcp_http import iter_sse  # noqa: E402
from codemode_runtime.connectors.mcp_stdio import resolve_env  # noqa: E402
from codemode_runtime.errors import ConfigError, ToolFailure  # noqa: E402
from codemode_runtime.helpers import describe_helpers  # noqa: E402
from codemode_runtime.instructions import instructions  # noqa: E402
from codemode_runtime.project import (  # noqa: E402
    connector_name, connectors_from_servers, dead_rules, gitignore_lines, read_server_list, starting_policy,
)
from codemode_runtime.jsonutil import canonical, fingerprint  # noqa: E402
from codemode_runtime.policy import Policy, arg_text, lookup, policy_from_dict  # noqa: E402
from codemode_runtime.schema_sig import signature, type_of  # noqa: E402
from codemode_runtime import sandbox_runner as sandbox  # noqa: E402
from codemode_runtime.textio import decode_text  # noqa: E402
from codemode_runtime.truncate import BoundedLog, budget_result, shape_of, truncate_text  # noqa: E402


class SchemaSignature(unittest.TestCase):
    def test_S1_schema_that_is_not_an_object_never_raises(self):
        for schema in (None, True, False, [], "string", 7):
            self.assertEqual(signature("a.b", schema), "a.b(**args: Any) -> Any")
            self.assertEqual(type_of(schema), "Any")

    def test_S2_type_given_as_a_list(self):
        self.assertEqual(type_of({"type": ["string", "null"]}), "str | None")
        self.assertEqual(type_of({"type": ["null", "integer"]}), "int | None")

    def test_S3_properties_without_type(self):
        self.assertEqual(type_of({"properties": {"a": {}}}), "dict")
        self.assertEqual(signature("a.b", {"properties": {"x": {"type": "integer"}}}), "a.b(x: int = ...) -> Any")

    def test_S4_required_name_missing_from_properties(self):
        self.assertEqual(signature("a.b", {"type": "object", "properties": {}, "required": ["ghost"]}), "a.b(ghost: Any) -> Any")

    def test_S5_required_first_each_group_in_schema_order(self):
        schema = {
            "type": "object",
            "properties": {"opt1": {"type": "string"}, "req2": {"type": "integer"}, "opt2": {"type": "boolean"}, "req1": {"type": "number"}},
            "required": ["req1", "req2"],
        }
        self.assertEqual(signature("a.b", schema), "a.b(req2: int, req1: float, opt1: str = ..., opt2: bool = ...) -> Any")

    def test_S6_names_that_cannot_be_parameters(self):
        schema = {
            "type": "object",
            "properties": {"ok": {"type": "string"}, "weird-name": {"type": "string"}, "2fa": {"type": "integer"},
                           "has space": {"type": "boolean"}, "class": {"type": "string"}, "from": {"type": "string"}},
            "required": ["ok", "class"],
        }
        text = signature("a.b", schema)
        self.assertEqual(text, "a.b(ok: str, **{'class': str, 'weird-name': str, '2fa': int, 'has space': bool, 'from': str}) -> Any")
        # Whatever is outside the ** dict must be a parameter list Python accepts.
        named = text[len("a.b("): text.index(", **{")]
        ast.parse("def f(%s): pass" % named)

    def test_S7_enums(self):
        self.assertEqual(type_of({"enum": ["a", 1, None, True]}), "Literal['a', 1, None, True]")
        self.assertEqual(type_of({"enum": ['say "hi"', "it's"]}), "Literal['say \"hi\"', \"it's\"]")
        self.assertEqual(type_of({"enum": list(range(20))}), "Literal[0, 1, 2, 3, 4, 5, ...]")
        self.assertEqual(type_of({"enum": [{"nested": 1}]}), "Any")
        self.assertEqual(type_of({"enum": [], "type": "string"}), "str")

    def test_S8_arrays(self):
        self.assertEqual(type_of({"type": "array", "items": {"type": "array", "items": {"type": "integer"}}}), "list[list[int]]")
        self.assertEqual(type_of({"type": "array"}), "list[Any]")
        self.assertEqual(type_of({"items": {"type": "string"}}), "list[str]")
        self.assertEqual(type_of({"type": "array", "items": True}), "list[Any]")

    def test_S9_unions(self):
        self.assertEqual(type_of({"anyOf": [{"type": "string"}, {"type": "null"}, {"type": "string"}]}), "str | None")
        self.assertEqual(type_of({"oneOf": [{"type": "null"}, {"type": "integer"}, {"type": "number"}]}), "int | float | None")
        self.assertEqual(type_of({"anyOf": [{"type": "string"}, {}]}), "Any")
        self.assertEqual(type_of({"allOf": [{"type": "integer"}]}), "int")
        self.assertEqual(type_of({"allOf": [{"properties": {"a": {}}}, {"properties": {"b": {}}}]}), "dict")
        self.assertEqual(type_of({"anyOf": []}), "Any")

    def test_S10_refs(self):
        root = {
            "type": "object",
            "properties": {"node": {"$ref": "#/$defs/Node"}, "old": {"$ref": "#/definitions/Old"}, "gone": {"$ref": "#/$defs/Missing"},
                           "remote": {"$ref": "https://example.com/schema.json"}, "bad": {"$ref": 5}},
            "$defs": {"Node": {"type": "object", "properties": {"next": {"$ref": "#/$defs/Node"}}}, "Loop": {"$ref": "#/$defs/Loop"}},
            "definitions": {"Old": {"type": "array", "items": {"type": "string"}}},
        }
        self.assertEqual(signature("a.b", root), "a.b(node: dict = ..., old: list[str] = ..., gone: Any = ..., remote: Any = ..., bad: Any = ...) -> Any")
        self.assertEqual(type_of({"$ref": "#/$defs/Loop", "$defs": root["$defs"]}), "Any")
        tree = {"$defs": {"T": {"type": "array", "items": {"$ref": "#/$defs/T"}}}, "$ref": "#/$defs/T"}
        self.assertTrue(type_of(tree).startswith("list["))  # terminates

    def test_S11_very_deep_schema_terminates_with_bounded_output(self):
        schema = {"type": "string"}
        for _ in range(500):
            schema = {"type": "array", "items": schema}
        text = type_of(schema)
        self.assertLess(len(text), 100)
        self.assertIn("Any", text)

    def test_S12_many_parameters_stay_short_in_compact_form(self):
        schema = {"type": "object", "properties": {"p%d" % i: {"type": "string"} for i in range(300)}}
        compact = signature("a.b", schema, max_params=8)
        self.assertLess(len(compact), 200)
        self.assertTrue(compact.endswith(", ...) -> Any"))
        self.assertEqual(signature("a.b", schema).count("p"), 300)

    def test_S13_const(self):
        self.assertEqual(type_of({"const": "fixed"}), "Literal['fixed']")
        self.assertEqual(type_of({"const": None}), "Literal[None]")

    def test_S14_type_that_is_not_a_known_string(self):
        for kind in (5, {"a": 1}, "file", "", "STRING"):
            self.assertEqual(type_of({"type": kind}), "Any")
        self.assertEqual(type_of({"type": ["string", 5]}), "Any")

    def test_S15_typed_additional_properties(self):
        self.assertEqual(type_of({"type": "object", "additionalProperties": {"type": "integer"}}), "dict[str, int]")
        self.assertEqual(type_of({"type": "object", "additionalProperties": True}), "dict")
        self.assertEqual(signature("a.b", {"type": "object", "properties": {}, "additionalProperties": False}), "a.b() -> Any")
        self.assertEqual(signature("a.b", {"type": "object"}), "a.b(**args: Any) -> Any")

    def test_S16_output_schema(self):
        self.assertEqual(signature("a.b", {"type": "object", "properties": {}}, None), "a.b() -> Any")
        self.assertEqual(signature("a.b", {"type": "object", "properties": {}}, {"type": "array", "items": {"type": "object"}}), "a.b() -> list[dict]")

    def test_defaults_are_shown_only_when_scalar(self):
        schema = {"type": "object", "properties": {"n": {"type": "integer", "default": 10}, "s": {"type": "string", "default": "x"},
                                                   "l": {"type": "array", "default": []}, "z": {"type": "string", "default": None}}}
        self.assertEqual(signature("a.b", schema), "a.b(n: int = 10, s: str = 'x', l: list[Any] = ..., z: str = None) -> Any")


class NameSanitizing(unittest.TestCase):
    def test_N1_separators(self):
        self.assertEqual(sanitize_name("notion-search"), "notion_search")
        self.assertEqual(sanitize_name("files/read.text now"), "files_read_text_now")
        self.assertEqual(sanitize_name("a--b__c"), "a_b_c")

    def test_N2_leading_digit(self):
        self.assertEqual(sanitize_name("2fa"), "t_2fa")
        self.assertEqual(sanitize_name("-9lives"), "t_9lives")

    def test_N3_keywords(self):
        for word in ("import", "class", "None", "True", "lambda"):
            self.assertEqual(sanitize_name(word), word + "_")

    def test_N4_empty_or_symbols_only(self):
        for name in ("", "---", "___", "!!!", " "):
            self.assertEqual(sanitize_name(name), "tool")

    def test_N5_collisions_resolve_the_same_whatever_the_listing_order(self):
        names = ["a-b", "a_b", "a.b", "other"]
        forward = assign_names(names)
        backward = assign_names(list(reversed(names)))
        self.assertEqual(forward, backward)
        self.assertEqual(len(set(forward.values())), 4)
        self.assertEqual(forward, {"a-b": "a_b", "a.b": "a_b_2", "a_b": "a_b_3", "other": "other"})

    def test_N5_a_suffixed_name_cannot_steal_a_real_one(self):
        mapping = assign_names(["x-y", "x_y", "x_y_2"])
        self.assertEqual(len(set(mapping.values())), 3)

    def test_N6_underscores_and_dunders(self):
        self.assertEqual(sanitize_name("__class__"), "class_")
        self.assertEqual(sanitize_name("_private"), "private")
        self.assertEqual(sanitize_name("__"), "tool")

    def test_N7_result_is_always_an_identifier_not_starting_with_underscore(self):
        samples = ["", "a", "9", "-", "ünïcödé", "日本語", "a b", "def", "__init__", "x" * 500, "\n\t", "a\u0000b", "é1", "1é"]
        for name in samples:
            out = sanitize_name(name)
            self.assertTrue(out.isidentifier(), (name, out))
            self.assertFalse(out.startswith("_"), (name, out))
            ast.parse("tools.ns.%s" % out)


class PolicyDecisions(unittest.TestCase):
    def test_L1_precedence(self):
        policy = Policy(default="allow", allow=("demo.*",), ask=("demo.write_*",), deny=("demo.write_secret",))
        self.assertEqual(policy.decide("demo.read").action, "allow")
        self.assertEqual(policy.decide("demo.write_note").action, "ask")
        self.assertEqual(policy.decide("demo.write_secret").action, "deny")
        self.assertEqual(policy.decide("demo.write_secret").rule, "demo.write_secret")

    def test_L2_default_is_ask_when_unset(self):
        self.assertEqual(policy_from_dict({}).decide("anything.at_all").action, "ask")
        self.assertEqual(policy_from_dict(None).decide("x.y").rule, "default")
        self.assertEqual(policy_from_dict({"allow": ["a.b"]}).decide("a.c").action, "ask")

    def test_L3_matching_is_case_sensitive_everywhere(self):
        policy = Policy(default="deny", allow=("Demo.Read",))
        self.assertEqual(policy.decide("Demo.Read").action, "allow")
        self.assertEqual(policy.decide("demo.read").action, "deny")

    def test_L4_star_crosses_the_dot(self):
        self.assertEqual(Policy(default="allow", deny=("*delete*",)).decide("github.delete_repo").action, "deny")
        self.assertEqual(Policy(default="deny", allow=("*",)).decide("a.b").action, "allow")
        self.assertEqual(Policy(default="deny", allow=("a.*",)).decide("ab.c").action, "deny")

    def test_L5_misspellings_are_errors(self):
        for raw in ({"mode": "enforced"}, {"default": "alow"}, {"defualt": "allow"}, {"allow": "demo.*"}, {"deny": None}, "allow", ["allow"]):
            with self.assertRaises(ConfigError, msg=repr(raw)):
                policy_from_dict(raw)

    def test_L6_patterns_must_be_non_empty_strings(self):
        for patterns in ([7], [None], [""], [["a"]], [True]):
            with self.assertRaises(ConfigError, msg=repr(patterns)):
                policy_from_dict({"allow": patterns})

    def test_L7_unbalanced_bracket_does_not_raise(self):
        policy = Policy(default="deny", allow=("demo.[read",))
        self.assertEqual(policy.decide("demo.[read").action, "allow")
        self.assertEqual(policy.decide("demo.read").action, "deny")

    def test_L8_observe_mode_decides_the_same_and_marks_it_not_enforced(self):
        enforce = Policy(mode="enforce", default="ask", deny=("a.b",))
        observe = Policy(mode="observe", default="ask", deny=("a.b",))
        for path in ("a.b", "a.c"):
            self.assertEqual(enforce.decide(path).action, observe.decide(path).action)
            self.assertTrue(enforce.decide(path).enforced)
            self.assertFalse(observe.decide(path).enforced)
            self.assertEqual(observe.decide(path).effective, "allow")
        self.assertEqual(enforce.decide("a.b").effective, "deny")


class PolicyRules(unittest.TestCase):
    def test_L9_a_rule_in_the_servers_spelling_applies_to_the_sanitized_name(self):
        policy = policy_from_dict({"default": "allow", "deny": ["files.delete-file"], "ask": ["files.write file"]})
        self.assertEqual(policy.decide("files.delete_file", {}, alias="files.delete-file").action, "deny")
        self.assertEqual(policy.decide("files.write_file", {}, alias="files.write file").action, "ask")
        self.assertEqual(policy.decide("files.read_file", {}, alias="files.read-file").action, "allow")
        # The sanitized spelling keeps working, and with no alias nothing changes.
        self.assertEqual(policy_from_dict({"default": "allow", "deny": ["files.delete_file"]}).decide("files.delete_file", {}, alias="files.delete-file").action, "deny")
        self.assertEqual(policy.decide("files.delete_file").action, "allow")

    def test_L9_an_alias_cannot_weaken_a_decision(self):
        # Allowed under one spelling and denied under the other: deny wins.
        policy = policy_from_dict({"default": "ask", "allow": ["x.a_b"], "deny": ["x.a-b"]})
        self.assertEqual(policy.decide("x.a_b", {}, alias="x.a-b").action, "deny")

    def test_L11_argument_patterns(self):
        policy = policy_from_dict({
            "default": "allow",
            "deny": [{"tool": "git.run", "args": {"argv.0": "push"}}, {"tool": "fs.write", "args": {"path": "*.env", "mode": "w*"}}],
            "ask": [{"tool": "db.query", "args": {"opts.readonly": "false"}}, {"tool": "x.n", "args": {"count": "1*"}}],
        })
        cases = [
            ("git.run", {"argv": ["push", "origin"]}, "deny"),
            ("git.run", {"argv": ["status"]}, "allow"),
            ("git.run", {"argv": []}, "allow"),            # index out of range: no match
            ("git.run", {}, "allow"),                      # missing argument: no match
            ("git.run", {"argv": "push"}, "allow"),        # a string has no index 0
            ("git.run", None, "allow"),
            ("fs.write", {"path": "a/.env", "mode": "w"}, "deny"),
            ("fs.write", {"path": "a/.env"}, "allow"),     # every named argument must match
            ("fs.write", {"path": "a/b.txt", "mode": "w"}, "allow"),
            ("db.query", {"opts": {"readonly": False}}, "ask"),
            ("db.query", {"opts": {"readonly": True}}, "allow"),
            ("db.query", {"opts": None}, "allow"),
            ("x.n", {"count": 12}, "ask"),
            ("x.n", {"count": 2}, "allow"),
            ("x.n", {"count": [1]}, "allow"),              # a list is matched as its JSON text
        ]
        for path, args, expected in cases:
            self.assertEqual(policy.decide(path, args).action, expected, (path, args))
        self.assertEqual(policy.decide("git.run", {"argv": ["push"]}).rule, 'git.run {"argv.0": "push"}')

    def test_L11_values_that_are_not_strings_match_as_json(self):
        self.assertEqual(arg_text("plain"), "plain")
        self.assertEqual(arg_text(True), "true")
        self.assertEqual(arg_text(None), "null")
        self.assertEqual(arg_text(3), "3")
        self.assertEqual(arg_text(["a", 1]), '["a",1]')
        self.assertEqual(arg_text({"b": 1, "a": 2}), '{"a":2,"b":1}')
        self.assertEqual(lookup({"a": [{"b": 5}]}, "a.0.b"), (True, 5))
        self.assertEqual(lookup({"a": [1, 2]}, "a.-1"), (True, 2))
        for args, path in (({"a": 1}, "a.b"), ({"a": [1]}, "a.x"), ({"a": [1]}, "a.5"), (None, "a"), ("text", "0"), ({}, "")):
            self.assertEqual(lookup(args, path), (False, None), (args, path))

    def test_L12_malformed_rule_objects_are_errors(self):
        for rule in ({"tol": "a.b"}, {"tool": ""}, {"tool": 5}, {"args": {"x": "y"}}, {"tool": "a.b", "args": ["x"]},
                     {"tool": "a.b", "args": {"x": 5}}, {"tool": "a.b", "args": {"": "y"}}, {"tool": "a.b", "when": "x"}, {}):
            for key in ("allow", "ask", "deny"):
                with self.assertRaises(ConfigError, msg=repr(rule)):
                    policy_from_dict({key: [rule]})

    def test_L12_rules_round_trip_through_the_config_shape(self):
        raw = {"mode": "enforce", "default": "ask", "allow": ["a.*"], "ask": [],
               "deny": [{"tool": "a.rm", "args": {"path": "/*"}}]}
        self.assertEqual(policy_from_dict(raw).to_dict(), raw)
        # String rules serialize as before, so a policy hash recorded by an
        # older version still compares equal.
        self.assertEqual(Policy(allow=("a.b",)).to_dict()["allow"], ["a.b"])

    def test_L15_a_decision_that_depends_on_arguments_is_flagged(self):
        policy = policy_from_dict({"default": "allow", "deny": [{"tool": "fs.wri*", "args": {"path": "*.env"}}, "fs.rm"]})
        self.assertTrue(policy.depends_on_args("fs.write"))
        self.assertTrue(policy.depends_on_args("fs.write_x", alias="fs.write-x"))
        self.assertFalse(policy.depends_on_args("fs.rm"))
        self.assertFalse(policy.depends_on_args("fs.read"))

    def test_L13_L14_approval_settings(self):
        base = {"connectors": {"demo": {"type": "demo"}}}
        self.assertEqual(config_from_dict(base, "t", {}).approval, {"require_terminal": True, "ttl_s": 86400})
        self.assertEqual(config_from_dict(dict(base, approval={"ttl_s": 0}), "t", {}).approval, {"require_terminal": True, "ttl_s": 0})
        for bad in ({"require_terminal": "no"}, {"require_terminal": 0}, {"ttl_s": -1}, {"ttl_s": "1h"}, {"ttl_s": True}, {"tty": False}, []):
            with self.assertRaises(ConfigError, msg=repr(bad)):
                config_from_dict(dict(base, approval=bad), "t", {})


class OutputBudgets(unittest.TestCase):
    def test_T1_at_or_under_budget_is_untouched(self):
        self.assertEqual(truncate_text("hello", 5), ("hello", 0))
        self.assertEqual(truncate_text("", 0), ("", 0))
        self.assertEqual(budget_result({"a": 1}, 100), ({"a": 1}, False, 8))

    def test_T2_degenerate_budgets_stay_bounded(self):
        for budget in (0, -1, -10**9, 1, 3):
            for keep in ("head", "both"):
                text, dropped = truncate_text("x" * 1000, budget, keep)
                self.assertLessEqual(len(text), max(budget, 0) + 40)
                self.assertEqual(dropped, 1000 - max(budget, 0))
            shown, truncated, total = budget_result(["x" * 1000], budget)
            self.assertTrue(truncated)
            self.assertLessEqual(len(shown), max(budget, 0) + 40)

    def test_T3_structure_over_budget_becomes_a_flagged_preview(self):
        value = {"rows": list(range(1000))}
        shown, truncated, total = budget_result(value, 50)
        self.assertTrue(truncated)
        self.assertIsInstance(shown, str)
        self.assertGreater(total, 50)
        self.assertNotEqual(shown, value)

    def test_T4_marker_states_what_was_dropped(self):
        text, dropped = truncate_text("a" * 100, 10)
        self.assertEqual(dropped, 90)
        self.assertIn("90 chars dropped", text)

    def test_T5_both_ends_are_kept(self):
        text, dropped = truncate_text("START" + "-" * 1000 + "END", 40, keep="both")
        self.assertTrue(text.startswith("START"))
        self.assertTrue(text.endswith("END"))
        self.assertEqual(dropped, 1008 - 40)

    def test_T6_exactly_at_and_one_over(self):
        self.assertEqual(truncate_text("x" * 10, 10), ("x" * 10, 0))
        self.assertEqual(truncate_text("x" * 11, 10)[1], 1)
        self.assertFalse(budget_result("x" * 8, 10)[1])  # 8 chars + 2 quotes
        self.assertTrue(budget_result("x" * 9, 10)[1])

    def test_T7_bounded_log_under_a_flood(self):
        log = BoundedLog(100)
        for i in range(20000):
            log.add("line %d\n" % i)
        total = sum(len("line %d\n" % i) for i in range(20000))
        self.assertEqual(log.total, total)
        rendered = log.render()
        self.assertLessEqual(len(rendered), 100 + 40)
        self.assertEqual(log.dropped, total - 100)
        self.assertTrue(rendered.startswith("line 0\n"))
        self.assertTrue(rendered.endswith("line 19999\n"))
        self.assertLessEqual(len(log._tail), 20)  # chunks are dropped, not hoarded

    def test_T7_bounded_log_small_inputs_and_zero_budget(self):
        log = BoundedLog(100)
        log.add("abc")
        log.add("def")
        self.assertEqual((log.render(), log.dropped), ("abcdef", 0))
        one = BoundedLog(100)
        one.add("x" * 1000)
        self.assertEqual(one.dropped, 900)
        self.assertEqual(len(one.render()), 100 + len("\n[... 900 chars dropped ...]\n"))
        zero = BoundedLog(0)
        zero.add("anything")
        self.assertEqual(zero.dropped, 8)
        self.assertNotIn("anything", zero.render())


class Fingerprints(unittest.TestCase):
    def test_F1_key_order_does_not_matter(self):
        self.assertEqual(fingerprint("tool", "a.b", {"x": 1, "y": {"p": 1, "q": 2}}), fingerprint("tool", "a.b", {"y": {"q": 2, "p": 1}, "x": 1}))

    def test_F2_any_difference_changes_it(self):
        base = fingerprint("tool", "a.b", {"x": 1})
        for other in (("tool", "a.b", {"x": 2}), ("tool", "a.c", {"x": 1}), ("step", "a.b", {"x": 1}), ("tool", "a.b", {"x": "1"}),
                      ("tool", "a.b", {"x": 1, "y": None}), ("tool", "a.b", None), ("tool", "a.b", {"x": [1]}), ("tool", "a.b", {"x": 1.5})):
            self.assertNotEqual(base, fingerprint(*other), other)
        # List order is meaningful, unlike key order.
        self.assertNotEqual(fingerprint("tool", "a", {"l": [1, 2]}), fingerprint("tool", "a", {"l": [2, 1]}))

    def test_F3_non_finite_numbers_are_refused(self):
        for value in (float("nan"), float("inf"), float("-inf")):
            with self.assertRaises(ValueError):
                canonical({"x": value})

    def test_F4_non_ascii_text_serializes_to_ascii(self):
        text = canonical({"k": "héllo 日本 \U0001f600"})
        self.assertTrue(text.isascii())
        self.assertNotEqual(fingerprint("tool", "a", {"k": "é"}), fingerprint("tool", "a", {"k": "e"}))


class Configuration(unittest.TestCase):
    def test_G5_env_reference_that_is_not_set(self):
        self.assertEqual(expand_env("Bearer ${TOKEN}", "x", {"TOKEN": "abc"}), "Bearer abc")
        self.assertEqual(expand_env("no reference", "x", {}), "no reference")
        with self.assertRaises(ConfigError) as caught:
            expand_env("${MISSING_ONE}", "connectors.gh.env.TOKEN", {})
        self.assertIn("MISSING_ONE", caught.exception.message)
        self.assertIn("connectors.gh.env.TOKEN", caught.exception.message)

    def test_environment_overrides_the_file_for_the_model_endpoint(self):
        raw = {"connectors": {"demo": {"type": "demo"}}, "llm": {"base_url": "http://file/v1", "model": "from-file"}}
        config = config_from_dict(raw, "test", {"CODEMODE_MODEL": "from-env"})
        self.assertEqual(config.llm, {"base_url": "http://file/v1", "model": "from-env"})
        self.assertEqual(config_from_dict(raw, "test", {}).llm["model"], "from-file")

    def test_another_providers_key_is_not_picked_up(self):
        # A key exported for one provider must not be sent to whatever
        # endpoint this runtime is pointed at.
        raw = {"connectors": {"demo": {"type": "demo"}}, "llm": {"base_url": "http://local/v1", "model": "m"}}
        config = config_from_dict(raw, "test", {"OPENAI_API_KEY": "sk-other", "ANTHROPIC_API_KEY": "sk-other"})
        self.assertNotIn("api_key", config.llm)

    def test_api_key_is_never_printed(self):
        raw = {"connectors": {"demo": {"type": "demo"}}, "llm": {"api_key": "sk-secret"}}
        self.assertNotIn("sk-secret", repr(config_from_dict(raw, "test", {}).describe()))


class CallLimits(unittest.TestCase):
    BASE = {"connectors": {"ci": {"type": "demo", "call_timeout_s": 90}, "db": {"type": "demo"}}}

    def test_C1_the_time_limit_of_one_call(self):
        config = config_from_dict(dict(self.BASE, limits={
            "call_timeout_s": 30, "call_timeouts": {"ci.run_build": 1800, "ci.run_*": 600, "*.vacuum": 900},
        }), "t", {})
        cases = [
            ("ci", "ci.run_build", "", 1800),      # the first pattern that matches wins
            ("ci", "ci.run_tests", "", 600),
            ("ci", "ci.status", "", 90),           # no pattern: the connector's own limit
            ("db", "db.query", "", 30),            # neither: the global limit
            ("db", "db.vacuum", "", 900),
            ("ci", "ci.run_build_x", "ci.run-build-x", 600),
            ("db", "db.t_2fa", "db.vacuum", 900),  # a pattern in the server's spelling applies too
        ]
        for connector, path, alias, expected in cases:
            self.assertEqual(config.call_timeout(connector, path, alias), expected, path)
        self.assertEqual(config_from_dict(self.BASE, "t", {}).call_timeout("db", "db.query"), 30)

    def test_C1_C2_malformed_limits_are_errors(self):
        for limits in ({"call_timeouts": ["ci.*"]}, {"call_timeouts": {"ci.*": 0}}, {"call_timeouts": {"ci.*": "5m"}},
                       {"call_timeouts": {"": 5}}, {"call_timeouts": {"ci.*": True}}):
            with self.assertRaises(ConfigError, msg=repr(limits)):
                config_from_dict(dict(self.BASE, limits=limits), "t", {})
        for extra in ({"call_timeout_s": 0}, {"call_timeout_s": "1"}, {"startup_timeout_s": -1}, {"max_concurrency": 0},
                      {"max_concurrency": 1.5}, {"max_concurrency": True}, {"max_concurency": 1}):
            with self.assertRaises(ConfigError, msg=repr(extra)):
                config_from_dict({"connectors": {"x": dict({"type": "demo"}, **extra)}}, "t", {})

    def test_C9_credentials_are_removed_from_values(self):
        secret = "tok_live_9f8e7d6c"
        value = {"headers": {"auth": "Bearer " + secret}, "rows": [secret, 7, None, {"note": "x" + secret + "y"}], secret: 1}
        cleaned, changed = redact(value, [secret])
        self.assertTrue(changed)
        self.assertNotIn(secret, json.dumps(cleaned))
        self.assertEqual(cleaned["headers"]["auth"], "Bearer [redacted]")
        self.assertEqual(cleaned["rows"][1:3], [7, None])
        self.assertEqual(cleaned["rows"][3], {"note": "x[redacted]y"})
        self.assertIn("[redacted]", cleaned)  # a key is text too

    def test_C9_what_is_not_redacted(self):
        self.assertEqual(redact({"a": "plain"}, ["tok_live_9f8e7d6c"]), ({"a": "plain"}, False))
        self.assertEqual(redact({"a": "the value 1 is true"}, ["1", "true", "", "abc"]), ({"a": "the value 1 is true"}, False))
        self.assertEqual(redact("text", []), ("text", False))
        self.assertEqual(redact(None, ["secret-value"]), (None, False))
        for name in ("GITHUB_TOKEN", "api_key", "X-Api-Key", "Authorization", "DB_PASSWORD", "client_secret", "AWS_SECRET_ACCESS_KEY", "Cookie"):
            self.assertTrue(looks_secret(name), name)
        for name in ("PATH", "PROJECT_ROOT", "LANG", "PORT", "DEBUG", "HOME"):
            self.assertFalse(looks_secret(name), name)

    def test_C10_literal_env_values_are_not_printed(self):
        raw = {"connectors": {"x": {"type": "mcp-stdio", "command": ["server"],
                                    "env": {"API_TOKEN": "literal-secret-value", "REGION": "eu", "REF": "${SOME_VAR}", "MIXED": "Bearer ${T}"}}}}
        shown = config_from_dict(raw, "t", {}).describe()["connectors"]["x"]["env"]
        self.assertEqual(shown, {"API_TOKEN": "(hidden)", "REGION": "(hidden)", "REF": "${SOME_VAR}", "MIXED": "Bearer ${T}"})
        # What the connector is started with is untouched.
        self.assertEqual(config_from_dict(raw, "t", {}).connectors["x"]["env"]["API_TOKEN"], "literal-secret-value")


class McpPieces(unittest.TestCase):
    def test_M4_what_a_tool_result_becomes(self):
        def text(*parts):
            return {"content": [{"type": "text", "text": part} for part in parts]}

        cases = [
            (text("plain words"), "plain words"),
            (text('{"a": [1, 2]}'), {"a": [1, 2]}),
            (text("42"), 42),
            (text("true"), True),
            (text("null"), None),
            (text("NaN"), "NaN"),                      # not JSON: stays text
            (text("{broken"), "{broken"),
            (text("one", "two"), ["one", "two"]),
            (text(), None),
            ({"content": [{"type": "text", "text": "ignored"}], "structuredContent": {"k": 1}}, {"k": 1}),
            ({"content": [], "structuredContent": 0}, 0),   # a falsy structured value still wins
            ("not a dict", "not a dict"),
            (None, None),
            ({"content": "not a list"}, None),
        ]
        for result, expected in cases:
            self.assertEqual(unwrap_result(result), expected, result)
        mixed = unwrap_result({"content": [{"type": "text", "text": "caption"}, {"type": "image", "mimeType": "image/png", "data": "QUJD" * 10}]})
        self.assertEqual(mixed, {"text": ["caption"], "other": [{"type": "image", "mimeType": "image/png", "chars": 40}]})

    def test_M4_text_kept_as_text_when_the_connector_says_so(self):
        for raw in ('{"a": 1}', "42", "true", "[1]", "words"):
            self.assertEqual(unwrap_result({"content": [{"type": "text", "text": raw}]}, parse_json_text=False), raw)
        # Structured content is still structured: the option is about text.
        self.assertEqual(unwrap_result({"content": [], "structuredContent": {"k": 1}}, parse_json_text=False), {"k": 1})

    def test_M4_an_error_result_raises_with_its_text(self):
        with self.assertRaises(ToolFailure) as caught:
            unwrap_result({"isError": True, "content": [{"type": "text", "text": "no such file"}]})
        self.assertEqual((caught.exception.kind, caught.exception.message), ("ToolError", "no such file"))
        with self.assertRaises(ToolFailure):
            unwrap_result({"isError": True, "structuredContent": {"would": "win otherwise"}})

    def test_M3_one_mailbox_per_connection(self):
        old, new = Mailbox(), Mailbox()
        waiting_old = old.expect({"id": 1, "method": "tools/call"}, None)
        waiting_new = new.expect({"id": 1, "method": "tools/call"}, None)   # the same id, on the new connection
        old.close()                                                          # the old process reached the end of its output
        self.assertTrue(waiting_old["event"].is_set())
        self.assertIsNone(waiting_old["message"])
        self.assertFalse(waiting_new["event"].is_set())                      # the new request is not woken by it
        self.assertIsNone(old.deliver({"id": 1, "result": {"late": True}}))  # nor answered by a late line from the old one
        self.assertFalse(waiting_new["event"].is_set())
        new.deliver({"id": 1, "result": {"ok": 1}})
        self.assertEqual(waiting_new["message"], {"id": 1, "result": {"ok": 1}})
        self.assertTrue(old.expect({"id": 2, "method": "x"}, None)["event"].is_set())   # a closed mailbox never keeps anyone waiting

    def test_M2_M7_what_else_arrives_on_the_stream(self):
        box = Mailbox()
        self.assertEqual(box.deliver({"id": "srv-1", "method": "ping"}), {"jsonrpc": "2.0", "id": "srv-1", "result": {}})
        self.assertEqual(box.deliver({"id": 9, "method": "sampling/createMessage"})["error"]["code"], -32601)
        seen = []
        slot = box.expect({"id": 5, "method": "tools/call", "params": {"_meta": {"progressToken": "t5"}}}, seen.append)
        box.deliver({"method": "notifications/progress", "params": {"progressToken": "t5", "progress": 1, "total": 2}})
        box.deliver({"method": "notifications/progress", "params": {"progressToken": "other", "progress": 9}})
        box.deliver({"method": "notifications/message", "params": {"data": "a log line"}})
        box.deliver({"level": "info"})                                          # JSON, but not a protocol message
        box.deliver(["a", "batch"])
        self.assertEqual(seen, ["1 of 2"])
        box.deliver({"id": 5, "result": {}})
        box.deliver({"method": "notifications/progress", "params": {"progressToken": "t5", "progress": 2, "total": 2}})
        self.assertEqual(seen, ["1 of 2"])                                      # after the response the token means nothing
        self.assertTrue(slot["event"].is_set())
        self.assertEqual(box.noise_note(), "")
        box.note_noise(b"Server listening on port 3000\r\n")
        box.note_noise(b"second line")
        self.assertIn("2 line(s)", box.noise_note())
        self.assertIn("Server listening on port 3000", box.noise_note())

    def test_M5_a_schema_is_cut_down_in_steps_until_it_fits(self):
        long = "words " * 200
        schema = {"type": "object", "required": ["p0"], "properties": {
            "p%d" % i: {"type": "string", "description": long, "properties": {"deep": {"type": "object", "description": long, "properties": {"deeper": {"type": "integer"}}}}}
            for i in range(300)}}
        size = len(json.dumps(schema))
        self.assertEqual(fit_schema(schema, size + 1), (schema, 0))        # fits: untouched, not flagged
        previous = size
        for budget in (200000, 60000, 20000, 5000, 600, 50, 0):
            fitted, original = fit_schema(schema, budget)
            now = len(json.dumps(fitted))
            self.assertEqual(original, size)
            self.assertLessEqual(now, max(budget, 120), budget)           # the last resort has a small fixed frame
            self.assertLessEqual(now, previous)
            self.assertEqual(fitted.get("type"), "object")
            previous = now
        medium, _ = fit_schema(schema, 60000)
        self.assertEqual(len(medium["properties"]), 300)                  # every parameter is still named
        self.assertEqual(medium["required"], ["p0"])
        tiny, _ = fit_schema(schema, 600)
        self.assertIn("300 properties", tiny["note"])
        for odd in (None, True, [], "text", {"type": "string"}):
            self.assertEqual(fit_schema(odd, 10 ** 6), (odd, 0))
        self.assertEqual(fit_schema({"a": object()}, 10)[1], 1)           # not JSON: an empty schema, flagged

    def test_M7_progress_as_one_line(self):
        self.assertEqual(progress_text({"progress": 2, "total": 5, "message": "building"}), "building (2 of 5)")
        self.assertEqual(progress_text({"progress": 2, "total": 5}), "2 of 5")
        self.assertEqual(progress_text({"progress": 0.5}), "0.5")
        self.assertEqual(progress_text({"message": "  linking  "}), "linking")
        for odd in ({}, None, "text", {"progress": "half"}, {"progress": True}):
            self.assertEqual(progress_text(odd), "")

    def test_M13_event_streams(self):
        def events(text):
            return list(iter_sse(iter(text.encode("utf-8").splitlines(True))))

        self.assertEqual(events("data: one\n\n"), [("message", "one")])
        self.assertEqual(events("event: endpoint\ndata: /messages?s=1\n\n"), [("endpoint", "/messages?s=1")])
        self.assertEqual(events("data: {\ndata: \"a\": 1}\n\n"), [("message", '{\n"a": 1}')])       # joined with a line break
        self.assertEqual(events(": comment\n\ndata: x\r\n\r\n: keep-alive\n\n"), [("message", "x")])
        self.assertEqual(events("event: ping\n\ndata: y\n\n"), [("message", "y")])                  # no data: not dispatched
        self.assertEqual(events("data:no space\ndata:  two spaces\n\n"), [("message", "no space\n two spaces")])
        self.assertEqual(events("id: 7\nretry: 100\ndata: z\n\n"), [("message", "z")])              # other fields are ignored
        self.assertEqual(events("data: cut off"), [("message", "cut off")])                        # a stream that just ends
        self.assertEqual(events(""), [])
        self.assertEqual(events("event: a\ndata: 1\n\ndata: 2\n\n"), [("a", "1"), ("message", "2")])  # the event name does not carry over

    def test_M12_M13_remote_connectors_in_the_config(self):
        good = {"connectors": {"web": {"type": "mcp-http", "url": "https://example.com/mcp", "headers": {"Authorization": "Bearer ${T}"}},
                               "old": {"type": "mcp-sse", "url": "http://localhost:1/sse", "parse_json_text": False}}}
        self.assertEqual(sorted(config_from_dict(good, "t", {}).connectors), ["old", "web"])
        for spec in ({"type": "mcp-http"}, {"type": "mcp-http", "url": "example.com/mcp"}, {"type": "mcp-http", "url": "ftp://x"},
                     {"type": "mcp-http", "url": 5}, {"type": "mcp-sse", "url": "http://x", "headers": ["a"]},
                     {"type": "mcp-http", "url": "http://x", "headers": {"A": 1}}, {"type": "mcp-http", "url": "http://x", "command": ["x"]},
                     {"type": "mcp-stdio", "command": ["x"], "url": "http://x"}, {"type": "mcp-http", "url": "http://x", "parse_json_text": "no"}):
            with self.assertRaises(ConfigError, msg=repr(spec)):
                config_from_dict({"connectors": {"x": spec}}, "t", {})

    def test_M6_which_values_of_a_connector_are_credentials(self):
        env = {"GH_TOKEN": "tok_abcdefghijkl", "ROOT": "/srv/project-root", "REGION": "eu-central"}
        spec = {"env": {"TOKEN": "${GH_TOKEN}", "PROJECT": "${ROOT}", "REGION": "${REGION}", "LITERAL_KEY": "written-in-the-file"},
                "headers": {"Authorization": "Bearer ${GH_TOKEN}", "X-Region": "${REGION}"}, "secrets": ["REGION"]}
        saved = dict(os.environ)
        os.environ.update(env)
        try:
            values, secrets = resolve_env("x", spec)
            self.assertEqual(values["PROJECT"], "/srv/project-root")
            self.assertEqual(sorted(set(secrets)), ["eu-central", "tok_abcdefghijkl"])   # by name, and by the secrets list; a plain path is not one
            _, header_secrets = resolve_env("x", spec, "headers")
            self.assertIn("Bearer tok_abcdefghijkl", header_secrets)
            self.assertIn("tok_abcdefghijkl", header_secrets)                           # the token without its scheme, too
        finally:
            os.environ.clear()
            os.environ.update(saved)
        with self.assertRaises(ToolFailure) as caught:
            resolve_env("x", {"env": {"TOKEN": "${CODEMODE_TEST_UNSET_VARIABLE}"}})
        self.assertEqual(caught.exception.kind, "ConnectorUnavailable")
        self.assertIn("CODEMODE_TEST_UNSET_VARIABLE", caught.exception.message)

    def test_M15_where_the_search_for_a_config_stops(self):
        import tempfile
        from pathlib import Path

        with tempfile.TemporaryDirectory() as top:
            top_path = Path(top).resolve()
            deep = top_path / "home" / "repo" / "src" / "pkg"
            deep.mkdir(parents=True)
            home = top_path / "home"
            self.assertIsNone(find_config_upward(deep, home))
            (top_path / "codemode.json").write_text("{}")
            self.assertIsNone(find_config_upward(deep, home))                    # above the home folder: someone else's
            (home / "codemode.json").write_text("{}")
            self.assertEqual(find_config_upward(deep, home), home / "codemode.json")
            (home / "repo" / "codemode.json").write_text("{}")
            self.assertEqual(find_config_upward(deep, home), home / "repo" / "codemode.json")   # the nearest one wins
            (home / "repo" / "src" / ".git").mkdir()
            self.assertIsNone(find_config_upward(deep, home))                    # the repository ends at src: do not leave it
            (home / "repo" / "src" / "codemode.json").write_text("{}")
            self.assertEqual(find_config_upward(deep, home), home / "repo" / "src" / "codemode.json")
            self.assertIsNone(find_config_upward(home / "repo" / "src", home))   # the start folder is the caller's to check


class CommandTools(unittest.TestCase):
    TOOL = {
        "command": ["prog", "--mode", "{mode}", "{files}", "x={count}", {"if": "only", "then": ["-k", "{only}"]}, {"if": "verbose", "then": ["-v"]}, "{{literal}}"],
        "params": {"mode": {"type": "string", "default": "fast", "enum": ["fast", "full"]}, "files": {"type": "array", "default": []},
                   "count": {"type": "integer"}, "only": {"type": "string"}, "verbose": {"type": "boolean", "default": False}},
        "allow_env": ["CI_*", "DEBUG"],
    }

    def spec(self, **tool):
        return {"type": "commands", "tools": {"t": dict({"command": ["prog"]}, **tool)}}

    def test_K1_what_a_config_may_not_say(self):
        validate_spec("ci", self.spec())
        validate_spec("ci", {"type": "commands", "tools": {"t": self.TOOL}})
        bad = [
            {"type": "commands"}, {"type": "commands", "tools": {}}, {"type": "commands", "tools": {"t": "prog"}},
            self.spec(command=[]), self.spec(command="prog"), self.spec(command=[5]), self.spec(command=["prog", 5]),
            self.spec(command=["prog", "{missing}"]), self.spec(command=["prog", {"if": "x", "then": ["-x"]}]),
            self.spec(command=["{tool}", "--version"], params={"tool": {}}), self.spec(command=["bin/{name}"], params={"name": {}}),
            self.spec(command=["prog", {"when": "x"}], params={"x": {}}), self.spec(command=["prog", {"if": "x", "then": "-x"}], params={"x": {}}),
            self.spec(params={"x": {"type": "object"}}), self.spec(params={"x": {"type": "integer", "default": "three"}}),
            self.spec(params={"x": {"enum": []}}), self.spec(params={"x": {"enum": ["a"], "default": "b"}}), self.spec(params={"bad-name": {}}),
            self.spec(params={"env": {}}), self.spec(params={"x": {"kind": "string"}}), self.spec(timeout_s=0), self.spec(ok_exit_codes=[]),
            self.spec(ok_exit_codes=["0"]), self.spec(allow_env="CI_*"), self.spec(env={"A": 1}), self.spec(shell=True),
            dict(self.spec(), inherit_env="all"), dict(self.spec(), max_output_chars=10), dict(self.spec(), env=["A"]),
        ]
        for spec in bad:
            with self.assertRaises(ConfigError, msg=repr(spec)):
                validate_spec("ci", spec)
        with self.assertRaises(ConfigError) as caught:
            validate_spec("ci", self.spec(command=["prog", "{missing}"]))
        self.assertIn("{missing}", caught.exception.message)
        self.assertIn("connectors.ci.tools.t", caught.exception.message)

    def test_K2_expansion_never_makes_a_second_command(self):
        nasty = 'a b; rm -rf "x" && echo $(id) | tee `y`'
        argv = expand_command(self.TOOL, {"count": 3, "only": nasty, "files": ["one", "two words", "-rf"]})
        self.assertEqual(argv, ["prog", "--mode", "fast", "one", "two words", "-rf", "x=3", "-k", nasty, "{literal}"])
        self.assertEqual(expand_command(self.TOOL, {"count": 0, "mode": "full", "verbose": True}),
                         ["prog", "--mode", "full", "x=0", "-v", "{literal}"])
        # A parameter that was not given, or given as empty or false, leaves its conditional part out.
        for args in ({"count": 1}, {"count": 1, "only": ""}, {"count": 1, "verbose": False}):
            self.assertEqual(expand_command(self.TOOL, args), ["prog", "--mode", "fast", "x=1", "{literal}"], args)

    def test_K2_values_inside_an_element(self):
        tool = {"command": ["prog", "--out={dir}/{name}.log", "{flag}", "list={items}"],
                "params": {"dir": {}, "name": {}, "flag": {"type": "boolean"}, "items": {"type": "array"}}}
        self.assertEqual(expand_command(tool, {"dir": "C:/out dir", "name": "run 1", "flag": True, "items": ["a", 2]}),
                         ["prog", "--out=C:/out dir/run 1.log", "true", "list=a 2"])
        self.assertEqual(required_params(tool), ["dir", "name", "flag", "items"])
        self.assertEqual(required_params(self.TOOL), ["count"])   # the rest have defaults or sit behind their own condition

    def test_K3_arguments_are_checked_before_anything_runs(self):
        self.assertEqual(check_args("ci.t", self.TOOL, {"count": 2, "env": {"CI_RUN": "1", "DEBUG": "x"}}), ({"count": 2}, {"CI_RUN": "1", "DEBUG": "x"}))
        bad = [({}, "needs count"), ({"count": "2"}, "must be integer"), ({"count": True}, "must be integer"), ({"count": 1, "mode": "slow"}, "one of"),
               ({"count": 1, "files": "a.txt"}, "list"), ({"count": 1, "files": [["nested"]]}, "list"), ({"count": 1, "extra": 1}, "does not take extra"),
               ({"count": 1, "env": {"PATH": "/tmp"}}, "PATH"), ({"count": 1, "env": {"CI_X": 5}}, "strings"), ({"count": 1, "env": "CI_X=1"}, "strings"),
               ({"count": 1, "env": {"ci_x": "lower case is another name"}}, "ci_x")]
        for args, needle in bad:
            with self.assertRaises(ToolFailure, msg=repr(args)) as caught:
                check_args("ci.t", self.TOOL, args)
            self.assertEqual(caught.exception.kind, "BadArguments")
            self.assertIn(needle, caught.exception.message, args)
        with self.assertRaises(ToolFailure) as caught:
            check_args("ci.t", {"command": ["prog"]}, {"env": {"ANY": "x"}})
        self.assertIn("allow_env", caught.exception.hint)        # a tool that allows nothing says where that is decided

    def test_K5_output_over_the_budget_keeps_both_ends(self):
        text = "START " + "x" * 5000 + " END"
        cut, was_cut = cut_middle(text, 400)
        self.assertTrue(was_cut)
        self.assertTrue(cut.startswith("START ") and cut.endswith(" END"))
        self.assertIn("%d chars not shown" % (len(text) - 400), cut)
        self.assertLess(len(cut), 500)
        self.assertEqual(cut_middle("short", 400), ("short", False))
        self.assertEqual(cut_middle("x" * 400, 400), ("x" * 400, False))

    def test_K5_capture_is_bounded_and_counts_everything(self):
        capture = _Capture(keep=100)
        for i in range(1000):
            capture.add(b"%04d\n" % i)
        self.assertEqual(capture.total, 5000)
        self.assertEqual(capture.lost, 4800)
        data = capture.data()
        self.assertEqual(len(data), 200)
        self.assertTrue(data.startswith(b"0000\n0001\n") and data.endswith(b"0998\n0999\n"))
        self.assertEqual(capture.last_line(), "0999")
        small = _Capture(keep=100)
        small.add(b"only this\n")
        self.assertEqual((small.data(), small.lost, small.last_line()), (b"only this\n", 0, "only this"))
        self.assertEqual(_Capture().last_line(), "")

    def test_K9_output_that_is_not_utf8_and_crlf(self):
        self.assertEqual(decode_output(b"one\r\ntwo\rthree\n"), "one\ntwo\nthree\n")
        self.assertEqual(decode_output("h\u00e9llo \u65e5\u672c\r\n".encode("utf-8")), "h\u00e9llo \u65e5\u672c\n")
        legacy = decode_output(b"caf\xe9 \xff\xfe\r\n")           # not UTF-8: decoded some way, never an exception
        self.assertTrue(legacy.startswith("caf") and legacy.endswith("\n") and "\r" not in legacy)
        self.assertEqual(decode_output(b""), "")


class ProjectHelpers(unittest.TestCase):
    def test_P6_what_is_in_a_helper_module_is_read_without_running_it(self):
        source = '''"""Things this project does often.

More detail that search does not need.
"""
import os
raise SystemExit("a helper file must never be executed on the host")

def run_suite(name, retries=2, *extra, only=None, **options):
    """Run one test suite and keep the failures.

    Longer text."""
    return name

def _private():
    pass

def no_doc(a, /, b: int = 3) -> list: return []

class NotAFunction:
    def method(self): pass

async def also_skipped(): pass
'''
        (module,) = describe_helpers({"ci": source})
        self.assertEqual(module["doc"], "Things this project does often.")
        self.assertEqual(module["functions"], [
            {"name": "run_suite", "signature": "helpers.ci.run_suite(name, retries=2, *extra, only=None, **options)",
             "doc": "Run one test suite and keep the failures."},
            {"name": "no_doc", "signature": "helpers.ci.no_doc(a, /, b: int=3)", "doc": ""},
        ])

    def test_P2_a_file_that_does_not_parse_is_listed_with_the_error(self):
        modules = describe_helpers({"good": "def f():\n    return 1\n", "broken": "def f(:\n    pass\n", "empty": ""})
        self.assertEqual([m["module"] for m in modules], ["broken", "empty", "good"])
        self.assertIn("SyntaxError", modules[0]["error"])
        self.assertIn("line 1", modules[0]["error"])
        self.assertEqual(modules[0]["functions"], [])
        self.assertNotIn("error", modules[1])
        self.assertEqual(modules[2]["functions"][0]["signature"], "helpers.good.f()")

    def test_P6_a_long_docstring_line_is_cut(self):
        (module,) = describe_helpers({"m": 'def f():\n    """%s"""\n' % ("word " * 200)})
        self.assertLessEqual(len(module["functions"][0]["doc"]), 300)
        self.assertTrue(module["functions"][0]["doc"].endswith("..."))


class ProjectSetup(unittest.TestCase):
    def convert(self, servers):
        return connectors_from_servers(servers)

    def test_U1_server_lists_in_each_shape(self):
        claude = '{"mcpServers": {"files": {"command": "npx", "args": ["-y", "pkg", "."], "env": {"MODE": "ro"}, "cwd": "sub"}}}'
        editor = '{"servers": {"web": {"type": "http", "url": "https://example.com/mcp"}}, "inputs": []}'
        self.assertEqual(self.convert(read_server_list(claude, "f"))[0],
                         {"files": {"type": "mcp-stdio", "command": ["npx", "-y", "pkg", "."], "env": {"MODE": "ro"}, "cwd": "sub"}})
        self.assertEqual(self.convert(read_server_list(editor, "f"))[0], {"web": {"type": "mcp-http", "url": "https://example.com/mcp"}})
        kinds = {"a": {"type": "sse", "url": "http://x/sse"}, "b": {"url": "http://x/mcp"}, "c": {"type": "streamable-http", "url": "http://x"},
                 "d": {"transport": "sse", "serverUrl": "http://y/sse"}}
        self.assertEqual({k: v["type"] for k, v in self.convert(kinds)[0].items()}, {"a": "mcp-sse", "b": "mcp-http", "c": "mcp-http", "d": "mcp-sse"})
        connectors, _, notes = self.convert({"off": {"command": "x", "disabled": True}, "odd": "not an object", "empty": {}, "plain": {"command": "x", "args": [1, True]}})
        self.assertEqual(connectors, {"plain": {"type": "mcp-stdio", "command": ["x", "1", "True"]}})
        self.assertEqual(len(notes), 3)
        for text in ("{not json", "[1]", '{"other": {}}', '{"mcpServers": []}'):
            with self.assertRaises(ConfigError, msg=text):
                read_server_list(text, "f")

    def test_U2_a_literal_credential_is_never_copied(self):
        servers = {"gh": {
            "command": "srv",
            "env": {"GITHUB_TOKEN": "ghp_literal_value_1234567890", "REGION": "eu", "REF": "${GH_REF}", "WITH_DEFAULT": "${GH_HOST:-github.com}",
                    "ASKED": "${input:api-key}", "PORT": 8080},
        }, "web": {"url": "https://x/mcp", "headers": {"Authorization": "Bearer abc-literal-token", "X-Api-Key": "k-literal", "Accept-Language": "en"}}}
        connectors, export, notes = self.convert(servers)
        self.assertEqual(connectors["gh"]["env"], {
            "GITHUB_TOKEN": "${GH_GITHUB_TOKEN}", "REGION": "eu", "REF": "${GH_REF}", "WITH_DEFAULT": "${GH_HOST}", "ASKED": "${API_KEY}", "PORT": "8080"})
        self.assertEqual(connectors["web"]["headers"], {"Authorization": "Bearer ${WEB_TOKEN}", "X-Api-Key": "${WEB_X_API_KEY}", "Accept-Language": "en"})
        self.assertEqual(export, ["API_KEY", "GH_GITHUB_TOKEN", "WEB_TOKEN", "WEB_X_API_KEY"])
        everything = json.dumps([connectors, export, notes])
        for literal in ("ghp_literal_value_1234567890", "abc-literal-token", "k-literal"):
            self.assertNotIn(literal, everything)
        config_from_dict({"connectors": connectors}, "t", {})        # what comes out is a valid config

    def test_U2_a_credential_under_an_innocent_name(self):
        # The name says nothing, the value does: a password inside a URL, a
        # token with a well-known prefix, a long opaque string.
        hidden = {"DATABASE_URL": "postgres://app:s3cr3t-pw@db.internal:5432/prod", "BUILD_ID": "9f8e7d6c5b4a39281706f5e4d3c2b1a098765432",
                  "SERVICE": "sk-proj-abcdefghijklmnop", "SESSION": "eyJhbGciOiJIUzI1NiJ9.payload.signature"}
        plain = {"REGION": "eu-central-1", "LOG_LEVEL": "debug", "ENDPOINT": "https://api.example.com/v1", "WORKDIR": "C:/Users/someone/a/long/path/to/the/project/folder",
                 "FEATURES": "alpha,beta,gamma,delta,epsilon,zeta,eta,theta", "EMPTY": "", "COUNT": "1234567890123456789012345678901234567890"}
        connectors, export, notes = self.convert({"db": {"command": "srv", "env": dict(hidden, **plain)}})
        env = connectors["db"]["env"]
        for key, value in hidden.items():
            self.assertEqual(env[key], "${DB_%s}" % key, key)
            self.assertNotIn(value, json.dumps([connectors, notes]))
            self.assertTrue(value_looks_secret(value), key)
        for key, value in plain.items():
            self.assertEqual(env[key], value, key)
            self.assertFalse(value_looks_secret(value), key)
        self.assertEqual(export, sorted("DB_" + key for key in hidden))

    def test_U3_names_a_program_can_write(self):
        self.assertEqual(connector_name("my-server", []), "my_server")
        self.assertEqual(connector_name("2fa", []), "s_2fa")
        self.assertEqual(connector_name("tools", []), "tools_server")
        self.assertEqual(connector_name("state", []), "state_server")
        self.assertEqual(connector_name("---", []), "server")
        self.assertEqual(connector_name("a.b", ["a_b"]), "a_b_2")
        self.assertEqual(connector_name("a b", ["a_b", "a_b_2"]), "a_b_3")
        connectors, _, notes = self.convert({"my-server": {"command": "x"}, "my.server": {"command": "y"}, "search": {"command": "z"}})
        self.assertEqual(sorted(connectors), ["my_server", "my_server_2", "search_server"])
        self.assertEqual(len(notes), 3)
        for name in connectors:
            self.assertTrue(name.isidentifier())

    def test_U3_the_runtimes_own_server_is_left_out(self):
        own = [{"command": "codemode", "args": ["serve"]}, {"command": "python", "args": ["/x/code-mode/main.py", "--config", "codemode.json", "serve"]},
               {"command": "py", "args": ["-m", "codemode_runtime", "serve"]}]
        for raw in own:
            connectors, _, notes = self.convert({"cm": raw})
            self.assertEqual(connectors, {}, raw)
            self.assertIn("own MCP server", notes[0])
        self.assertEqual(sorted(self.convert({"other": {"command": "serve", "args": ["--port", "1"]}})[0]), ["other"])

    def test_U4_the_starting_policy(self):
        tools = {
            "fs": [{"name": "read_file", "annotations": {"readOnlyHint": True}}, {"name": "write_file", "annotations": {"readOnlyHint": False, "destructiveHint": True}},
                   {"name": "get_secret_and_delete", "annotations": {"readOnlyHint": False, "destructiveHint": True}}, {"name": "list_dir", "annotations": {"readOnlyHint": True}}],
            "plain": [{"name": "list_items"}, {"name": "add_item"}],
            "down": None,
        }
        policy = starting_policy(tools)
        self.assertEqual((policy["mode"], policy["default"], policy["deny"]), ("enforce", "ask", ["*.delete_*"]))
        self.assertEqual(policy["allow"], ["fs.read_file", "fs.list_dir", "plain.list_*", "plain.get_*", "plain.search_*", "plain.read_*",
                                           "down.list_*", "down.get_*", "down.search_*", "down.read_*"])
        self.assertEqual(policy["ask"], ["fs.write_file", "fs.get_secret_and_delete"])
        decided = policy_from_dict(policy)
        self.assertEqual(decided.decide("fs.read_file").action, "allow")
        self.assertEqual(decided.decide("fs.get_secret_and_delete").action, "ask")     # a reading verb does not outvote the server's own word
        self.assertEqual(decided.decide("plain.add_item").action, "ask")
        self.assertEqual(decided.decide("plain.delete_item").action, "deny")
        self.assertEqual(decided.decide("fs.anything_new").action, "ask")

    def test_U6_gitignore_entries_are_added_once(self):
        self.assertEqual(gitignore_lines(""), [".codemode/", ".codemode-work/"])
        self.assertEqual(gitignore_lines("node_modules/\r\n.codemode/\r\n"), [".codemode-work/"])
        self.assertEqual(gitignore_lines(".codemode\n/.codemode-work/\n"), [])
        self.assertEqual(gitignore_lines("# .codemode/ is mentioned in a comment only\n"), [".codemode/", ".codemode-work/"])

    def test_U7_rules_that_match_no_tool(self):
        policy = policy_from_dict({
            "allow": ["fs.read_*", "fs.reed_file", "web.*", "*.list_*"],
            "ask": [{"tool": "fs.write_file", "args": {"path": "*.md"}}, {"tool": "fs.wirte_file", "args": {"path": "*"}}],
            "deny": ["*.delete_*", "fs.remove-all", "fs.format_disk"],
        })
        names = ["fs.read_file", "fs.write_file", "fs.remove_all", "fs.remove-all", "fs.list_dir"]
        dead = dead_rules(policy, names, unknown_namespaces=["web"])
        # fs.remove-all is the server's spelling of a tool that exists; the
        # rule for web cannot be judged while web is down; a rule for every
        # namespace ("*.delete_*") is an intent, not a misspelt name.
        self.assertEqual(dead, [("deny", "fs.format_disk"), ("ask", 'fs.wirte_file {"path": "*"}'), ("allow", "fs.reed_file")])
        # With nothing unknown, the rule for the missing namespace is dead too.
        self.assertEqual(dead_rules(policy, names, [])[-1], ("allow", "web.*"))
        self.assertEqual(len(dead_rules(policy, names, [])), 4)
        self.assertEqual(dead_rules(policy_from_dict({}), names, []), [])

    def test_U11_the_guide_carries_the_instructions_the_command_prints(self):
        import sync_docs

        self.assertEqual(sync_docs.current(), sync_docs.block(), "docs/ADOPTING.md is out of date: run python tests/sync_docs.py")
        cli, mcp = instructions("codemode --config x.json", "cli"), instructions("codemode", "mcp")
        self.assertIn("codemode --config x.json run --json -f", cli)
        self.assertNotIn("{cmd}", cli + mcp)
        self.assertNotIn("{inside}", cli + mcp)
        self.assertIn('"background": true', mcp)
        for text in (cli, mcp):
            self.assertIn("Never approve it yourself", text)
            self.assertLess(len(text), 7000)


class SandboxHelpers(unittest.TestCase):
    """The helpers a program gets that need no host. The module is the file
    that is sent to the sandbox; importing it here runs nothing."""

    LOG = "alpha\r\nbeta ERROR one\r\ngamma\r\ndelta error two\r\nepsilon\r\n"

    def test_H3_grep(self):
        self.assertEqual(list(sandbox.grep(self.LOG, "ERROR")), ["beta ERROR one"])
        both = sandbox.grep(self.LOG, "error", ignore_case=True)
        self.assertEqual((list(both), both.total, both.more), (["beta ERROR one", "delta error two"], 2, 0))
        capped = sandbox.grep(self.LOG, "error", ignore_case=True, limit=1)
        self.assertEqual((list(capped), capped.total, capped.more), (["beta ERROR one"], 2, 1))
        none = sandbox.grep(self.LOG, "zzz")
        self.assertEqual((list(none), none.total, none.more), ([], 0, 0))
        zero = sandbox.grep(self.LOG, "error", ignore_case=True, limit=0)
        self.assertEqual((list(zero), zero.total, zero.more), ([], 2, 2))

    def test_H3_grep_context_does_not_repeat_lines(self):
        around = sandbox.grep(self.LOG, "error", ignore_case=True, context=1)
        self.assertEqual(list(around), ["alpha", "beta ERROR one", "gamma", "delta error two", "epsilon"])
        self.assertEqual(around.total, 2)
        self.assertEqual(list(sandbox.grep(self.LOG, "alpha", context=5)), self.LOG.splitlines())

    def test_H3_grep_inputs_that_are_not_one_string(self):
        self.assertEqual(list(sandbox.grep("", "x")), [])
        self.assertEqual(list(sandbox.grep(None, "x")), [])
        self.assertEqual(list(sandbox.grep(["a b", "c", 7], "b|7")), ["a b", "7"])
        self.assertEqual(list(sandbox.grep(self.LOG, "a", invert=True)), ["epsilon"])

    def test_H3_tail_and_head(self):
        self.assertEqual(sandbox.tail(self.LOG, 2), "delta error two\nepsilon")
        self.assertEqual(sandbox.head(self.LOG, 2), "alpha\nbeta ERROR one")
        self.assertEqual(sandbox.tail(self.LOG, 99), "\n".join(self.LOG.splitlines()))
        for text in ("", None, []):
            self.assertEqual((sandbox.tail(text), sandbox.head(text)), ("", ""))
        self.assertEqual((sandbox.tail(self.LOG, 0), sandbox.head(self.LOG, 0), sandbox.tail(self.LOG, -3)), ("", "", ""))
        self.assertEqual(sandbox.tail(["a", "b", "c"], 2), "b\nc")

    def test_H3_summarize(self):
        text = "\n".join(["start"] + ["step %d ok" % i for i in range(40)] + ["warning: slow", "ERROR: boom", "2 tests FAILED", "", "Timed out after 5s", "done"])
        out = sandbox.summarize(text)
        self.assertEqual(out["lines"], 46)  # 47 lines, one of them empty and not counted
        self.assertEqual(out["chars"], len(text))
        self.assertEqual(out["head"], ["start", "step 0 ok", "step 1 ok"])
        self.assertEqual(out["tail"][-1], "done")
        self.assertEqual(out["counts"], {"error": 1, "failure": 1, "warning": 1, "timeout": 1})
        self.assertEqual(out["matches"]["error"], ["ERROR: boom"])
        self.assertEqual(out["matches"]["timeout"], ["Timed out after 5s"])

    def test_H3_summarize_edges(self):
        empty = sandbox.summarize("")
        self.assertEqual((empty["lines"], empty["head"], empty["tail"], empty["matches"]), (0, [], [], {}))
        self.assertEqual(sandbox.summarize(None)["chars"], 0)
        short = sandbox.summarize("a\nb")
        self.assertEqual((short["head"], short["tail"]), (["a", "b"], []))  # nothing is shown twice
        wide = sandbox.summarize("x" * 5000, width=50)
        self.assertLess(len(wide["head"][0]), 90)
        self.assertIn("+4950 chars", wide["head"][0])
        custom = sandbox.summarize(["PASS a", "FAIL b", "PASS c"], patterns={"passed": "^PASS", "failed": "^FAIL"}, limit=1)
        self.assertEqual(custom["counts"], {"passed": 2, "failed": 1})
        self.assertEqual(custom["matches"], {"passed": ["PASS a"], "failed": ["FAIL b"]})
        self.assertEqual(sandbox.summarize("no trouble here")["matches"], {})

    def test_H4_shape(self):
        value = {"ok": True, "n": 3, "ratio": 0.5, "none": None, "out": "x" * 900, "rows": [{"id": 1, "tags": ["a"]}], "empty": []}
        self.assertEqual(sandbox.shape(value), {
            "ok": "bool", "n": "int", "ratio": "float", "none": "null", "out": "str[900]",
            "rows": {"list": 1, "of": {"id": "int", "tags": "list[1]"}}, "empty": "list[0]",   # depth 3 ends at tags
        })
        self.assertEqual(sandbox.shape(value, depth=4)["rows"]["of"]["tags"], {"list": 1, "of": "str[1]"})
        self.assertEqual(sandbox.shape({"a": {"b": {"c": {"d": 1}}}}, depth=2), {"a": {"b": "dict[1 keys]"}})
        self.assertEqual(sandbox.shape([[1, 2], [3]], depth=1), {"list": 2, "of": "list[2]"})
        wide = sandbox.shape({"k%d" % i: i for i in range(30)}, keys=3)
        self.assertEqual(wide, {"k0": "int", "k1": "int", "k2": "int", "...": "+27 keys"})
        self.assertEqual(sandbox.shape((1, 2)), {"list": 2, "of": "int"})
        self.assertEqual(sandbox.shape(object()), "object")

    def test_H4_host_and_sandbox_agree(self):
        samples = [None, True, 0, 1.5, "", "text", [], [1], [[1]], {}, {"a": []}, {"a": {"b": [{"c": None}]}},
                   {"k%d" % i: [i] for i in range(20)}, {"deep": {"er": {"and": {"deeper": {"still": 1}}}}}]
        for value in samples:
            for depth in (0, 1, 3):
                self.assertEqual(shape_of(value, depth=depth), sandbox.shape(value, depth=depth), (value, depth))

    def test_H5_what_counts_as_a_failed_run(self):
        failure = sandbox.ToolError("ToolTimeout", "too slow")
        self.assertEqual(sandbox._run_failed(failure, None), {"error": "ToolTimeout", "message": "too slow"})
        self.assertIsNone(sandbox._run_failed({"ok": True}, None))
        self.assertIsNone(sandbox._run_failed("text", None))
        self.assertIsNone(sandbox._run_failed({"ok": None}, None))        # only an explicit false
        self.assertEqual(sandbox._run_failed({"ok": False, "x": 1}, None), {"result": {"ok": False, "x": 1}})
        self.assertEqual(sandbox._run_failed(3, lambda v: v > 5), {"result": 3})
        self.assertIsNone(sandbox._run_failed({"ok": False}, lambda v: True))   # the caller's check wins
        raised = sandbox._run_failed("text", lambda v: v["exit"] == 0)
        self.assertIn("check_raised", raised)
        # A ToolError is a failure whatever the check says: it never saw a result.
        self.assertEqual(sandbox._run_failed(failure, lambda v: True)["error"], "ToolTimeout")

    def test_H8_snapshot_of_a_failed_program(self):
        import json as json_module
        api = {"tools": object(), "state": {}, "args": {}}
        space = dict(api, res={"a": [1, 2]}, big="x" * 500, small=1, _private=2, handle=object(), mod=json_module,
                     fn=lambda: 1, seen={3, 1, 2}, args={"rebound": True})
        kept, omitted = sandbox._snapshot(space, api, 10 ** 6)
        self.assertEqual(kept, {"res": {"a": [1, 2]}, "big": "x" * 500, "small": 1, "seen": [1, 2, 3], "args": {"rebound": True}})
        self.assertEqual(omitted, [])
        kept, omitted = sandbox._snapshot(space, api, 60)
        self.assertNotIn("big", kept)
        self.assertIn("small", kept)
        self.assertEqual(omitted, [{"name": "big", "chars": 502}])
        kept, omitted = sandbox._snapshot(space, api, 0)
        self.assertEqual(kept, {})
        self.assertEqual(len(omitted), 5)

    def test_H9_a_value_lost_in_a_trailing_block(self):
        import ast as ast_module
        lost = ["x = 1\nif x:\n    {'a': 1}", "for i in range(3):\n    i", "try:\n    pass\nexcept Exception:\n    res",
                "if x:\n    pass\nelse:\n    x + 1", "with open(f) as h:\n    data"]
        fine = ["", "x = 1", "x = 1\nx", "if x:\n    print(x)", "for i in y:\n    tools.a.b(i=i)", "if x:\n    y = 1", "def f():\n    1"]
        for source in lost:
            self.assertTrue(sandbox._lost_value(ast_module.parse(source)), source)
        for source in fine:
            self.assertFalse(sandbox._lost_value(ast_module.parse(source)), source)

    def test_H10_hints_for_text_a_shell_changed(self):
        cases = [("x = 'line one\nline two'", "shell"), ("p = 'C:\\Users\\name'", "raw string"), ("return 5", "expression"),
                 ("await f()", "synchronous")]
        for source, needle in cases:
            try:
                compile(source, "<code>", "exec")
            except SyntaxError as exc:
                self.assertIn(needle, sandbox._describe_error(exc).get("hint", ""), source)
            else:
                self.fail("expected a SyntaxError for %r" % source)
        for exc in (KeyError("output"), IndexError("list index out of range"), TypeError("string indices must be integers, not 'str'"),
                    AttributeError("'NoneType' object has no attribute 'get'")):
            self.assertIn("shape(", sandbox._describe_error(exc).get("hint", ""), repr(exc))
        self.assertNotIn("hint", sandbox._describe_error(ZeroDivisionError("division by zero")))
        uncaught = sandbox._describe_error(sandbox.ToolError("PolicyDenied", "no", "Tell the user."))
        self.assertEqual(uncaught["type"], "PolicyDenied")
        self.assertTrue(uncaught["hint"].startswith("Tell the user."))
        self.assertIn("on_error", uncaught["hint"])


class TextDecoding(unittest.TestCase):
    def test_E3_byte_order_marks_pick_the_encoding(self):
        text = '{"k": "héllo 日本"}'
        for data in (text.encode("utf-8"), b"\xef\xbb\xbf" + text.encode("utf-8"), text.encode("utf-16"),
                     b"\xff\xfe" + text.encode("utf-16-le"), b"\xfe\xff" + text.encode("utf-16-be"), text.encode("utf-32")):
            self.assertEqual(decode_text(data), text, data[:4])
        self.assertEqual(decode_text(b""), "")

    def test_E3_line_ends_are_normalized(self):
        self.assertEqual(decode_text(b"a = 1\r\nb = 2\rc = 3\n"), "a = 1\nb = 2\nc = 3\n")
        self.assertEqual(decode_text("x\r\ny".encode("utf-16")), "x\ny")

    def test_E3_bytes_that_are_not_text_raise_a_value_error(self):
        # ValueError is what the config loader turns into "not readable JSON".
        for data in (b"\xff\xfe\x00", b"\x80abc", b"\xef\xbb\xbf\xff"):
            with self.assertRaises(ValueError, msg=repr(data)):
                decode_text(data)


if __name__ == "__main__":
    unittest.main()
