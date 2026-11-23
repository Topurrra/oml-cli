# Sources and the reader brief

Where `upskill` finds its records, and the brief for reader subagents.

## Where sessions live

Check each path and use what exists. Formats change between versions: read the first line of a file before you parse it.

| Harness | Prompts | Full sessions |
| --- | --- | --- |
| Claude Code | `~/.claude/history.jsonl`: one JSON object per line with `display`, `timestamp` (milliseconds), `project`, `sessionId`, `pastedContents` | `~/.claude/projects/<project path with separators as dashes>/*.jsonl` |
| Codex | `~/.codex/history.jsonl`: `text`, `ts` (seconds), `session_id` | `~/.codex/sessions/<year>/<month>/<day>/`; the first line of a session holds its folder at `payload.cwd` |
| Cursor | none in one file | `~/.cursor/projects/<project path with separators as dashes>/agent-transcripts/<id>/<id>.jsonl`; no dates inside, use the file time |
| GitHub Copilot, Gemini CLI, others | look under the home folder and the editor's storage | |

`~` is the user's home folder (`%USERPROFILE%` on Windows). A harness missing here still keeps its history somewhere: look before you report it missing.

Match prompts to the project by its folder name as a whole path part, not one exact path: worktrees and renamed folders log under other paths. List the matched paths in the notes. Where the prompt file has no path (Codex), keep the sessions whose folder is inside the project.

Claude Code shows a screenshot as `[Image #1]` and a paste as `[Pasted text #1 +37 lines]`. A short paste keeps its text in `pastedContents`; count the ones you could not recover.

## oml's record

- Usage log: `%LOCALAPPDATA%\oml-cli\usage.jsonl` (or the file `OML_LOG` names; `OML_LOG=0` means none was written). One line per oml command. `oml log summary [--hours N]` gives counts, failures and the top failure messages per command.
- `oml learn report [--since <date>] --md <file.md>`: repeated failures, retries and what fixed them, failing spec keys, doc searches with no good hit, ignored memory traps.
- Memory: `oml memory list --top 50`, `oml memory traps`.
- Reports the team saved: `oml check --json` and `oml review` outputs.

## Reader brief

Give each reader this text, the path of its file and one sentence about the project. Add nothing about what you expect it to find.

> Read-only task. Read the whole file, in parts if needed. Sample nothing.
>
> It is a dated log of every prompt one person typed to a coding agent on one OutSystems project, where the agent uses the oml command-line tool. Only their side is there.
>
> `[Image #n]` and `[Pasted text #n +k lines]` mark a screenshot or a paste whose content is not in the log. A line that starts with a file path is a dragged file, not a command.
>
> The aim is to learn where their time and patience go, so tooling can be designed from evidence. There is no hypothesis to confirm. Report what the log shows, including what does not recur.
>
> For each item give a count where you can and two to four short dated quotes. Leave out any quote with a key, a token, a password, a server name, an email address or a customer's words.
>
> 1. Recurring kinds of work, with a rough share of prompts for each.
> 2. Procedures the person dictates step by step or explains again on different days.
> 3. Corrections, grouped by what went wrong: wrong module or element, too much changed, not verified in the app, asked twice. Name each "still broken" streak and its subject. Keep the person's own changes of mind in a separate group.
> 4. How they check work: what they look at in the app, what they paste by hand that an agent could have fetched.
> 5. Work that is not building: publishing, deployments, reviews, answering colleagues.
> 6. Commands and skills they invoke, and what they type next.
> 7. Episodes that happened once and cost a lot.
>
> Plain text, under 1,200 words. Where the evidence is thin, say so.
