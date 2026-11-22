# SPDX-License-Identifier: MIT
"""What an agent needs to know about the oml connector, for `oml code prompt`, the `execute` tool of
`oml code serve` and `oml code instructions`. Written for small models: short lines, one way to do each thing,
a worked example. The runtime's own text (codemode_runtime/) stays as it is; this is added after it."""

import json

# Used when the server cannot be asked for its own descriptions.
_FALLBACK = [
    ("read", "commands that only read: inspect, describe, find, query, tree, flow, stats, header, export, check, review, "
             "architecture, map, doc, spec, explain-warning, memory recall/list, batch."),
    ("write", "commands that write a new file: new, apply (to a staged copy), migrate, spec check, memory add/retract."),
    ("gated", "install, publish, references, pull, lifetime, server, review --server."),
]

_POLICY = {"allow": "runs at once", "ask": "pauses until the user approves", "deny": "denied"}
_DEFAULT_POLICY = {"read": "allow", "write": "allow", "gated": "ask"}

_BODY = """\
Each takes one argument, args: the oml command line as a list of strings, without "oml". It returns
{"exit": 0, "output": ..., "stderr": "..."}; output is oml's JSON, sometimes still as text.
These forms work (nothing else; never tools.oml.read("inspect", ...) or tools.oml.read(["inspect", ...])):
  r = tools.oml.read(args=["inspect", "Orders.oml", "--own"])
  r = call("oml.read", {"args": ["inspect", "Orders.oml", "--own"]})
  rs = parallel([("oml.read", {"args": ["describe", "A.oml", "GetOrders"]}), ("oml.read", {"args": ["find", "Order*", "A.oml"]})])
parallel() takes pairs of ("oml.<tool>", {"args": [...]}), never lambdas or calls.

The easy way, helpers.oml (parses output, raises ToolError when the command failed):
  oml = helpers.oml
  info = oml.read("inspect", "Orders.oml", "--only", "entities")
  a, b = oml.many([("read", ["check", "A.oml"]), ("read", ["check", "B.oml"])])   # parallel
  outs = [oml.J(r["output"]) for r in rs]   # J parses JSON text, returns anything else as it is

Rules:
- Paths are relative to the project folder; use forward slashes.
- The server keeps a module open after the first read, so reading it again is cheap.
- One program for the whole step: read, filter, decide, write. Independent calls go in one many() or parallel().
- Return only what you need: counts, names, the few fields you will use. Never return whole inspect, export or check output.
- A gated call pauses the program: tell the user what is pending and the approve command from the hint. Several
  independent gated calls in one many() or parallel() pause once and are approved together."""


def prompt(tools):
    """The oml section of the `execute` tool description. ``tools``: [(name, description, policy)] from the server
    and the policy; empty when the server cannot be asked."""
    fallback = dict(_FALLBACK)
    lines = ["The oml namespace runs the oml command line (OutSystems 11 modules). Its tools:"]
    for name, text, action in tools or [(n, t, _DEFAULT_POLICY.get(n)) for n, t in _FALLBACK]:
        summary = " ".join(str(text or fallback.get(name, "")).split())
        if len(summary) > 600:
            summary = summary[:597] + "..."
        policy = _POLICY.get(action or "")
        lines.append("- oml.%s: %s%s" % (name, summary, (" (%s)" % policy) if policy else ""))
    return "\n".join(lines) + "\n" + _BODY


_SECTION = """\
### The oml connector

{prompt}

Use `oml code run` instead of single `oml` calls when a step needs three or more oml commands, or when you would
read a large output only to pick a few fields from it. One call, or one `oml batch` that returns little: run oml
directly.

Example, `.oml/code/entities.py`:

```python
oml = helpers.oml
files = ["modules/Orders.oml", "modules/Customers.oml"]
infos = oml.many([("read", ["inspect", f, "--only", "entities", "--own"]) for f in files])
{{f: [e["name"] for e in info.get("entities", [])] for f, info in zip(files, infos)}}
```
"""


def instructions(block, surface):
    """The runtime's instructions block with oml's folder for programs and the oml section added."""
    block = block.replace(".codemode-work/", ".oml/code/").replace("`.codemode/` folder", "`.oml/codemode/` folder")
    return block.rstrip("\n") + "\n\n" + _SECTION.format(prompt=prompt([])).rstrip("\n") + "\n"


def drop_own_connector(path, report):
    """`oml code init` read the project's MCP server list. oml adds its own `oml` connector and rules, so a server
    called oml in that list is left out of `.oml/codemode.json`, with the rules init wrote for it."""
    with open(path, encoding="utf-8") as handle:
        raw = json.load(handle)
    if "oml" not in raw.get("connectors", {}):
        return
    del raw["connectors"]["oml"]
    policy = raw.get("policy") or {}
    for key in ("allow", "ask", "deny"):
        if isinstance(policy.get(key), list):
            policy[key] = [r for r in policy[key] if not _for_oml(r)]
            if isinstance(report.get("policy", {}).get(key), list):
                report["policy"][key] = [r for r in report["policy"][key] if not _for_oml(r)]
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(json.dumps(raw, indent=2, ensure_ascii=True) + "\n")
    report.get("connectors", {}).pop("oml", None)
    report.setdefault("notes", []).append("The server list names an oml server; it is left out: oml code adds its own oml connector.")


def _for_oml(rule):
    tool = rule.get("tool") if isinstance(rule, dict) else rule
    return isinstance(tool, str) and tool.startswith("oml.")
