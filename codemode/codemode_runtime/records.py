# SPDX-License-Identifier: MIT
"""The execution log: one append-only JSONL file per execution.

The log is the source of truth. Status, pending approvals and replay data are
all derived from it by ``ExecutionView``; nothing is stored twice and nothing
is edited in place. That is what makes an execution resumable after a pause
and checkable afterwards: the file says what happened, in order.

Entry types: ``start``, ``pass_start``, ``call``, ``approval``, ``pass_end``,
``end``, ``verify``. A ``call`` entry with a given ``seq`` may appear more than
once (``executing`` then ``applied``, or ``pending`` then ``applied``); the
last one is the current state of that sequence number.
"""

from __future__ import annotations

import calendar
import json
import os
import secrets
import time
from contextlib import contextmanager
from pathlib import Path
from typing import Any, Dict, Iterator, List, Optional, Set

from . import launcher
from .errors import BusyError, CodemodeError, NotFoundError
from .jsonutil import dumps_line
from .procutil import pid_alive

TERMINAL = ("completed", "error", "rejected")
_TS_FORMAT = "%Y-%m-%dT%H:%M:%SZ"


def parse_ts(text: Any) -> Optional[float]:
    """The ``ts`` of a log entry as seconds since the epoch, or None."""
    try:
        return float(calendar.timegm(time.strptime(str(text), _TS_FORMAT)))
    except (TypeError, ValueError):
        return None


def format_ts(seconds: float) -> str:
    """Seconds since the epoch as the ``ts`` a log entry carries."""
    return time.strftime(_TS_FORMAT, time.gmtime(seconds))


class ExecutionView:
    """What a log means right now."""

    def __init__(self, entries: List[Dict[str, Any]]) -> None:
        self.entries = entries
        self.start: Dict[str, Any] = {}
        self.calls: Dict[int, Dict[str, Any]] = {}
        self.approved: Set[int] = set()
        # Approvals a person gave in advance, for calls this execution makes
        # later: which tools ("*" is every tool), who gave it, and when.
        self.standing: List[Dict[str, Any]] = []
        self.passes = 0
        self.end: Optional[Dict[str, Any]] = None
        self.verify: Optional[Dict[str, Any]] = None
        self.policy_changed = False
        self.paused_at: Optional[str] = None
        self.live_logs: List[str] = []  # output persisted while the pass runs
        self.progress: Dict[int, str] = {}  # seq -> the last progress text of that call
        self.pass_pid: Optional[int] = None
        last_pass_status = None
        for entry in entries:
            kind = entry.get("type")
            if kind == "start":
                self.start = entry
            elif kind == "log":
                self.live_logs.append(str(entry.get("text", "")))
            elif kind == "progress":
                self.progress[int(entry.get("seq", 0))] = str(entry.get("text", ""))
            elif kind == "call":
                self.calls[int(entry["seq"])] = entry
            elif kind == "approval":
                self.approved.update(int(s) for s in entry.get("seqs", []))
                self.policy_changed = self.policy_changed or bool(entry.get("policy_changed"))
                if isinstance(entry.get("standing"), list) and entry["standing"]:
                    self.standing.append({"tools": [str(tool) for tool in entry["standing"]], "by": entry.get("by"), "ts": entry.get("ts"), "seqs": set()})
                elif entry.get("via") == "standing":
                    # A call that was let through under one of them. A set:
                    # a call in a batch that paused is let through again on
                    # the next pass, and is still one call.
                    for grant in reversed(self.standing):
                        if grant["ts"] == entry.get("granted"):
                            grant["seqs"].update(int(s) for s in entry.get("seqs", []))
                            break
            elif kind == "pass_start":
                self.passes += 1
                self.pass_pid = entry.get("pid")
                self.live_logs = []  # each pass runs the program from the top
                last_pass_status = None
            elif kind == "pass_end":
                last_pass_status = entry.get("status")
                if last_pass_status == "paused":
                    self.paused_at = entry.get("ts")
            elif kind == "end":
                self.end = entry
            elif kind == "verify":
                self.verify = entry
        if self.end is not None:
            self.status = self.end.get("status", "error")
        elif last_pass_status == "paused":
            self.status = "paused"
        else:
            self.status = "running"

    @property
    def id(self) -> str:
        return self.start.get("id", "")

    @property
    def pending(self) -> List[Dict[str, Any]]:
        """Calls waiting for a human, in order."""
        return [
            entry
            for seq, entry in sorted(self.calls.items())
            if entry.get("state") == "pending" and seq not in self.approved
        ]

    def standing_for(self, path: str, ttl_s: float, now: Optional[float] = None) -> Optional[Dict[str, Any]]:
        """The approval given in advance that covers a call to ``path`` now,
        or None. One that is older than ``ttl_s`` no longer counts (0: no
        limit): like any approval it answered a question asked at one
        moment. One with no readable time counts as too old."""
        now = time.time() if now is None else now
        for grant in reversed(self.standing):
            if path not in grant["tools"] and "*" not in grant["tools"]:
                continue
            given = parse_ts(grant.get("ts"))
            if ttl_s > 0 and (given is None or now - given > ttl_s):
                continue
            return grant
        return None

    def paused_for(self, now: Optional[float] = None) -> float:
        """Seconds since the execution paused; 0 if that cannot be told."""
        when = parse_ts(self.paused_at)
        if when is None:
            return 0.0
        return max(0.0, (time.time() if now is None else now) - when)

    @property
    def verification(self) -> str:
        """How this execution was checked. ``none`` is an answer too: an
        unverified result should be findable, not indistinguishable."""
        if self.verify is None:
            return "none"
        return "replay-ok" if self.verify.get("ok") else "replay-failed"

    def summary(self) -> Dict[str, Any]:
        applied = sum(1 for c in self.calls.values() if c.get("kind") == "tool" and c.get("state") == "applied")
        return {
            "executionId": self.id,
            "status": self.status,
            "session": self.start.get("session"),
            "agent": self.start.get("agent"),
            "startedAt": self.start.get("ts"),
            "passes": self.passes,
            "toolCalls": applied,
            "verification": self.verification,
        }


class ExecutionStore:
    def __init__(self, home: Path) -> None:
        self._dir = Path(home) / "executions"

    def _path(self, exec_id: str) -> Path:
        return self._dir / (exec_id + ".jsonl")

    def new_id(self) -> str:
        """Sortable by start time, so ``last`` and listings need no index.

        Microseconds are part of the id: with whole seconds, two executions
        started in the same second would sort by their random suffix and
        ``last`` would name the wrong one.
        """
        now = time.time_ns()
        stamp = time.strftime("%Y%m%dT%H%M%S", time.gmtime(now // 1_000_000_000))
        return "ex_%s%06d_%s" % (stamp, (now % 1_000_000_000) // 1000, secrets.token_hex(2))

    def append(self, exec_id: str, entry: Dict[str, Any]) -> None:
        self._dir.mkdir(parents=True, exist_ok=True)
        entry = dict(entry, ts=time.strftime(_TS_FORMAT, time.gmtime()))
        with open(self._path(exec_id), "ab") as handle:
            handle.write(dumps_line(entry))

    def save_blob(self, exec_id: str, seq: int, value: Any) -> Optional[str]:
        """Keep a tool result that is too large for the log, so that the work
        of the call is not lost with it. Returns the file's path, or None if
        it could not be written; the caller reports either."""
        folder = self._dir.parent / "blobs"
        path = folder / ("%s-%d.json" % (exec_id, seq))
        try:
            folder.mkdir(parents=True, exist_ok=True)
            with open(path, "w", encoding="ascii") as handle:
                json.dump(value, handle, ensure_ascii=True)
        except (OSError, TypeError, ValueError):
            return None
        return str(path)

    def read(self, exec_id: str) -> List[Dict[str, Any]]:
        path = self._path(exec_id)
        if not path.is_file():
            raise NotFoundError(
                "no execution %r" % exec_id, hint="List them with: %s" % launcher.command("executions")
            )
        entries = []
        lines = path.read_bytes().split(b"\n")
        for number, line in enumerate(lines):
            if not line.strip():
                continue
            try:
                entries.append(json.loads(line))
            except ValueError:
                # The last line may be half written: another process is
                # appending right now, or one died in the middle of a write.
                # Anything earlier that does not parse is real damage.
                if number == len(lines) - 1:
                    break
                raise NotFoundError(
                    "the log of execution %s is damaged at line %d" % (exec_id, number + 1),
                    hint="The file is %s. Start the program again; the entries before that line can still be read by hand." % path,
                )
        return entries

    def view(self, exec_id: str) -> ExecutionView:
        return ExecutionView(self.read(exec_id))

    def ids(self) -> List[str]:
        if not self._dir.is_dir():
            return []
        return sorted(p.stem for p in self._dir.glob("ex_*.jsonl"))

    def resolve_id(self, ref: str, agent: Optional[str] = None) -> str:
        """Accept a full id, a unique prefix, or ``last``.

        With several agents on one data directory, ``last`` is the last
        execution of the agent that asks, when it has a name: the newest
        one overall may be somebody else's, started a moment ago.
        """
        ids = self.ids()
        if ref == "last":
            if not ids:
                raise NotFoundError("there are no executions yet")
            if agent and agent != "anonymous":
                for candidate in reversed(ids[-500:]):
                    if self._started_by(candidate) == agent:
                        return candidate
                raise NotFoundError(
                    "agent %r has no executions here yet" % agent,
                    hint="'last' means the last execution of the agent that asks. Use an execution id, or see all of them with: %s"
                    % launcher.command("executions"),
                )
            return ids[-1]
        if ref in ids:
            return ref
        matches = [i for i in ids if i.startswith(ref)]
        if len(matches) == 1:
            return matches[0]
        if matches:
            raise NotFoundError("%r matches %d executions: %s" % (ref, len(matches), ", ".join(matches[:5])))
        raise NotFoundError("no execution %r" % ref, hint="List them with: %s" % launcher.command("executions"))

    def _first_entry(self, exec_id: str) -> Dict[str, Any]:
        """An execution's ``start`` entry, reading only that line."""
        try:
            with open(self._path(exec_id), "rb") as handle:
                entry = json.loads(handle.readline())
            return entry if isinstance(entry, dict) else {}
        except (OSError, ValueError):
            return {}

    def _started_by(self, exec_id: str) -> Optional[str]:
        return self._first_entry(exec_id).get("agent")

    # -- retention ---------------------------------------------------------

    def _files_of(self, exec_id: str) -> List[Path]:
        """Everything on disk that belongs to one execution."""
        home = self._dir.parent
        files = [self._path(exec_id), self.worker_log(exec_id), self._dir / (exec_id + ".cancel"), self._dir / (exec_id + ".lock")]
        for folder, pattern in ((home / "blobs", exec_id + "-*.json"), (home / "outputs", exec_id + "-*.txt")):
            if folder.is_dir():
                files.extend(folder.glob(pattern))
        return [path for path in files if path.is_file()]

    def prune(self, max_age_s: float = 0.0, max_executions: int = 0, dry_run: bool = False) -> Dict[str, Any]:
        """Remove the records of old executions: those that ended more than
        ``max_age_s`` ago, and the oldest ones beyond ``max_executions``
        (0 turns a limit off).

        Never removed: an execution a live process is running. A paused
        execution is removed only for its age, never to make room: it is
        waiting for a person. Helper snapshots that no remaining execution
        refers to go with them.
        """
        ids = self.ids()
        now = time.time()
        surplus = set(ids[: max(0, len(ids) - max_executions)]) if max_executions > 0 else set()
        doomed: List[str] = []
        for exec_id in ids:
            path = self._path(exec_id)
            try:
                idle = now - path.stat().st_mtime  # the log's last entry is when the execution last did anything
            except OSError:
                continue
            too_old = max_age_s > 0 and idle > max_age_s
            if not too_old and exec_id not in surplus:
                continue
            try:
                status = self.view(exec_id).status
            except NotFoundError:
                status = "damaged"
            if status == "running" and (self.holder(exec_id) is not None or idle < 3600):
                continue  # in use, or too recent to call abandoned
            if status == "paused" and not too_old:
                continue
            doomed.append(exec_id)
        freed = 0
        for exec_id in doomed:
            for path in self._files_of(exec_id):
                try:
                    freed += path.stat().st_size
                    if not dry_run:
                        path.unlink()
                except OSError:
                    pass
        if not dry_run:
            self._prune_helpers(set(ids) - set(doomed))
        return {"removed": len(doomed), "kept": len(ids) - len(doomed), "freedBytes": freed, "dryRun": dry_run}

    def _prune_helpers(self, remaining: Set[str]) -> None:
        folder = self._dir.parent / "helpers"
        if not folder.is_dir():
            return
        wanted = {str(self._first_entry(exec_id).get("helpers_sha256")) for exec_id in remaining}
        for path in folder.glob("*.json"):
            if path.stem not in wanted:
                try:
                    path.unlink()
                except OSError:
                    pass

    def auto_prune(self, max_age_s: float, max_executions: int, every_s: float = 6 * 3600) -> None:
        """Apply the retention limits now and then: at most once every
        ``every_s`` seconds per data directory, so that a run does not pay
        for it. Never raises: housekeeping must not fail a program."""
        if max_age_s <= 0 and max_executions <= 0:
            return
        stamp = self._dir.parent / "pruned.stamp"
        try:
            if stamp.is_file() and time.time() - stamp.stat().st_mtime < every_s:
                return
            self._dir.parent.mkdir(parents=True, exist_ok=True)
            stamp.write_text(str(os.getpid()), encoding="ascii")
            self.prune(max_age_s, max_executions)
        except (OSError, CodemodeError):
            pass

    @contextmanager
    def lock(self, exec_id: str) -> Iterator[None]:
        """One pass at a time per execution. Two concurrent passes could both
        run the same approved call, which is exactly the double side effect
        replay exists to prevent."""
        self._dir.mkdir(parents=True, exist_ok=True)
        path = self._dir / (exec_id + ".lock")
        with file_lock(path, 0.0, lambda owner: BusyError(
            "execution %s is being run by another process (pid %s)" % (exec_id, owner or "?"),
            hint="Wait for it: %s" % launcher.command("wait", exec_id),
        )):
            yield

    def holder(self, exec_id: str) -> Optional[int]:
        """The pid of the live process running a pass of this execution."""
        pid = _read_pid(self._dir / (exec_id + ".lock"))
        return pid if pid is not None and pid_alive(pid) else None

    # -- cancellation across processes ------------------------------------

    def request_cancel(self, exec_id: str) -> None:
        """Ask the process running this execution to stop it. A file, because
        that process may be another one: a detached worker, an MCP server."""
        self._dir.mkdir(parents=True, exist_ok=True)
        (self._dir / (exec_id + ".cancel")).write_text(str(os.getpid()), encoding="ascii")

    def cancel_requested(self, exec_id: str) -> bool:
        return (self._dir / (exec_id + ".cancel")).is_file()

    def clear_cancel(self, exec_id: str) -> None:
        try:
            (self._dir / (exec_id + ".cancel")).unlink()
        except OSError:
            pass

    def worker_log(self, exec_id: str) -> Path:
        """Where a detached worker's own output goes: the place to look when
        it died before it could write to the execution log."""
        return self._dir / (exec_id + ".worker.log")


@contextmanager
def file_lock(path: Path, wait_s: float, busy: Any) -> Iterator[None]:
    """Hold a lock file for the length of a ``with`` block.

    The file holds the owner's process id. A lock whose owner is dead is
    left over from a process that was killed, and is cleared; a lock whose
    owner is alive is waited for up to ``wait_s`` seconds, after which
    ``busy(owner pid)`` is raised.
    """
    deadline = time.monotonic() + max(0.0, wait_s)
    attempt = 0
    while not _create_exclusive(path):
        owner = _read_pid(path)
        alive = owner is not None and pid_alive(owner)
        if (alive and time.monotonic() >= deadline) or attempt >= 500:
            raise busy(owner)
        if not alive:
            # Clearing is done by one process at a time, so two that find a
            # stale lock together cannot both end up holding it. The loop is
            # for the moments when the file is busy: being read by another
            # process, or half deleted.
            _clear_if_stale(path)
            if attempt >= 5 and time.monotonic() >= deadline:
                raise busy(owner)
        attempt += 1
        time.sleep(min(0.05, 0.01 * attempt))
    try:
        yield
    finally:
        _release(path)


def _release(path: Path) -> None:
    """Remove a lock file this process holds. Windows refuses to delete a
    file while anyone has it open, and somebody reading the owner's id has
    it open for a moment. A lock that is not removed would be held by a live
    process for as long as that process runs, so this tries until it works."""
    for attempt in range(200):
        try:
            os.unlink(str(path))
            return
        except FileNotFoundError:
            return
        except OSError:
            time.sleep(0.005 if attempt < 20 else 0.02)


def _clear_if_stale(path: Path) -> None:
    guard = path.with_name(path.name + ".clearing")
    try:
        if guard.is_file() and time.time() - guard.stat().st_mtime > 30:
            guard.unlink()  # a clearer that died; the guard is seconds of work, never 30
    except OSError:
        pass
    if not _create_exclusive(guard):
        return  # someone else is looking at it; they will decide
    try:
        pid = _read_pid(path)
        if path.is_file() and (pid is None or not pid_alive(pid)):
            path.unlink()
    except OSError:
        pass
    finally:
        try:
            guard.unlink()
        except OSError:
            pass


def _create_exclusive(path: Path) -> bool:
    """Create a lock file holding this process's id. False if it exists."""
    try:
        fd = os.open(str(path), os.O_CREAT | os.O_EXCL | os.O_WRONLY)
    except FileExistsError:
        return False
    except PermissionError:
        return False  # Windows: the file is being deleted by its owner right now
    try:
        os.write(fd, str(os.getpid()).encode("ascii"))
    finally:
        os.close(fd)
    return True


def _read_pid(path: Path) -> Optional[int]:
    try:
        return int(path.read_text(encoding="ascii").strip())
    except (OSError, ValueError):
        return None
