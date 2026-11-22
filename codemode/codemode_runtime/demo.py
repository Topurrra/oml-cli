# SPDX-License-Identifier: MIT
"""The zero-config demo.

``python main.py`` runs this. It needs no configuration, no network and no
model: the "agent" is a fixed list of programs, and the tools are the built-in
notes store. Each step shows one idea the application is built on, and each
step is checked, so the demo is also a smoke test: it exits 0 only if every
check holds.
"""

from __future__ import annotations

import json
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any, Dict, List

from . import launcher
from .config import DEMO_CONFIG, config_from_dict
from .runtime import Runtime, summarize_stats

DISCOVER = 'search("notes")'

COMPOSE = '''\
notes = tools.demo.list_notes()
bodies = parallel([("demo.read_note", {"id": n["id"]}) for n in notes])
open_actions = []
for note in bodies:
    for line in note["body"].splitlines():
        if line.startswith("ACTION:"):
            open_actions.append({"note": note["id"], "action": line[7:].strip()})
state["open_actions"] = open_actions
{"notes": len(notes), "open_actions": len(open_actions)}
'''

REUSE = '''\
by_owner = {}
for item in state["open_actions"]:
    owner = item["action"].split(" to ")[0]
    by_owner[owner] = by_owner.get(owner, 0) + 1
dict(sorted(by_owner.items()))
'''

WRITE = '''\
before = len(tools.demo.list_notes())
lines = ["- %s (note %d)" % (a["action"], a["note"]) for a in state["open_actions"]]
created = tools.demo.create_note(title="Open actions", body="\\n".join(lines), tags=["generated"])
{"created": created["id"], "notes_before": before}
'''

DENIED = '''\
try:
    tools.demo.delete_note(id=1)
    outcome = "deleted"
except ToolError as error:
    outcome = error.kind
outcome
'''

SNIPPET = 'run("actions_by_owner")'


def run_demo(as_json: bool = False, keep: bool = False) -> int:
    home = Path(tempfile.mkdtemp(prefix="codemode-demo-"))
    runtime = Runtime(config_from_dict(DEMO_CONFIG, "built-in demo config"), home, agent="demo")
    steps: List[Dict[str, Any]] = []
    checks: List[Dict[str, Any]] = []

    def say(text: str = "") -> None:
        if not as_json:
            sys.stdout.write(text + "\n")

    def check(name: str, ok: bool) -> None:
        checks.append({"check": name, "ok": bool(ok)})
        say("  [%s] %s" % ("ok" if ok else "FAIL", name))

    def step(title: str, why: str, code: str) -> Dict[str, Any]:
        say()
        say("== %s" % title)
        say(why)
        say()
        for line in code.rstrip().splitlines():
            say("    " + line)
        payload = runtime.execute(code)
        show(payload)
        steps.append({"title": title, "code": code, "payload": payload})
        return payload

    def show(payload: Dict[str, Any]) -> None:
        say()
        say("  -> status: %s" % payload["status"])
        if "result" in payload:
            say("  -> result: %s" % json.dumps(payload["result"], ensure_ascii=True)[:400])
        for item in payload.get("pending", []):
            say("  -> pending: %s (policy: ask)" % item["tool"])
        if payload.get("stats"):
            say("  -> %s" % summarize_stats(payload["stats"], payload))

    try:
        say("codemode-runtime demo. Data directory: %s" % home)
        say("The agent has one tool, execute(code). Each step below is one call to it.")

        found = step(
            "1. Discover",
            "No tool schema was loaded up front. The program asks for what it needs.",
            DISCOVER,
        )
        check("search returned tool signatures", any(h["path"] == "demo.read_note" for h in found.get("result", [])))

        composed = step(
            "2. Compose",
            "One program lists the notes, reads all of them in parallel, and returns two numbers.\n"
            "The note bodies never leave the sandbox.",
            COMPOSE,
        )
        stats = composed.get("stats", {})
        check("11 tool calls in one round trip", stats.get("toolCalls") == 11)
        check(
            "tool results stayed in the sandbox (%d bytes in, %d returned)"
            % (stats.get("toolResultBytes", 0), len(json.dumps(composed.get("result")))),
            stats.get("toolResultBytes", 0) > 20 * len(json.dumps(composed.get("result"))),
        )

        reused = step(
            "3. Reuse state",
            "The previous program kept what it found in state. This one makes no tool call at all.",
            REUSE,
        )
        check("no tool call was needed", reused.get("stats", {}).get("toolCalls") == 0)
        check("state carried the data over", reused.get("result") == {"Jonas": 3, "Mara": 2, "Priya": 3})

        paused = step(
            "4. A write waits for a human",
            "create_note is not on the allow list, so the policy default (ask) applies.\n"
            "The execution pauses before the call runs.",
            WRITE,
        )
        check("the execution paused", paused["status"] == "paused")
        check("nothing was written while paused", not (home / "demo" / "notes.json").exists())

        say()
        say("   A human now runs: %s approve %s" % (launcher.program_name(), paused["executionId"]))
        say("   (the demo does it for them)")
        resumed = runtime.approve(paused["executionId"], by="demo-human")
        show(resumed)
        steps.append({"title": "4b. Approve and resume", "payload": resumed})
        check("the resumed execution completed", resumed["status"] == "completed")
        check(
            "the read before the write was replayed, not run again",
            resumed.get("stats", {}).get("replayed") == 1,
        )
        check("the note was created once", resumed.get("result") == {"created": 11, "notes_before": 10})

        denied = step(
            "5. A denied call fails the call, not the program",
            "delete_note is on the deny list. The program catches the error and carries on.",
            DENIED,
        )
        check("the program saw PolicyDenied and finished", denied.get("result") == "PolicyDenied")

        say()
        say("== 6. Verify by replay")
        say("Re-run step 4 from its log alone. No tool is called; the result must match.")
        report = runtime.verify(resumed["executionId"])
        say("  -> %s: %s" % (report["verification"], report["detail"]))
        steps.append({"title": "6. Verify by replay", "payload": report})
        check("replay reproduced the recorded result", report["verified"])

        say()
        say("== 7. Promote a program to a snippet")
        say("The host promotes step 3; later programs call it by name.")
        runtime.save_snippet("actions_by_owner", reused["executionId"], "Count open actions per owner from state.")
        snippet = step("7b. Run the snippet", "", SNIPPET)
        check("the snippet returned the same answer", snippet.get("result") == reused.get("result"))

        passed = all(c["ok"] for c in checks)
        say()
        say("%d of %d checks passed." % (sum(c["ok"] for c in checks), len(checks)))
        say("Next: %s --help    |    %s serve  (MCP server)" % (launcher.program_name(), launcher.program_name()))
        if as_json:
            sys.stdout.write(json.dumps({"status": "completed" if passed else "error", "checks": checks, "steps": steps}, ensure_ascii=True) + "\n")
        return 0 if passed else 1
    finally:
        runtime.close()
        if keep:
            say("Kept: %s" % home)
        else:
            shutil.rmtree(home, ignore_errors=True)
