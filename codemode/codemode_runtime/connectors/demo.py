# SPDX-License-Identifier: MIT
"""The demo connector: a small notes store.

It exists so the application can be tried with no configuration, no network
and no model. It has read tools, write tools and a destructive tool, which is
enough to show discovery, composition, approval and denial. The store is one
JSON file under the data directory, seeded on first use.
"""

from __future__ import annotations

import json
import os
import threading
from pathlib import Path
from typing import Any, Dict, List

from ..errors import ToolFailure
from . import CallContext, Connector

_SEED = [
    ("Kickoff: Lighthouse migration", ["meeting", "lighthouse"],
     "Attendees: Mara, Jonas, Priya. We agreed to move the billing exports from the nightly cron "
     "to the event pipeline in three phases. Phase one covers invoices only. Jonas raised that the "
     "legacy exporter silently drops rows with a null currency, which nobody had written down.\n"
     "ACTION: Jonas to document the null-currency behaviour before phase one starts.\n"
     "ACTION: Priya to get a staging copy of last quarter's exports."),
    ("Incident 2291: duplicate invoices", ["incident", "billing"],
     "Between 02:10 and 02:40 the exporter retried a batch after a timeout and wrote 318 invoices "
     "twice. Root cause: the retry key was derived from the wall clock, not from the batch id, so "
     "the second attempt looked like new work. Customers were not charged twice; only the export "
     "file was wrong.\n"
     "ACTION: Mara to derive the retry key from the batch id.\n"
     "DONE: Finance was told which file to discard."),
    ("Design note: idempotent exports", ["design", "lighthouse"],
     "Every export row gets a key of (tenant, invoice id, revision). The sink upserts on that key. "
     "A replay of any batch is then harmless, which is the property the incident showed we lack. "
     "Open question: revisions are not monotonic for credit notes, so the key may need the "
     "document type as well.\n"
     "ACTION: Priya to check how credit-note revisions are numbered."),
    ("Weekly sync, week 14", ["meeting"],
     "Phase one is two days behind because the staging copy arrived late. The null-currency rows "
     "turned out to be 0.4 percent of volume, all from one reseller. Agreed to export them with an "
     "explicit UNKNOWN currency instead of dropping them.\n"
     "DONE: Staging copy delivered.\n"
     "ACTION: Jonas to add the UNKNOWN currency case to the exporter tests."),
    ("Vendor call: warehouse limits", ["vendor"],
     "The warehouse vendor confirmed a limit of 500 concurrent load jobs per account and a soft "
     "limit of 2 GB per file. Our largest tenant produces a 3.1 GB invoice file at quarter end. "
     "They suggested splitting by month.\n"
     "ACTION: Mara to split quarter-end files by month."),
    ("Retro: phase one", ["meeting", "lighthouse", "retro"],
     "What went well: the replay test caught two ordering bugs before launch. What did not: we "
     "found out about the 2 GB limit from a failed load, not from the vendor notes, because nobody "
     "re-read them. Decision: limits go in the runbook, not in meeting notes.\n"
     "ACTION: Priya to move vendor limits into the runbook."),
    ("Runbook: export backfill", ["runbook", "billing"],
     "To backfill a tenant: pause the tenant's stream, run the exporter with --from and --to, "
     "compare row counts against the ledger, then resume the stream. Never backfill two tenants "
     "at once on the shared queue. Expected duration is about four minutes per million rows."),
    ("Idea: per-tenant export health page", ["idea"],
     "A page that shows, per tenant, the last successful export, the row count, and the delta "
     "against the ledger. Support asks for this weekly. It needs no new data, only a view over "
     "what the exporter already logs."),
    ("Weekly sync, week 15", ["meeting"],
     "Phase two starts Monday and covers credit notes. Credit-note revisions restart at 1 for each "
     "document type, so the idempotency key does need the type. Mara's retry-key fix is merged.\n"
     "DONE: Retry key derived from the batch id.\n"
     "DONE: Credit-note revision numbering checked.\n"
     "ACTION: Jonas to extend the idempotency key with the document type."),
    ("Personal: reading list", ["personal"],
     "Papers to read when phase two is out: the one on deterministic replay in workflow engines, "
     "and the survey on exactly-once delivery. Neither is urgent."),
]

_TOOLS: List[Dict[str, Any]] = [
    {
        "name": "list_notes",
        "description": "List every note: id, title, tags and word count. Bodies are not included.",
        "inputSchema": {"type": "object", "properties": {}, "additionalProperties": False},
        "outputSchema": {"type": "array", "items": {"type": "object"}},
    },
    {
        "name": "read_note",
        "description": "Return one note with its full body.",
        "inputSchema": {
            "type": "object",
            "properties": {"id": {"type": "integer", "description": "Note id from list_notes."}},
            "required": ["id"],
            "additionalProperties": False,
        },
        "outputSchema": {"type": "object"},
    },
    {
        "name": "search_notes",
        "description": "Find notes whose title, tags or body contain the query (case-insensitive).",
        "inputSchema": {
            "type": "object",
            "properties": {"query": {"type": "string"}},
            "required": ["query"],
            "additionalProperties": False,
        },
        "outputSchema": {"type": "array", "items": {"type": "object"}},
    },
    {
        "name": "create_note",
        "description": "Create a note. Returns its id.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "title": {"type": "string"},
                "body": {"type": "string"},
                "tags": {"type": "array", "items": {"type": "string"}, "default": []},
            },
            "required": ["title", "body"],
            "additionalProperties": False,
        },
        "outputSchema": {"type": "object"},
    },
    {
        "name": "update_note",
        "description": "Replace the title, body or tags of a note. Fields left out are kept.",
        "inputSchema": {
            "type": "object",
            "properties": {
                "id": {"type": "integer"},
                "title": {"type": "string"},
                "body": {"type": "string"},
                "tags": {"type": "array", "items": {"type": "string"}},
            },
            "required": ["id"],
            "additionalProperties": False,
        },
        "outputSchema": {"type": "object"},
    },
    {
        "name": "delete_note",
        "description": "Delete a note permanently.",
        "inputSchema": {
            "type": "object",
            "properties": {"id": {"type": "integer"}},
            "required": ["id"],
            "additionalProperties": False,
        },
        "outputSchema": {"type": "object"},
    },
]


class DemoConnector(Connector):
    def __init__(self, name: str, home: Path) -> None:
        self.name = name
        self._path = Path(home) / "demo" / "notes.json"
        self._lock = threading.Lock()  # parallel() calls arrive on several threads

    def list_tools(self) -> List[Dict[str, Any]]:
        return _TOOLS

    # -- storage ----------------------------------------------------------

    def _load(self) -> Dict[str, Any]:
        if not self._path.is_file():
            notes = [
                {"id": i, "title": title, "tags": tags, "body": body}
                for i, (title, tags, body) in enumerate(_SEED, start=1)
            ]
            return {"next_id": len(notes) + 1, "notes": notes}
        return json.loads(self._path.read_text(encoding="utf-8"))

    def _save(self, data: Dict[str, Any]) -> None:
        self._path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self._path.with_name(self._path.name + ".tmp")
        tmp.write_text(json.dumps(data, ensure_ascii=True, indent=1), encoding="utf-8")
        os.replace(tmp, self._path)  # readers never see a half-written file

    # -- tools ------------------------------------------------------------

    def call(self, tool: str, args: Dict[str, Any], ctx: CallContext) -> Any:
        handler = getattr(self, "_tool_" + tool, None)
        if handler is None:
            raise ToolFailure("UnknownTool", "the demo connector has no tool %r" % tool)
        with self._lock:
            return handler(args)

    @staticmethod
    def _arg(args: Dict[str, Any], name: str, kind: type, required: bool = True) -> Any:
        if name not in args:
            if required:
                raise ToolFailure("BadArguments", "missing required argument %r" % name)
            return None
        value = args[name]
        if isinstance(value, bool) or not isinstance(value, kind):
            raise ToolFailure(
                "BadArguments", "argument %r must be %s, got %s" % (name, kind.__name__, type(value).__name__)
            )
        return value

    def _find(self, data: Dict[str, Any], note_id: int) -> Dict[str, Any]:
        for note in data["notes"]:
            if note["id"] == note_id:
                return note
        raise ToolFailure("NotFound", "no note with id %d" % note_id)

    @staticmethod
    def _meta(note: Dict[str, Any]) -> Dict[str, Any]:
        return {"id": note["id"], "title": note["title"], "tags": note["tags"], "words": len(note["body"].split())}

    def _tool_list_notes(self, args: Dict[str, Any]) -> Any:
        return [self._meta(note) for note in self._load()["notes"]]

    def _tool_read_note(self, args: Dict[str, Any]) -> Any:
        return dict(self._find(self._load(), self._arg(args, "id", int)))

    def _tool_search_notes(self, args: Dict[str, Any]) -> Any:
        query = self._arg(args, "query", str).lower()
        hits = []
        for note in self._load()["notes"]:
            text = " ".join([note["title"], " ".join(note["tags"]), note["body"]])
            at = text.lower().find(query)
            if at >= 0:
                hits.append({"id": note["id"], "title": note["title"], "snippet": text[max(0, at - 40) : at + 80]})
        return hits

    def _tool_create_note(self, args: Dict[str, Any]) -> Any:
        title = self._arg(args, "title", str)
        body = self._arg(args, "body", str)
        tags = self._arg(args, "tags", list, required=False) or []
        data = self._load()
        note = {"id": data["next_id"], "title": title, "tags": [str(t) for t in tags], "body": body}
        data["notes"].append(note)
        data["next_id"] += 1
        self._save(data)
        return {"id": note["id"]}

    def _tool_update_note(self, args: Dict[str, Any]) -> Any:
        data = self._load()
        note = self._find(data, self._arg(args, "id", int))
        for name, kind in (("title", str), ("body", str), ("tags", list)):
            value = self._arg(args, name, kind, required=False)
            if value is not None:
                note[name] = value
        self._save(data)
        return self._meta(note)

    def _tool_delete_note(self, args: Dict[str, Any]) -> Any:
        data = self._load()
        note = self._find(data, self._arg(args, "id", int))
        data["notes"].remove(note)
        self._save(data)
        return {"deleted": note["id"]}
