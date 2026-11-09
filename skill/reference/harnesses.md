# Harnesses

How to start a worker subagent per harness, for the architect role. The table comes from public docs, read on 2026-10-22. Nobody tested it with oml. `Status` shows what the docs confirm.

| Harness | How to start a worker | Status |
|---|---|---|
| Claude Code | The Agent tool. Set `model` and `effort` per worker. Send independent workers in one message. | verified, model and effort |
| Copilot in VS Code | The `runSubagent` tool with a custom agent. Set `model` in the agent file. A model above the main model's cost tier does not run. | verified; effort unverified |
| Copilot CLI | `/agent`, `--agent`, or name the agent in the prompt. The model decides whether to delegate. A model per agent is not documented. | verified; model and effort per agent unverified |
| Codex | Ask it to spawn agents. Agent files: `.codex/agents/*.toml` with `model`. | unverified; effort unverified |
| Cursor | Subagents in `.cursor/agents/*.md` with a `model` field. They run in parallel. | verified; effort unverified |
