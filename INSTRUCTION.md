# Working with AI agents and oml

oml lets any AI agent (Claude Code, Codex, Cursor, Copilot, Gemini and others) build, check and test OutSystems 11 apps safely. The agent runs `oml`; you decide what changes and what ships.

> **The one rule to remember:** agents never change your real module or an environment by themselves. They work on a staged copy and ask you before `install`, `publish` or a deployment.

---

## 1. Set up once (10 minutes)

| Step | Do this |
|---|---|
| Install oml | Unzip the [latest release](https://github.com/Topurrra/oml-cli/releases) and add its `bin` folder to PATH. Check: `oml version` |
| Check your machine | `oml doctor` says what is missing (Service Studio, .NET, browser) and how to fix it. |
| Start a project | `oml create falcon --in <folder of the project's .oap files>` makes a `falcon` folder with memory, the agent's skill and guards, your modules and the application map. Start your agent in that folder. |
| Instructions for the agent | `oml create` writes `AGENTS.md` (read by Codex, Cursor, Copilot, Gemini and most others) and `CLAUDE.md` for Claude Code. |
| Guards | `oml create` installs them: no reading huge references whole, no editing `.oml` files by hand, no calling the test-data API around oml. They cost no tokens. See the table below. |
| Existing folder instead | Copy `skill/` and `AGENTS.md` in, then `oml memory init` (memory), `oml map build <project> --in <folder of .oap files>` (the map), `oml hooks install` (guards). |

**Which agents get the guards**

| Agent | Guards |
|---|---|
| Claude Code | Yes |
| GitHub Copilot in VS Code, the Copilot app and Copilot CLI (with any model, Claude included) | Yes, from the same `.claude` file. If they seem off, turn on agent hooks in Copilot's settings. |
| Cursor | Yes, from the same `.claude` file |
| Codex | Yes, after you trust the project in Codex |
| Gemini CLI | Yes |
| Copilot cloud agent (on github.com), Windsurf, Cline, Kiro | No automatic guard; the agent still follows the same rules from `AGENTS.md`. |

`oml hooks status` shows what is installed. Run `oml hooks install` again after moving oml.

The usage log is on by default and never leaves your computer. `oml learn report` turns it into lessons; `OML_LOG=0` turns it off.

---

## 2. What you can ask for

### Development
Ask in plain words: *"Add a Customer entity with a list screen"*, *"Make Save validate the email"*. The agent then:

| Step | Command | What it does |
|---|---|---|
| Read | `oml inspect`, `oml describe`, `oml find` | Learns what the module holds before changing it. |
| Change | `oml spec check`, `oml apply` | Writes a change spec and applies it to a **new staged file**. Your module stays as it is. |
| Look | `oml preview` | Renders the changed screen in light and dark, with a screenshot. |
| Report | `oml review`, `oml show-change` | Shows what changed and its risks, as Markdown or a page with flow diagrams. |
| Install | `oml install` | Only after you say yes. |

### Manual QA (what a manual tester does)
`oml qa page <url>` opens a running app and lists what the screen shows: buttons, fields, tables, messages, errors and layout problems. `oml qa run <file>` clicks, types and checks like a tester. `oml qa record <url>` writes a test from what **you** click.

### QA automation (pay once, replay for free)
The agent tests a flow once with `oml qa run`, then saves the passing run: `oml qa run flow.json --save flow.replay.json`. Anyone replays it with `oml qa run flow.replay.json`, with no agent and no tokens. Each step has a time limit, so a slower app fails the test.

### Test scenarios after every task
After a task, the agent offers `oml test scenarios <source.oml> <staged.oml>`. It lists what to test, from the model:
- **BDD** (Given / When / Then) for every public action the task created or changed. `oml test bdd` builds the BDDFramework tests.
- **QA** for every changed screen: one scenario per path through each button, with a ready `qa.json` to run.

### Seeing the database (dev only)
Agents cannot see what was saved. `oml test data module` builds a small dev-only module that works through your module's **wrapper actions** (Create, Get, Update, Delete of an entity; the developer or the agent adds a missing one, as usual). It has no table of its own, so deleting it later leaves nothing in the database. Then the agent can `create` test data, `update` it, `check` what was saved and `cleanup` exactly what it created, like a BDD test. It calls that API only through oml. Ask your team before publishing it to DEV.

### Mocks for Castlemock (or any mock server)
`oml mock --all` writes a JSON response for every consumed API and persona, plus a checklist of which request value selects which answer. Mocks are **chained**: the CustomerId one API returns is the one the next API is asked about. Give real dev test data with `--personas personas.json`.

### Understanding a big application
`oml map build falcon --in <folder>` reads every app of a project once. Then ask:

| Question | Command |
|---|---|
| What is in this project? | `oml map overview` |
| Who uses this element? | `oml map uses GetCustomer` |
| What breaks if I change it? | `oml map impact GetCustomer` |
| Who writes and reads this entity? | `oml map data Customer` |
| Where does this API's data go next? | `oml map api CrmApi.GetClient` |

`oml architecture --in <folder>` draws the modules and their layers; `oml check --in <folder>` finds rule problems across them.

### Memory and learning
The agent saves decisions, your answers and traps with `oml memory`, so the next session does not ask again. `oml learn report` shows where agents lose time; `oml learn propose` suggests fixes that **you** accept or reject.

### Releases and environments
`oml pull`, `oml publish` and `oml references` work through Service Studio. `oml lifetime` downloads apps and deploys through LifeTime. All of them need your clear yes.

---

## 3. Tips for good results

- **Name the module and the outcome:** *"In Orders.oml, add a Cancel button on OrderDetail that asks for confirmation."*
- **One task at a time.** Ask for the review at the end and read it.
- **Answer the agent's questions.** It asks before anything risky; it does not guess.
- **Find a command fast:** `oml --help` (by area), `oml <command> --help`, `oml doc --search "<words>"`.
- **Use the dashboard if you prefer a browser:** `oml dashboard` (reads only; buttons show the command to run).

---

## 4. What agents will never do

The production rules are in [skill/rules.md](skill/rules.md). In short:
- **No destructive changes:** never delete or rename existing elements or data, never break a public element's signature, never write SQL that changes data, never weaken security.
- **No `.oml` file edits by hand:** only through `oml apply`, to a new staged file.
- **No install, publish or deploy without your yes.**

`oml apply` enforces the main rules itself and tells you the manual step when it refuses.
