# SPDX-License-Identifier: MIT
"""The tool catalog: names, search, describe, resolve.

Tool schemas never go into the model's context up front. The program asks the
catalog at run time: ``search`` returns a few one-line signatures,
``describe`` returns one full schema. A catalog of a thousand tools therefore
costs the model nothing until it looks something up.

Connectors are loaded lazily and one at a time. A server that is down makes
its own namespace unavailable, with the reason, and leaves the rest working.
"""

from __future__ import annotations

import difflib
import json
import keyword
import os
import re
import threading
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable, Dict, Iterable, List, Optional, Tuple

from .connectors import Connector, redact
from .errors import ToolFailure
from .policy import Policy
from .schema_sig import signature

MAX_SEARCH_RESULTS = 200
DEFAULT_SEARCH_RESULTS = 10  # a query: the best few matches
DEFAULT_LISTING_RESULTS = 50  # no query: "what is there?"
_SUMMARY_CHARS = 160
_DESCRIPTION_CHARS = 4000  # of one tool, in describe()


def sanitize_name(name: str) -> str:
    """Make a tool name usable as ``tools.ns.<name>``.

    The result is always a valid identifier that does not start with an
    underscore, so it can never collide with a private attribute of the proxy
    object in the sandbox.
    """
    cleaned = re.sub(r"[^0-9A-Za-z]+", "_", str(name)).strip("_")
    if not cleaned:
        cleaned = "tool"
    if cleaned[0].isdigit():
        cleaned = "t_" + cleaned
    if keyword.iskeyword(cleaned):
        cleaned += "_"
    return cleaned


def assign_names(originals: Iterable[str]) -> Dict[str, str]:
    """Map each original tool name to a unique sanitized one.

    Names are processed in sorted order so that a collision resolves the same
    way no matter what order the server listed its tools in.
    """
    used: Dict[str, str] = {}
    out: Dict[str, str] = {}
    for original in sorted(set(originals)):
        base = sanitize_name(original)
        candidate, n = base, 1
        while candidate in used:
            n += 1
            candidate = "%s_%d" % (base, n)
        used[candidate] = original
        out[original] = candidate
    return out


@dataclass(frozen=True)
class ToolEntry:
    connector: str
    name: str  # sanitized: what the program writes
    original: str  # what the connector calls it
    description: str
    input_schema: Any
    output_schema: Any

    @property
    def path(self) -> str:
        return "%s.%s" % (self.connector, self.name)

    @property
    def original_path(self) -> str:
        """The same tool under the server's own spelling of its name."""
        return "%s.%s" % (self.connector, self.original)

    def signature(self, max_params: Optional[int] = None) -> str:
        return signature(self.path, self.input_schema, self.output_schema, max_params)


def _summary(text: str) -> str:
    line = " ".join(str(text or "").split())
    return line if len(line) <= _SUMMARY_CHARS else line[: _SUMMARY_CHARS - 3] + "..."


def _whole(text: str) -> str:
    """A description as its author wrote it, up to the size kept for one
    tool; past that it is cut and says by how much."""
    text = str(text or "")
    if len(text) <= _DESCRIPTION_CHARS:
        return text
    return "%s [... %d more chars of description not shown ...]" % (text[:_DESCRIPTION_CHARS], len(text) - _DESCRIPTION_CHARS)


def _tokens(text: str) -> List[str]:
    return re.findall(r"[a-z0-9]+", text.lower())


def default_limit(query: str) -> int:
    """How many results a search returns when the caller gave no limit."""
    return DEFAULT_SEARCH_RESULTS if _tokens(str(query)) else DEFAULT_LISTING_RESULTS


def _related(a: str, b: str) -> bool:
    """A cheap stand-in for stemming: "notes" finds "note" and "read" finds
    "reader". Short fragments are excluded so "a" does not match everything."""
    return min(len(a), len(b)) >= 3 and (a in b or b in a)


def _score(wanted: List[str], name_tokens: set, text_tokens: set) -> int:
    score = 0
    for token in wanted:
        if token in name_tokens:
            score += 4
        elif any(_related(token, t) for t in name_tokens):
            score += 3
        elif token in text_tokens:
            score += 2
        elif any(_related(token, t) for t in text_tokens):
            score += 1
    return score


def _pruned(node: Any, depth: int, description_chars: int) -> Any:
    """A copy of a schema node with descriptions cut to ``description_chars``
    (0 drops them) and everything below ``depth`` replaced by its type."""
    if isinstance(node, list):
        return [_pruned(item, depth, description_chars) for item in node[:50]]
    if not isinstance(node, dict):
        return node
    if depth <= 0:
        return {"type": node.get("type", "object")} if ("properties" in node or "items" in node or "type" in node) else {}
    out: Dict[str, Any] = {}
    for key, value in node.items():
        if key == "description" and isinstance(value, str):
            if description_chars and value:
                out[key] = value if len(value) <= description_chars else value[:description_chars] + "..."
        elif key in ("properties", "$defs", "definitions") and isinstance(value, dict):
            out[key] = {name: _pruned(sub, depth - 1, description_chars) for name, sub in value.items()}
        elif isinstance(value, (dict, list)):
            out[key] = _pruned(value, depth - 1, description_chars)
        else:
            out[key] = value
    return out


def fit_schema(schema: Any, budget: int) -> Tuple[Any, int]:
    """Make a schema fit ``budget`` characters of JSON. Returns the schema
    and, when it had to be shortened, its original size (0 otherwise).

    Shortening goes in steps and stops at the first that fits: shorter
    descriptions, then no descriptions, then fewer nested levels. The last
    step keeps only the top-level parameter names and their types.
    """
    try:
        size = len(json.dumps(schema, ensure_ascii=True))
    except (TypeError, ValueError):
        return {}, 1
    if size <= budget:
        return schema, 0
    for depth, description_chars in ((8, 300), (6, 80), (4, 0), (3, 0), (2, 0)):
        candidate = _pruned(schema, depth, description_chars)
        if len(json.dumps(candidate, ensure_ascii=True)) <= budget:
            return candidate, size
    props = schema.get("properties") if isinstance(schema, dict) and isinstance(schema.get("properties"), dict) else {}
    names: Dict[str, Any] = {}
    frame = {"type": "object", "properties": names, "note": "%d properties in all" % len(props)}
    for name, prop in props.items():
        names[name] = {"type": prop.get("type", "any")} if isinstance(prop, dict) else {}
        if len(json.dumps(frame, ensure_ascii=True)) > budget:
            names.pop(name)
            break
    return frame, size


class ToolListCache:
    """Tool lists on disk, so that looking tools up does not have to start
    every server.

    An entry is used only while it is younger than ``ttl_s`` and the
    connector's stamp is unchanged. The stamp covers the connector's settings
    and the local files its command names, so editing a project's own server
    script drops the entry at once. A server whose tools change some other
    way is caught by the time limit, or when a program calls a tool the entry
    does not have: the catalog then asks the server.
    """

    def __init__(self, folder: Path, ttl_s: float) -> None:
        self._folder = Path(folder)
        self._ttl = float(ttl_s)

    def _path(self, name: str) -> Path:
        return self._folder / ("tools-%s.json" % name)

    def load(self, name: str, stamp: str) -> Optional[List[Dict[str, Any]]]:
        if self._ttl <= 0:
            return None
        try:
            entry = json.loads(self._path(name).read_text(encoding="ascii"))
            fresh = time.time() - float(entry["saved"]) < self._ttl
            if entry["stamp"] != stamp or not fresh or not isinstance(entry["tools"], list):
                return None
            return entry["tools"]
        except (OSError, ValueError, KeyError, TypeError):
            return None

    def save(self, name: str, stamp: str, tools: List[Dict[str, Any]]) -> None:
        if self._ttl <= 0:
            return
        try:
            self._folder.mkdir(parents=True, exist_ok=True)
            tmp = self._path(name).with_name("%s.%d.tmp" % (self._path(name).name, os.getpid()))
            tmp.write_text(json.dumps({"saved": time.time(), "stamp": stamp, "tools": tools}, ensure_ascii=True), encoding="ascii")
            os.replace(tmp, self._path(name))
        except (OSError, TypeError, ValueError):
            pass  # a cache that cannot be written is only a slower start

    def clear(self) -> None:
        try:
            for path in self._folder.glob("tools-*.json"):
                path.unlink()
        except OSError:
            pass


class Catalog:
    def __init__(
        self,
        connectors: Dict[str, Connector],
        policy: Policy,
        list_snippets: Callable[[], List[Dict[str, Any]]],
        max_concurrency: Optional[Dict[str, int]] = None,
        cache: Optional["ToolListCache"] = None,
        no_config_hint: str = "",
        schema_chars: int = 40000,
        list_helpers: Optional[Callable[[], List[Dict[str, Any]]]] = None,
    ) -> None:
        self._schema_chars = int(schema_chars)
        self._list_helpers = list_helpers or (lambda: [])
        self._connectors = connectors
        self._policy = policy
        self._list_snippets = list_snippets
        self._cache = cache
        self._no_config_hint = no_config_hint
        self._entries: Dict[str, Dict[str, ToolEntry]] = {}
        self._stamps: Dict[str, str] = {}
        self.cached: set = set()  # namespaces whose tool list came from the disk cache
        self.unavailable: Dict[str, str] = {}
        self._hints: Dict[str, str] = {}
        # One gate per connector that set max_concurrency. A call waits at
        # the gate, and its time limit starts only once it is through.
        self._gates = {name: threading.BoundedSemaphore(count) for name, count in (max_concurrency or {}).items() if count}
        self._max_concurrency = dict(max_concurrency or {})

    # -- loading ----------------------------------------------------------

    def _load(self, name: str, live: bool = False) -> Dict[str, ToolEntry]:
        """The tools of one namespace. From memory, else from the disk cache
        (unless ``live``), else from the connector, which starts its server."""
        if name in self._entries and not live:
            return self._entries[name]
        if name in self.unavailable and not live:
            raise ToolFailure("ConnectorUnavailable", self.unavailable[name], self._hints.get(name, ""))
        connector = self._connectors[name]
        stamp = self._stamp(connector)
        cache = self._cache if getattr(connector, "cacheable", False) else None
        tools = None if (live or cache is None) else cache.load(name, stamp)
        if tools is not None:
            self.cached.add(name)
        else:
            self.cached.discard(name)
            try:
                tools = connector.list_tools()
            except ToolFailure as exc:
                # The message may quote what the server printed while it
                # failed, and a server may print what it was given.
                message = redact(exc.message, connector.secrets())[0]
                self.unavailable[name] = message
                self._hints[name] = exc.hint
                raise ToolFailure("ConnectorUnavailable", message, exc.hint)
            except Exception as exc:  # a connector bug must not take the catalog down
                self.unavailable[name] = "%s: %s" % (type(exc).__name__, exc)
                raise ToolFailure("ConnectorUnavailable", self.unavailable[name])
            self.unavailable.pop(name, None)
            if cache is not None:
                cache.save(name, stamp, tools)
        self._stamps[name] = stamp
        names = assign_names(t["name"] for t in tools)
        entries = {}
        for tool in tools:
            entry = ToolEntry(
                connector=name,
                name=names[tool["name"]],
                original=tool["name"],
                description=str(tool.get("description") or ""),
                input_schema=tool.get("inputSchema"),
                output_schema=tool.get("outputSchema"),
            )
            entries[entry.name] = entry
        self._entries[name] = entries
        return entries

    @staticmethod
    def _stamp(connector: Connector) -> str:
        try:
            return str(connector.stamp())
        except Exception:
            return ""

    def preload(self, names: Iterable[str]) -> None:
        """Load several namespaces side by side: five servers that take a
        second each to start cost a second, not five."""
        missing = [n for n in dict.fromkeys(names) if n in self._connectors and n not in self._entries and n not in self.unavailable]
        if len(missing) < 2:
            return

        def attempt(name: str) -> None:
            try:
                self._load(name)
            except ToolFailure:
                pass

        threads = [threading.Thread(target=attempt, args=(name,), daemon=True) for name in missing]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join()

    def _all(self) -> List[ToolEntry]:
        self.preload(self._connectors)
        out: List[ToolEntry] = []
        for name in self._connectors:
            try:
                out.extend(self._load(name).values())
            except ToolFailure:
                continue
        return out

    def refresh_changed(self) -> List[str]:
        """Forget the tools of every connector whose server changed on disk
        since they were listed, and stop that server so the next call starts
        the new code. A long-lived process (the MCP server mode) calls this
        before each execution: a project that edits its own server must not
        keep talking to the old one."""
        changed = []
        for name, connector in self._connectors.items():
            if name in self._stamps and self._stamp(connector) != self._stamps[name]:
                changed.append(name)
                self._entries.pop(name, None)
                self._stamps.pop(name, None)
                self.unavailable.pop(name, None)
                try:
                    connector.close()
                except Exception:
                    pass
        for name in list(self.unavailable):
            # A server that was down gets another chance on the next use.
            self.unavailable.pop(name, None)
        return changed

    def namespaces(self) -> Dict[str, Any]:
        """Namespace to tool count, or to the reason it is unavailable."""
        self._all()
        return {
            name: (len(self._entries[name]) if name in self._entries else "unavailable: " + self.unavailable.get(name, ""))
            for name in self._connectors
        }

    # -- lookup -----------------------------------------------------------

    def resolve(self, path: str) -> ToolEntry:
        connector, _, tool = str(path).partition(".")
        if connector not in self._connectors:
            raise ToolFailure(
                "UnknownTool",
                "no namespace %r. Namespaces: %s. Use search() to find tools."
                % (connector, ", ".join(sorted(self._connectors)) or "(none)"),
                self._no_config_hint,
            )
        entries = self._load(connector)
        if tool not in entries and connector in self.cached:
            # The list came from the cache and the server may have gained
            # the tool since. Ask the server before saying it does not exist.
            entries = self._load(connector, live=True)
        if tool in entries:
            return entries[tool]
        close = difflib.get_close_matches(tool, list(entries), n=3, cutoff=0.5)
        for entry in entries.values():
            if entry.original == tool and entry.name not in close:
                close.insert(0, entry.name)  # they used the server's spelling
        raise ToolFailure(
            "UnknownTool",
            "no tool %r in %r.%s Use search() to find tools."
            % (tool, connector, (" Did you mean: %s?" % ", ".join("%s.%s" % (connector, c) for c in close)) if close else ""),
        )

    def search(
        self, query: str, limit: Optional[int] = None, namespace: Optional[str] = None, full: bool = False
    ) -> List[Dict[str, Any]]:
        return self.search_with_total(query, limit, namespace, full)[0]

    def search_with_total(
        self, query: str, limit: Optional[int] = None, namespace: Optional[str] = None, full: bool = False
    ) -> Tuple[List[Dict[str, Any]], int]:
        """Rank tools and snippets by word overlap. Deliberately simple: the
        ranking is easy to predict and needs no index or model.

        Returns the hits and the number of matches there are in all, so the
        caller can say when the list stops short.

        ``namespace`` keeps the search to one connector's tools, or to the
        project's helper functions for ``helpers``. With an empty query that
        is the list of what one server offers, each with its description and
        its policy, in the server's own order.

        A description is one line, cut to fit a listing. ``full`` gives it
        whole, as ``describe()`` does, for a caller that builds something out
        of the list (a prompt, a table) and would otherwise ask once per tool.
        """
        limit = max(1, min(int(default_limit(query) if limit is None else limit), MAX_SEARCH_RESULTS))
        wanted = _tokens(str(query))
        shown = _whole if full else _summary
        if namespace is not None:
            return self._search_namespace(str(namespace), wanted, limit, shown)
        scored = []
        for entry in self._all():
            score = _score(wanted, set(_tokens(entry.path)), set(_tokens(entry.description)))
            if score or not wanted:
                scored.append((-score, entry.path, {
                    "path": entry.path,
                    "signature": entry.signature(max_params=8),
                    "description": shown(entry.description),
                    "policy": self._policy.decide(entry.path, None, entry.original_path).action,
                }))
        for snippet in self._list_snippets():
            score = _score(wanted, set(_tokens(snippet["name"])), set(_tokens(snippet.get("description", ""))))
            if score or not wanted:
                scored.append((-score, "~" + snippet["name"], {
                    "path": "snippet:" + snippet["name"],
                    "signature": "run(%r, **args)" % snippet["name"],
                    "description": shown(snippet.get("description", "")),
                    "policy": "allow",
                }))
        scored.extend((-score, "~~" + hit["path"], hit) for score, hit in self._helper_hits(wanted, shown))
        scored.sort(key=lambda item: (item[0], item[1]))
        return [item[2] for item in scored[:limit]], len(scored)

    def _helper_hits(self, wanted: List[str], shown: Any = _summary) -> List[Tuple[int, Dict[str, Any]]]:
        """The project's helper functions that match, each with its score. A
        helper runs in the sandbox; what the policy decides is each tool call
        it makes, so there is no decision to show for it."""
        hits = []
        for module in self._list_helpers():
            rows = [(f["signature"].partition("(")[0], f["signature"], f["doc"]) for f in module["functions"]]
            if module.get("error"):
                rows = [("helpers." + module["module"], "helpers." + module["module"], "This helper file does not parse: " + module["error"])]
            for path, signature, doc in rows:
                score = _score(wanted, set(_tokens(path)), set(_tokens(doc + " " + module.get("doc", ""))))
                if score or not wanted:
                    hits.append((score, {"path": path, "signature": signature, "description": shown(doc), "policy": "helper"}))
        return hits

    def _search_namespace(
        self, namespace: str, wanted: List[str], limit: int, shown: Any = _summary
    ) -> Tuple[List[Dict[str, Any]], int]:
        helpers = namespace == "helpers" and "helpers" not in self._connectors
        if not helpers and namespace not in self._connectors:
            raise ToolFailure(
                "UnknownTool",
                "no namespace %r. Namespaces: %s." % (namespace, ", ".join(sorted(self._connectors)) or "none"),
            )
        rows: List[Tuple[int, int, Dict[str, Any]]] = []
        if helpers:
            rows = [(-score, index, hit) for index, (score, hit) in enumerate(self._helper_hits(wanted, shown))]
        else:
            for entry in self._load(namespace).values():
                score = _score(wanted, set(_tokens(entry.path)), set(_tokens(entry.description)))
                if score or not wanted:
                    rows.append((-score, len(rows), {
                        "path": entry.path,
                        "signature": entry.signature(max_params=8),
                        "description": shown(entry.description),
                        "policy": self._policy.decide(entry.path, None, entry.original_path).action,
                    }))
        rows.sort(key=lambda item: (item[0], item[1]))  # best match first; without a query, the server's own order
        return [item[2] for item in rows[:limit]], len(rows)

    def _describe_helper(self, target: str) -> Dict[str, Any]:
        modules = {module["module"]: module for module in self._list_helpers()}
        _, _, rest = target.partition(".")
        name, _, function = rest.partition(".")
        if not name:
            return {"kind": "helpers", "modules": [
                {"module": m["module"], "doc": m.get("doc", ""), "functions": [f["signature"] for f in m["functions"]], **({"error": m["error"]} if m.get("error") else {})}
                for m in modules.values()
            ]}
        if name not in modules:
            raise ToolFailure("UnknownTool", "no helper module %r. This project has: %s." % (name, ", ".join(sorted(modules)) or "none"))
        module = modules[name]
        if not function:
            return dict({"kind": "helper-module"}, **module)
        for item in module["functions"]:
            if item["name"] == function:
                return {
                    "kind": "helper", "path": "helpers.%s.%s" % (name, function), "signature": item["signature"], "description": item["doc"],
                    "policy": "A helper runs in the sandbox with the program. Each tool call it makes is decided by the policy, one by one.",
                }
        raise ToolFailure(
            "UnknownTool", "helpers.%s has no function %r. It has: %s." % (name, function, ", ".join(f["name"] for f in module["functions"]) or "none")
        )

    def describe(self, target: str) -> Dict[str, Any]:
        target = str(target)
        if (target == "helpers" or target.startswith("helpers.")) and "helpers" not in self._connectors:
            return self._describe_helper(target)
        if target.startswith("snippet:"):
            name = target[len("snippet:"):]
            for snippet in self._list_snippets():
                if snippet["name"] == name:
                    return {"kind": "snippet", **snippet}
            raise ToolFailure("UnknownTool", "no snippet %r" % name)
        if "." not in target:
            if target not in self._connectors:
                raise ToolFailure(
                    "UnknownTool",
                    "no namespace %r. Namespaces: %s." % (target, ", ".join(sorted(self._connectors))),
                )
            entries = self._load(target)
            listing: Dict[str, Any] = {
                "kind": "namespace",
                "namespace": target,
                "tools": [e.signature(max_params=8) for e in list(entries.values())[:MAX_SEARCH_RESULTS]],
                "toolCount": len(entries),
            }
            if len(entries) > MAX_SEARCH_RESULTS:
                listing["toolsNotShown"] = len(entries) - MAX_SEARCH_RESULTS
            return listing
        entry = self.resolve(target)
        decision = self._policy.decide(entry.path, None, entry.original_path)
        description = _whole(entry.description)
        # The three parts share one budget, so that the whole description of
        # a tool fits in a value however large the server made its schema.
        signature = entry.signature()
        if len(signature) > self._schema_chars // 3:
            signature = entry.signature(max_params=40)
        schema, cut = fit_schema(entry.input_schema, max(1000, self._schema_chars - len(signature) - len(description)))
        out: Dict[str, Any] = {
            "kind": "tool",
            "path": entry.path,
            "signature": signature,
            "description": description,
            "inputSchema": schema,
            "policy": decision.action,
        }
        if cut:
            # A schema of hundreds of kilobytes would not fit in a value, and
            # the program would get an error instead of a description.
            out["schemaTruncated"] = True
            out["schemaChars"] = cut
            out["schemaNote"] = "The schema was shortened to fit; the signature lists the parameters with their types."
        if self._policy.depends_on_args(entry.path, entry.original_path):
            # The decision above is for the tool alone. Saying so beats a
            # program that reads "allow" and is then denied for its arguments.
            out["policyDependsOnArguments"] = True
        if entry.output_schema is not None:
            out["outputSchema"] = entry.output_schema
        if entry.original != entry.name:
            out["originalName"] = entry.original
        return out

    def connector_for(self, entry: ToolEntry) -> Connector:
        return self._connectors[entry.connector]

    def gate_for(self, entry: ToolEntry) -> Optional[threading.BoundedSemaphore]:
        return self._gates.get(entry.connector)

    def max_concurrency(self, connector: str) -> Optional[int]:
        return self._max_concurrency.get(connector)

    def close(self) -> None:
        """Stop every connector, side by side: a server that takes its two
        seconds to notice the end of its input should not make the others,
        and the caller, wait in line."""
        def stop(connector: Connector) -> None:
            try:
                connector.close()
            except Exception:
                pass  # closing is best effort; there is nobody left to tell

        threads = [threading.Thread(target=stop, args=(connector,), daemon=True) for connector in self._connectors.values()]
        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=15)
