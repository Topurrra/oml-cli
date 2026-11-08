# FirstPrompt

You are working in an OutSystems 11 project folder made by `oml create`. Read AGENTS.md first and follow it and skill/rules.md for the whole session. Use oml for everything; never open or edit .oml/.oap files directly.

Goal of this session: learn this project and save what you learn, so later sessions start with that knowledge. Do not change any module. Never run install, publish, references or lifetime commands.

modules/ holds the project's modules, grouped into one or more applications (typically one main application plus shared components). Do not assume names or counts; discover them with the commands below.

Save tokens: put several commands in one tool call, joined with `;`. Never print a whole report: the commands below write big output to a file and read only the part you need. Stop reading when you can answer the questions in step 7.

Steps:

1. Check the setup: `oml version; oml doctor; oml team-rules; oml memory list --top 20`.
   If something is missing, or modules/ is empty, stop and tell me. If memory or LEARNING.md already exist, build on them: update what changed, and do not save a fact twice.
2. See the map: `oml map overview`. It lists the applications, modules, layers and consumed APIs. Only if it reports no map, pick a short project name and run `oml map build <project> --in modules`.
3. Learn the structure, per module, starting with the main application:
   `oml inspect <module.oml> --own --only entities,actions,rest,timers,processes`
   Find the main entities, the exposed and consumed REST APIs, the timers, the processes and the public actions.
   For dependency problems between modules:
   `oml architecture --in modules --json architecture.json --force > architecture.log; (Get-Content architecture.json -Raw | ConvertFrom-Json).findings`
4. Learn how it works: pick the 5-10 most important flows (the entry points first: exposed API methods, timers, processes, then what they call). Follow them with `oml map uses <element>`, `oml map api <Api.Method>`, `oml map data <Entity>` and `oml flow <module.oml> <Action>`. Note what each shared module or component is used for and where.
5. Find risks, high severity only:
   `oml check --in modules --json check.json > check.log; $c = Get-Content check.json -Raw | ConvertFrom-Json; $c.summary; $c.findings | Where-Object severity -eq 'high' | Select-Object rule,module,element,title`
6. Save to memory as you go, one fact per record, 20 to 40 records in all:
   `oml memory add "<fact>" --kind fact --module <Module> --elements A,B`
   Use `--kind decision` or `--kind trap` when it fits. Good records: what each application and module is for, how a request flows through the main application, how shared components are used, the important entities and who writes them, anything surprising or fragile.
7. Finally, write LEARNING.md in this folder:
   - the application in one paragraph
   - a table of modules: layer, purpose, key elements
   - the main flows step by step
   - external APIs and what they exchange
   - risks and open questions for me
   Keep it short and factual, citing element names. Delete check.log and architecture.log. Then show me `oml memory list --top 40`.

Ask me when something is unclear instead of guessing.
