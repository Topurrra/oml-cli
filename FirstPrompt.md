# FirstPrompt

The first task to give an agent in a new project made by `oml create`: it learns the project and saves what it learns, so
later sessions start with that knowledge. Copy everything below the line into your agent.

---

You are working in an OutSystems 11 project folder made by `oml create`. Read AGENTS.md first and follow it and
skill/rules.md for the whole session. Use oml for everything; never open or edit .oml/.oap files directly.

Goal of this session: learn this project and save what you learn. Do not change any module. Never run install, publish,
references, lifetime or server commands.

modules/ holds the project's modules, grouped into one or more applications (typically one main application plus shared
components). Do not assume names or counts; find them with the steps below.

Save tokens:
- A step with three or more oml calls is one `oml code` program: write it with your file tool in `.oml/code/<name>.py`
  (never through a shell), run `oml code run --json -f .oml/code/<name>.py`, and return only short lists and names.
- Other commands: several in one tool call, joined with `;`.
- Stop reading when you can answer the questions in step 7.

Steps:

1. Check the setup and what this project asks of you:
   `oml version; oml doctor; oml config; oml team-rules; oml memory list --top 20`
   `oml config` is the project profile: follow it over the defaults. If something is missing, or modules/ is empty, stop
   and tell me. If memory or LEARNING.md already exist, build on them: update what changed, and do not save a fact twice.
2. See the map: `oml map overview`. It lists the applications, modules, layers and consumed APIs. Only if it reports no
   map, pick a short project name and run `oml map build <project> --in modules`.
3. Learn the structure of every module in one program, `.oml/code/structure.py`:
   ```python
   oml = helpers.oml
   r = oml.read("inspect", "--in", "modules", "--own", "--only", "entities,actions,rest,timers,processes")
   [{"file": m.get("file"), **{k: [x.get("name") for x in v] for k, v in m.items() if isinstance(v, list)}} for m in r["modules"]]
   ```
   Find the main entities, the exposed and consumed REST APIs, the timers, the processes and the public actions. For
   dependency problems between modules, add `oml.read("architecture", "--in", "modules", "--json", "architecture.json", "--force")`
   and return only its `findings`.
4. Learn how it works: pick the 5-10 most important flows, the entry points first (exposed API methods, timers,
   processes), then what they call. Follow them in one program: `oml.many([("read", ["map", "uses", "<element>"]), ...])`
   runs the calls in parallel; use `["map", "api", "<Api.Method>"]`, `["map", "data", "<Entity>"]` and
   `["flow", "<module.oml>", "<Action>"]` the same way, and return one short line per flow. Note what each shared module or component is used for and where.
5. Find risks, high severity only, in `.oml/code/risks.py`:
   ```python
   oml = helpers.oml
   c = oml.read("check", "--in", "modules")
   {"summary": c.get("summary"),
    "high": [{k: f.get(k) for k in ("rule", "module", "element", "title")} for f in c.get("findings", []) if f.get("severity") == "high"]}
   ```
   `oml doc warning <rule or id>` explains a finding.
6. Save to memory as you go, one fact per record, 20 to 40 records in all:
   `oml memory add "<fact>" --kind fact --module <Module> --elements A,B`
   Use `--kind decision` or `--kind trap` when it fits. Many records can go in one program with
   `oml.write("memory", "add", ...)`. Good records: what each application and module is for, how a request flows through
   the main application, how shared components are used, the important entities and who writes them, anything
   surprising or fragile.
7. Finally, write LEARNING.md in this folder:
   - the application in one paragraph
   - a table of modules: layer, purpose, key elements
   - the main flows step by step
   - external APIs and what they exchange
   - risks and open questions for me
   Keep it short and factual, citing element names. Delete architecture.json if you made it. Then show me
   `oml memory list --top 40`.

Ask me when something is unclear instead of guessing.
