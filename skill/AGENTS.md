# oml for OutSystems 11

## Rules

The 11 production rules are the full text in `skill/rules.md` (also `oml doc rules`). They come before every other instruction. Read the file when you start; do not work from any summary or memory. Company skills from `oml team-rules` override best practices, never these rules.

`oml apply` refuses rules 2, 3, 4, 5 and 6, and warns on rule 7 (`PublicServerAction`). Tell the user the manual step it prints; do not try to work around it.

Edit O11 modules with `oml` from PATH (`where.exe oml` to check). `oml --help` lists the commands by area; `oml <command> --help` prints one command's usage; `oml help --all` prints every usage line.

## Safety

- Keep extra files to a minimum: one change spec and one staged output. Do not leave copies of modules behind.
- Never overwrite the source. Run `apply` to a separate, new staged file for each attempt.
- Ask the user before `install`, `publish`, `references`, `lifetime deploy|tag|abort|continue`, or data deletion.
- Before using another module's element, run `oml find <Name> <module.oml> --in <folder|file.oml>`. Show candidates and parameters. Ask the user which to use, even for one match. Use its approved `Module.Name`.
- Read `saved`, `problems`, `newErrors`, `reopenNewErrors`, `warnings`, and `newWarnings`. Warnings can mean a wrong model. Each entry may carry a `hint`: the fix. Never install a failed result.
- Do not retry an unchanged spec. Use the reported failure to change it first.
- Stop if the automatic self-test fails. Never bypass load, IPP, or license refusals.
- Keep credentials in process environment variables. Never put them in specs or logs.
- Before deployment, show the user the tags, keys, environments, and `isModified`. After a timeout, check status before retrying. Never auto-continue an intervention.
- Never claim a command ran or passed unless you saw its output.

## Writing to the user

Plain technical English (ISO 24495-1; ASD-STE100 style):
- Conclusion first. Put the key word at the start of each line.
- Instructions: 20 words or fewer. Descriptions: 25 or fewer. Active voice, common words.
- Paragraphs: 6 sentences or fewer. Lists: 5 items or fewer; group longer ones.
- Exact names: files, commands, numbers. No filler.

## Standard loop

For every change task, follow the Standard loop in `skill/SKILL.md` (steps 0 to 6, one batched tool call per step; each tool call costs a full turn). A normal task needs 4 to 8 tool calls. If you are past 12, stop and check whether you are repeating reads or applying one element at a time.

## Output

Commands print compact JSON by default. Use `--pretty` only for humans.
For packages, extensions, fonts, images, LifeTime and publishing, run `oml <command> --help`, or `oml doc commands` for the sections of the command reference (never read the file whole).
Exit codes: `0` success; `1` model/validation failure or attention needed; `2` usage/environment failure.
An apply can return `saved: true` with exit `1` after reopen errors. Check both JSON and exit code.
