# FirstPrompt

You are working in an OutSystems 11 project folder set up by oml. Read AGENTS.md first and follow it and skill/rules.md for the whole session. Use oml for everything; never open or edit .oml/.oap files directly.

Goal of this session: learn this project fully and save what you learn, so later sessions start with that knowledge. Do not change any module.

modules/ holds the project's modules, grouped into one or more applications (typically one main application plus shared components). Do not assume names or counts; discover them with the commands below.

Steps:

1. Run `oml version` and `oml doctor`. If something is missing, stop and tell me.
2. Build the map: pick a short project name and run
`oml map build <project> --in modules`.
Then `oml map overview` to see the applications and their modules. Later `oml map ...` queries in this folder need no project name.
3. Learn the structure:
   - `oml architecture --in modules --json architecture.json` for the layers and the dependencies between modules.
   - Inspect every module at once with `oml inspect --in modules --own`, or one by one with `oml inspect <module.oml> --own`. Find the main entities, the exposed and consumed REST APIs, the timers, the processes and the public actions.
4. Learn how it works: pick the 5-10 most important flows (the entry points first: exposed API methods, timers, processes, then what they call). Follow them with `oml map uses`, `oml map api <Api.Method>`, `oml map data <Entity>` and `oml flow <module.oml> <Action>`. Note what each shared module or component is used for and where.
5. Find risks: `oml check --in modules`. Summarize the high-severity findings only.
6. Save to memory as you go with `oml memory add` (one fact per record; --kind fact, decision or trap; --module set). Good records: what each application and module is for, how a request flows through the main application, how shared components are used, the important entities and who writes them, anything surprising or fragile.
7. Finally, write LEARNING.md in this folder:
   - the application in one paragraph
   - a table of modules: layer, purpose, key elements
   - the main flows step by step
   - external APIs and what they exchange
   - risks and open questions for me
   Keep it short and factual, citing element names. Then show me `oml memory list`.

Ask me when something is unclear instead of guessing. Never run install, publish, references or lifetime commands.
