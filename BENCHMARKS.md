# Benchmarks

Same task, same module and same model on both sides. The OutSystems MCP side uses Service Studio's MCP server with the official OutSystems skill for O11. The small and large tasks ran in October 2026 with the skill of that time (before its commits `2fafeda` and `be8e730`); the OUI run uses the skill of 2026-11-20 (`8cdd94f`). Each row is one run unless it says otherwise, so read the numbers as a direction, not a guarantee.

Re-run any of them with [tests/bench](tests/bench/README.md). Every run uses real model calls and costs money.

## Summary

| Task | OutSystems MCP | oml | oml advantage |
|---|---|---|---|
| Small: entity, 2 CRUD actions, list screen | 611k input tokens, 185 s | 207k input tokens, 56 s | 3.0× fewer tokens, 3.3× faster |
| Large: 24 UI components, 1 agent | $0.94, 787 s, 58 turns | $0.55, 706 s, 37 turns | 42% cheaper |
| Large: 24 UI components, 8 agents | 20/24 built, $3.16 | 24/24 built, $1.17 | all parts built, 63% cheaper |
| OUI subset: 11 blocks + wizard screen | $1.51, 1073 s, 13.0M input tokens | $0.76, 752 s, 6.1M input tokens | 50% cheaper, 30% faster, inputs right |

## OUI subset: oml code vs OutSystems MCP

Both agents rebuild a subset of OUI (about 10 blocks and 1 page template) from the same descriptions. The oml side uses `oml code`: one Python program runs many oml calls and returns only the answer. The OUI kit's own build of the same subset is the reference: each result is compared with it element by element (blocks, inputs, events, placeholders, validation).

Task: [tests/bench/tasks/oui-subset.md](tests/bench/tasks/oui-subset.md): the wizard template OuiTplWizard, the 11 blocks it uses and one structure, from a blank Reactive module. Model: Claude Haiku 5.5. One run per side, on 2026-11-23. MCP side: Service Studio 11.55.97 and the official skill at commit `8cdd94f` (2026-11-20).

| | OutSystems MCP + O11 skill | oml code |
|---|---:|---:|
| Cost | $1.51 | **$0.76** (50% less) |
| Time | 1,073 s | **752 s** (30% less) |
| Turns | 73 | **62** |
| Input tokens (incl. cache) | 12,990,846 | **6,056,354** (2.1× fewer) |
| Output tokens | 232,253 | **154,307** |
| Tool calls (failed) | 94 (3) | 94 (2) |
| Validation | 0 errors, 6 warnings | 0 errors, 29 warnings (27 are offline image warnings) |
| Elements vs the OUI kit's build | 82/82 | 82/82 |
| Inputs as specified | **45 of 47 optional inputs made mandatory** | All 47 right: types, mandatory and defaults |

Both built every block, input, event, placeholder, the screen and the structure. The difference is in quality and cost: the MCP agent made almost every block input mandatory, so each use of a block must pass every value; the oml code agent matched the description. Gaps on the oml side: the wizard's country dropdown has no options (the agent could not add list items and left it), and neither side's JavaScript was tried in a browser. `tests/bench/compare.ps1` checks signatures, not behaviour or CSS.

## Small task

An entity, two CRUD actions and a list screen. Model `gpt-6-luna`.

| | OutSystems MCP + O11 skill | oml |
|---|---:|---:|
| Result | 0 errors | 0 errors, same content |
| Input tokens | 611,714 | **207,210** (3.0× fewer) |
| Output tokens | 4,474 | **2,780** |
| Time | 185 s | **56 s** (3.3× faster) |
| Tool calls (failed) | 19 (2) | **10 (0)** |

## Large task

24 UI components, a gallery screen, a theme toggle and a font picker. Model Claude Haiku 5.5; cost as reported by Claude Code.

| | Result | Cost | Time | Turns |
|---|---|---:|---:|---:|
| MCP, 1 agent | 24/24, 0 errors | $0.94 | 787 s | 58 |
| **oml, 1 agent** | 24/24, 0 errors | **$0.55** | **706 s** | **37** |
| MCP, 8 agents + 1 assembly | **20/24**: one agent was blocked, one block was lost in a merge | $3.16 | 968 s | 340 |
| **oml, 8 agents + 1 assembly** | 24/24, 0 errors, 0 warnings | **$1.17** | **700 s** | 267 |

Why the gap:

- Output is similar, because both sides write the same CSS and JavaScript.
- MCP needs more turns, and every turn reads the context again.
- Parallel MCP agents share one edit session per module: a second agent is refused until the first ends. oml agents each work on their own files.

## oml 1.1.0 vs 1.2.0

The same large task, 1 agent, Haiku 5.5, 3 runs each, averages.

| | Cost | Time | Turns | Result |
|---|---:|---:|---:|---|
| oml 1.1.0 | $0.80 | 864 s | 64 | 24/24, 0 errors |
| **oml 1.2.0** | **$0.36** | **615 s** | **37** | 24/24, 0 errors |

1.2.0 gives agents a fixed loop that batches commands into fewer tool calls. One 1.2.0 run left unexplained warnings, so the skill now requires fixing or explaining every new warning. One run with that rule cost $0.81 with 0 warnings; the cost with the rule needs more runs.

## Call overhead: `oml mcp` and `oml code`

No model involved: the time oml itself needs for the calls. Measured on the TraditionalWebSample modules, Service Studio 11.55.97.

| | One `oml.exe` per call | Through `oml mcp` |
|---|---:|---:|
| 5 inspects, one after another | 9.4 s | **13 ms** |
| 4 inspects in parallel | 2.4 s | **5 ms** |

`oml mcp` loads Service Studio's model once and keeps a module that was only read open between calls. A real `oml code` program with 6 calls on 3 modules took 3.7 s end to end, including the start of Python and Service Studio's model.
