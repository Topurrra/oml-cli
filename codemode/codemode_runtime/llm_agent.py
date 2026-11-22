# SPDX-License-Identifier: MIT
"""An optional, minimal agent loop for any OpenAI-compatible endpoint.

The runtime does not need this: any harness can drive it over MCP or the CLI.
The loop is here so the application is usable on its own and so the provider
boundary is real code rather than a claim. It speaks the chat-completions wire
format that hosted APIs, gateways and local servers (Ollama, LM Studio,
llama.cpp, vLLM, ...) share, using only ``urllib``. The endpoint, the model
and the key come from configuration at run time.

The model is offered one function, ``execute``. The loop ends when the model
answers without calling it, or at ``max_turns``: a bounded number of attempts
and then back to the human, never an endless loop.
"""

from __future__ import annotations

import json
import time
import urllib.error
import urllib.request
from typing import Any, Callable, Dict, List, Optional, Tuple

from .config import expand_env
from .engine import Approver
from .errors import CodemodeError, ConfigError, ModelEndpointError
from .runtime import Runtime

_SYSTEM = (
    "You complete tasks by calling the execute tool with Python programs. "
    "Do as much as you can in each program. When the task is done, answer in plain text "
    "without calling the tool, and state any assumption you made."
)
_PARAMETERS = {
    "type": "object",
    "properties": {
        "code": {"type": "string", "description": "The Python program to run."},
        "executionId": {"type": "string", "description": "Instead of code: fetch the outcome of an earlier execution."},
    },
}
_RETRY_STATUS = (429, 500, 502, 503, 504)
_MAX_RETRIES = 2

Emit = Callable[[Dict[str, Any]], None]


def resolve_llm(llm: Dict[str, Any]) -> Dict[str, Any]:
    """Check the endpoint settings and say exactly what is missing."""
    missing = [key for key in ("base_url", "model") if not llm.get(key)]
    if missing:
        raise ConfigError(
            "the model endpoint is not configured: missing %s" % " and ".join(missing),
            hint="Set CODEMODE_BASE_URL and CODEMODE_MODEL (and CODEMODE_API_KEY if the endpoint needs one), "
            'or the "llm" section of the config. Example for a local server: '
            "CODEMODE_BASE_URL=http://localhost:11434/v1 CODEMODE_MODEL=<model name>.",
        )
    out = dict(llm)
    base = out["base_url"].rstrip("/")
    if base.endswith("/chat/completions"):
        base = base[: -len("/chat/completions")]
    out["base_url"] = base
    if out.get("api_key"):
        out["api_key"] = expand_env(out["api_key"], "llm.api_key")
    return out


def _chat(llm: Dict[str, Any], messages: List[Dict[str, Any]], tools: List[Dict[str, Any]]) -> Dict[str, Any]:
    """One request. Retries the failures that are worth retrying, and honours
    Retry-After, because a long task should not die on one 429."""
    body = json.dumps({"model": llm["model"], "messages": messages, "tools": tools}).encode("utf-8")
    headers = {"Content-Type": "application/json"}
    if llm.get("api_key"):
        headers["Authorization"] = "Bearer " + llm["api_key"]
    url = llm["base_url"] + "/chat/completions"
    last = ""
    for attempt in range(_MAX_RETRIES + 1):
        request = urllib.request.Request(url, data=body, headers=headers, method="POST")
        try:
            with urllib.request.urlopen(request, timeout=float(llm.get("timeout_s", 120))) as response:
                text = response.read().decode("utf-8", "replace")
            try:
                data = json.loads(text)
            except ValueError:
                raise ModelEndpointError("the endpoint did not return JSON: %s" % text[:200])
            if not isinstance(data, dict) or not data.get("choices"):
                raise ModelEndpointError("the endpoint returned no choices: %s" % text[:200])
            return data
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", "replace")[:300]
            last = "HTTP %d from %s: %s" % (exc.code, url, detail)
            if exc.code not in _RETRY_STATUS or attempt == _MAX_RETRIES:
                raise ModelEndpointError(
                    last, hint="Check CODEMODE_BASE_URL, CODEMODE_MODEL and CODEMODE_API_KEY."
                )
            try:
                wait = min(30.0, float(exc.headers.get("Retry-After", "")))
            except (TypeError, ValueError):
                wait = float(2 ** attempt)
        except (urllib.error.URLError, OSError) as exc:
            last = "could not reach %s: %s" % (url, getattr(exc, "reason", exc))
            if attempt == _MAX_RETRIES:
                raise ModelEndpointError(last, hint="Is the endpoint running and is CODEMODE_BASE_URL right?")
            wait = float(2 ** attempt)
        time.sleep(wait)
    raise ModelEndpointError(last)


def _run_tool(runtime: Runtime, raw_args: Any, session: str, approver: Optional[Approver]) -> Dict[str, Any]:
    """Run one tool call from the model. Every failure becomes a payload the
    model can read, so a malformed call costs a turn, not the task."""
    try:
        args = json.loads(raw_args) if isinstance(raw_args, str) else raw_args
        if not isinstance(args, dict):
            raise ValueError("the arguments must be a JSON object")
    except ValueError as exc:
        return {"status": "error", "error": {
            "type": "BadArguments",
            "message": "could not read the tool arguments: %s" % exc,
            "hint": 'Send {"code": "..."} as JSON.',
        }}
    try:
        if args.get("executionId") and not args.get("code"):
            return runtime.get(str(args["executionId"]))
        return runtime.execute(str(args.get("code", "")), session, approver=approver)
    except CodemodeError as exc:
        return {"status": "error", "error": exc.to_dict()}


def run_agent(
    runtime: Runtime,
    task: str,
    session: str = "default",
    max_turns: Optional[int] = None,
    emit: Optional[Emit] = None,
    approver: Optional[Approver] = None,
) -> Tuple[bool, str]:
    """Drive the model until it answers. Returns (finished, final text)."""
    llm = resolve_llm(runtime.config.llm)
    turns = int(max_turns or llm.get("max_turns", 12))
    emit = emit or (lambda event: None)
    tools = [{"type": "function", "function": {
        "name": "execute",
        "description": runtime.tool_description(),
        "parameters": _PARAMETERS,
    }}]
    messages: List[Dict[str, Any]] = [
        {"role": "system", "content": _SYSTEM},
        {"role": "user", "content": task},
    ]
    for turn in range(1, turns + 1):
        message = _chat(llm, messages, tools)["choices"][0].get("message") or {}
        calls = message.get("tool_calls") or []
        assistant: Dict[str, Any] = {"role": "assistant", "content": message.get("content") or ""}
        if calls:
            assistant["tool_calls"] = calls
        messages.append(assistant)
        if not calls:
            emit({"event": "final", "turn": turn, "text": assistant["content"]})
            return True, assistant["content"]
        for call in calls:
            function = call.get("function") or {}
            payload = _run_tool(runtime, function.get("arguments"), session, approver)
            emit({
                "event": "tool",
                "turn": turn,
                "status": payload.get("status"),
                "executionId": payload.get("executionId"),
                "error": (payload.get("error") or {}).get("type"),
            })
            messages.append({
                "role": "tool",
                "tool_call_id": call.get("id", ""),
                "content": json.dumps(payload, ensure_ascii=True),
            })
    text = "stopped after %d turns without a final answer (max_turns)" % turns
    emit({"event": "stopped", "turns": turns, "text": text})
    return False, text
