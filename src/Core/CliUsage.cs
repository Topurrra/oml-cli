namespace OmlCli;

// The usage text. No model types: the launcher also compiles this file, so `oml <command> --help` works for the
// commands it runs itself (lifetime, font, doctor, menu explorer, image, dashboard), even when Service Studio cannot load.
static class CliUsage
{
    // Every usage line, under its area ("Project:", ...): `oml help --all` prints it. The overview below names the same
    // areas with the same commands (`oml commands --check` compares them). A command word belongs to one area.
    public const string Text = """
        Project:
        oml create [<project|path>] [--dir <parent>] [--in <folder|.oap|.oml>]... [--author "<name>"] [--server <host>] [--hooks <agent>] [--no-skill] [--no-library] [--no-doctor]   (a project ready to work in; alone in a terminal it asks)
        oml config [show | init | example | set <key> <value>] [--dir <project>]   (the project profile, .oml/oml-config.json: what agents do, skip, ask; example: all settings; bare: what agents follow)
        oml skill refresh [--dir <project>]   (update a project's skill/ and AGENTS.md to this oml)
        oml hooks [status|install|remove] [--dir <project>] [--agent claude|copilot|codex|gemini|all]   (agent guards: no big references read whole, no .oml edits, no test-data API; the profile at session start)
        oml doctor [--text] [--selftest]   (checks the setup and says what to fix)
        oml doctor --selftest --show   (the whole self-test report; it also runs by itself on a new Service Studio build)
        oml version
        Read a module:
        oml inspect <module.oml> [--own] [--only entities,structures,actions,screens,blocks,references,timers,roles,processes,rest,static,siteProperties,uiFlows,otherContent] [--history] [--changed-since <yyyy-mm-dd>]
        oml inspect --in <folder> [--recursive] [--own] [--only ...]   (every .oml in the folder in one process: {modules:[{file,...}]}, a broken file gives an error entry)
        oml inspect <module.oml> --stats   (how many of each element kind)
        oml inspect --header <file.oml>...   (the module header without loading the model: name, version, upgrade level, key, hashes, signature, type)
        oml inspect --header --diff <a.oml> <b.oml>   (which header fields differ)
        oml describe <module.oml> <Element>... [--depth N] [--in <folder|file.oml>]... [--history] [--css|--sql [--out <file>]]
        oml find <Name|Pat*> [module.oml] [--in <folder|file.oml>]...  (which module has it: own, referenced, producer files)
        oml find <module.oml> --text "<words>" [--top N]  (this module's elements ranked by name, description, expressions, SQL, labels)
        oml query <module.oml> "<ModelQL>" [--limit N] [--max-chars N] [--live] | --fields <Type>   (read-only model questions: "Root { Entities { Name } }"; Studio may be closed)
        oml tree <module.oml> <ElementName> [--depth N]   (object tree of one action, screen, entity, ...; at most 250 parts)
        oml flow <module.oml> <Action>   (one flow as a graph: nodes with their positions and what they do, and the connectors; the Dif page draws it)
        oml export <module.oml> <Element>...   (a screen, web block, email, server action or OnException action as JSON for tools; read with describe)
        The whole system:
        oml map build <project> [--in <folder|file.oap|file.oml>]... [--app <Name>]...   (the map of every module of a project: calls, APIs, entities, processes; rebuilds only changed files)
        oml map overview | uses <element> | impact <element> | data <Entity> | api <Api.Method> | list [--project <name>]   (short answers from the map, without Service Studio)
        oml map mock <Api.Method> | --api <Api> | --all [--personas <file.json>] [--out <folder>] [--project <name>]   (mock responses for a mock server such as Castlemock, chained by the map: one persona keeps its values across APIs)
        oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>] [--force]
        oml architecture --in <folder|file.oml|file.oap>... --publish-order [--changed <names file|folder>] [--app <Name>]... [--layers <layers.json>]   (JSON: the modules producers first, cycles; writes no file)
        Change a module:
        oml spec [<key>]   (reference for one spec key; no key lists them. Runs without Service Studio)
        oml spec --search "<words>" [--top N]   (ranked search of the spec keys and sections)
        oml spec check <spec.json> [--module <source.oml>]   (checks a spec's shape; with --module also its names, nothing saved; exit 1 on problems)
        oml spec schema [--out <file.json>] [--force]   (JSON Schema for spec files)
        oml spec model-types [<TypeName>...]   (members of model/plugin types on the installed build; no name lists the useful types)
        oml spec widget-types   (registered widget descriptors)
        oml spec expr "<text>"   (parse an expression: ok and its canonical form, or the parse error; exit 1 on invalid)
        oml apply <source.oml> <spec.json>... [<staged-out.oml>] [--describe A,B,...] [--describe-created] [--report <report.json>] [--user-approved]   (no staged path: in place, with autoApprove.install)
        oml apply --batch <index.json>   (many modules in one process: {items:[{source,spec,staged}]}; one combined summary; exit 1 if any item failed)
        oml new <out.oml> [--name N] [--kind WebResponsive|Mobile|Service|ReactiveLibrary|...]
        oml new <out.oml> --template <name-or-exported.oml> [--name N]
        oml new <out.oml> --from-template <template.oml> [--name N] [--kind K]
        oml new --templates   (local template/sample modules and bundles)
        oml batch <steps.json> [--continue]   (several commands in one process, so Service Studio loads once; "-" reads the steps from stdin)
        oml migrate plan <old.oml> --name <NewModule> [--in <folder|file.oml>]... [--out <folder>] [--template <reactive.oml>] [--migrated <Old>=<New>]... [--copy-exposed]   (a Traditional Web module's move to Reactive: plan, specs for old and new module, screens translated or listed; changes no module)
        oml migrate status <old.oml> <new.oml> [--plan <folder>]   (by name: done, still to do, only in new, duplicates, screens by status)
        oml migrate check <old.oml> <new.oml>   (is the new module a correct copy: validation, action signatures, screen inputs and roles, block inputs, TODO stubs; exit 1 on a failed check)
        Quality:
        oml check [<module.oml>] [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--rules <oml-rules.json>] [--baseline <old-check.json>] [--out <report.html>] [--json <report.json>] [--fail-on high|medium|low]
        oml check --init-rules [--out <oml-rules.json>]   (writes the team rules file: every rule, on/off, severity, options)
        oml check --diff <before.oml> <after.oml> [--rules <oml-rules.json>]   (findings fixed and introduced per rule; exit 1 when one is introduced)
        oml validate <module.oml> [--warnings]
        oml review <before.oml> <after.oml> [--md <file.md>] [--out <file.html>] [--notes <file.md>] [--rules <oml-rules.json>]   (what changed, rule risks, new findings)
        oml review <before.oml> <after.oml> --prepare <folder> | --import <findings.json>   (blind logic review: a packet for a reviewer agent; its findings checked)
        oml review --server <Module> <v1> <v2> [--keep <folder>] [review options]   (two server versions: a number, published or latest)
        oml team-rules [list]   (your company skills: Markdown rules that override OutSystems best practices; oml doc <name> prints one)
        oml team-rules add <file.md> [--force]   (check a skill file (frontmatter, size, secrets) and install it for this user)
        oml team-rules remove <name>   (uninstall one company skill; the source file stays)
        Service Studio:
        oml pull [Module] [--out file.oml]  (save the module open in Service Studio to a file; needs 11.55.93+)
        oml install <staged.oml> <target.oml> [--allow-existing-errors]
        oml install --batch <index.json> [--allow-existing-errors]   (items {staged,target}; checks all first, installs none if any is refused)
        oml publish <Module | file.oml> [--close]   (1-Click Publish through Service Studio)
        oml publish --in <folder> --order <order.json>   (publish many modules in that order; stops at the first failure)
        oml references <Module | file.oml>  (Refresh All references through Service Studio)
        oml studio   (Service Studio's MCP server: reachable? open modules)
        oml studio tabs | focus | close <Module> | open <Module> [--environment <host>]   (module tabs; bring it forward; close a saved tab; open from the server)
        Server and LifeTime:
        oml server modules [--app <Name>] | versions <Module>   (platform server modules and versions, Studio closed; OML_SERVER_HOST, _USER, _PASSWORD)
        oml server pull <Module> [--version N] --out <file.oml> | --app <Name> --out <folder>   (one version, default published; or each module of an app)
        oml server check [--app <Name>]... [--rules <oml-rules.json>] [--out <report.html>]   (end of day: published modules checked, scores vs last run)
        oml lifetime environments|teams|team-apps|apps|download|tag|deploy|status|abort|continue (LifeTime API v2; run 'oml lifetime' for usage)
        Test:
        oml test bdd <module.oml> <ServerAction>... --bdd <BDDFramework.oml> [--out <spec.json>] [--module-name <TestModule>] [--producer <path>]   (spec for BDD test screens in a separate test module)
        oml test scenarios <source.oml> <staged.oml> [--out <folder>]   (what to test after a change: BDD per changed public action, scenarios and a browser script per screen)
        oml test data module <producer.oml> --entity <Entity>:Create=<A>,Delete=<B>[,Get=..][,Update=..][,CreateOrUpdate=..]... [--name <Module>] [--out <spec.json>]   (dev-only test-data module through your wrapper actions)
        oml test data allow <dev host> | create <Entity> --values <json> [--run <id>] | update <Entity> <Id> --values <json> | check <Entity> <Id> [--expect <json>] [--absent] | cleanup [--run <id>] | status   (DEV test data, BDD style; cleanup removes exactly what the run created)
        oml browser page <url> [--headed] [--width N] [--height N] [--timeout <ms>] [--screenshot <file.png>] [--max-width N] [--style-conflicts <class prefix>] [--snapshot <file.json>] [--engine auto|cdp|uia] [--no-session]   (look at a running app: actions, inputs, tables, messages, layout, OutSystems calls and slow ones, errors, accessibility)
        oml browser run <script.json> [--save <new.json>] [--base-url <url>] [--headed] [--width N] [--height N] [--timeout <ms>] [--out <folder>] [--continue] [--engine auto|cdp|uia] [--no-session]   (browser steps like a manual tester: open, click, fill, select, press, wait, expect, handoff, raw; exit 1 when a step fails)
        oml browser record <url> [--out <script.jsonc>] [--engine auto|cdp|uia] [--width N] [--height N]   (a browser script from what you click and type; close the window or press Ctrl+C to finish)
        oml browser start [--engine auto|cdp|uia] [--headed] [--width N] [--height N] [--attach [<port>]]   (keep one browser open: later browser commands skip the start and keep the login; --attach: a new tab in your own Chrome)
        oml browser look <url> <widget | text:<text> | css:<selector>> [--state hover|focus|active|focus-visible] [--theme dark|light] [--screenshot <file.png>] [--width N] [--no-session]   (one element: screen > blocks > widget, box, computed values, the CSS rule that wins each property, what hides or covers it)
        oml browser try <url> [--css <file.css>] [--html <target>=<file.html>] [--append <target>=<file.html>] [--js <file.js>] [--screenshot <file.png>] [--no-session]   (preview planned UI in the running page before building it; nothing is saved)
        oml browser sweep <url> [--widths 390,768,1366] [--out <folder>] [--no-session]   (the page at each width the project supports: overflow and its causes, layout findings, widgets shown or hidden per width)
        oml browser diff <before.json> <after.json | url>   (what changed between two snapshots (page --snapshot): widgets added, removed, moved, resized, restyled, new text)
        oml browser stop | accounts   (close the browser of oml browser start; the test accounts by role)
        oml browser raw <CDP method> [<params json>]   (any DevTools call on the session's page)
        Agents:
        oml code run [-f <program.py> | -] [--arg <name>=<value>]... [--detach] [--json]   (a Python program calls oml many times, in parallel, and returns only what it needs; exit 3: waits for approval)
        oml code get | wait | approve | reject | cancel <id> | doctor | init | prompt | instructions | serve | ...   (code mode; approve is for a person at a terminal)
        oml mcp   (MCP server on stdio: tools read, write, gated; Service Studio loads once)
        oml memory init [--module <file.oml> | --dir <folder>] [--force]   (create the store .oml/memory/memory.log in the current folder, the project root; no other command does)
        oml memory add "<one fact>" [--kind fact|decision|pref|trap|log|glossary|answer] [--module <Name>] [--elements A,B] [--why "<reason>"] [--tags a,b] [--supersedes <id>] [--conf 0-100] [--src <agent>] [--valid-from <date>] [--valid-until <date>]   (one record, 300 bytes at most; secrets are refused)
        oml memory recall "<question>" [--module <Name>] [--kind K] [--top N] [--include-stale]   (the best records for a question, ranked by use; read only)
        oml memory list [--module <Name>] [--kind K] [--top N] [--all]   (records, newest first; --all adds replaced ones; read only)
        oml memory show <id>   (one record in full, with its status; read only)
        oml memory history <id>   (what replaced what; read only)
        oml memory retract <id> --why "<reason>"   (mark a record as no longer true; it stays in the file)
        oml memory doctor [--module <file.oml>] [--file <usage.jsonl>]   (check the hash chain and the index; with a module, that the elements exist; failures that repeat)
        oml memory handoff [--module <Name>] [--top N]   (a brief for the next agent from the newest records; read only)
        oml memory traps [--command "<command>"] [--module <Name>] [--top N]   (live trap records; with --command, those it may run into; read only)
        oml memory dream --prepare [<folder>] | --import <ops.json> | --status   (end-of-day memory tidy: a packet, then checked add/retract ops)
        oml learn usage [--hours N] [--file <usage.jsonl>]   (the usage log per command: count, failures, median ms, top failures; --hours: oml vs agent time)
        oml learn report [--since <date>] [--file <usage.jsonl>]... [--check <report.json>]... [--md <file.md>]   (where agents fail and retry, from the local log and memory; changes nothing)
        oml learn propose [--maintainer [--repo <folder>]] [report options]   (proposals with evidence: trap records, a draft company skill, doc patches; nothing applied)
        oml learn show [<id>] | accept <id> | reject <id> [--why <text>] | history | undo <id>   (a person accepts; each accepted change can be undone)
        Look things up:
        oml doc [<topic>[#section]]   (OutSystems rules for agents by topic; no topic lists them. Runs without Service Studio)
        oml doc --search "<words>" [--top N] [--in <folder> | --library]   (ranked search of doc sections, spec keys, recipes, warnings and commands; --library: the OutSystems 11 docs; compact JSON)
        oml doc warning <id | title words>   (cause and fix of a validation message, such as NoErrorHandlingInFlow)
        oml doc warning --catalog [<prefix>]   (list the verification ids oml knows; a prefix narrows, such as Entity_)
        oml doc library [status]   (the local copy of the OutSystems 11 docs for doc --search --library: sources, commit, files)
        oml doc library fetch [--source product|support|howtos]... [--from <unzipped repository>]   (download or update the OutSystems 11 docs from GitHub: Markdown, about 15 MB)
        oml doc library update [--check] [--source product|support|howtos]...   (fetch again only the sources OutSystems changed; --check lists the changed pages, downloads nothing)
        Other files:
        oml app list <file.oap|file.osp>
        oml app extract <file.oap|file.osp> <outdir>
        oml app check <file.oap|file.osp> --producers <folder|package> [--deep]
        oml app pack <out.oap> --name <AppName> <module.oml|extension.xif>...
        oml extension inspect <file.xif>
        oml extension new <out.xif> --name <Name> [--spec <spec.json>]
        oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...
        oml translations export <module.oml> <file> [--format Excel|xlsx|ResX]
        oml font woff2 <input> [output.woff2] [--force]
        oml font batch <folder> [--out folder] [--force]
        oml image <input> [--to png|jpeg|webp|svg|ico] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force] [--precision 0-10] [--strip-title] [--remove-ids] [--aggressive]   (convert, resize or optimize a raster image or SVG)
        oml image <logo.png> --to svg [--mode trace|embed] [--colors 1-32] [--tolerance 0.1-5] [--min-area N] [--alpha-threshold 1-255] [--transparent-bg]   (trace a logo or icon to vector paths)
        oml image <input> --to ico [--sizes 16,32,48]   (multi-size Windows icon or favicon; sizes 1-256)
        oml image batch <folder> [same options] [--out folder]
        For people:
        oml dashboard [--port N] [--no-open] [--module <file.oml>]   (local web dashboard for developers; reads only, on 127.0.0.1)
        oml dashboard --change <file.md> [--port N] [--no-open]   (an agent's change walkthrough on the dashboard: call stacks, flows, element links, review, comments)
        oml menu   (an interactive menu of everyday tasks that shows the command it runs; a bare oml in a terminal opens it; agents never use it)
        oml menu explorer [--remove | --status]   (Explorer right-click entries for the current user: fonts, images, .oml files, folders)
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        --timings on a command that loads a module adds "timings": milliseconds per phase.
        """;

    // `oml`, `oml help` and `oml --help`: the overview, about 600 tokens. The areas of Text in its order, each with every
    // command word of its lines (tests/doc-check.ps1 and `oml commands --check` check it). `oml help --all` prints Text instead.
    public const string Overview = """
        Commands by area (OutSystems 11 modules; compact JSON out, a readable view in a console):
        Project: create, config, skill, hooks, doctor, version (a folder ready to work in; what agents do, skip and ask; refresh skill/; agent guards; the setup and build check)
        Read a module: inspect, describe, find, query, tree, flow, export (what a module holds, its counts and header; one element; search; model questions; one flow; JSON for tools)
        The whole system: map, architecture (every app of a project: who calls what, API mocks chained by the map; module layers and publish order)
        Change a module: spec, apply, new, batch, migrate (spec keys, model types, expressions; edit by spec into a staged copy; new module, templates; one process; Traditional to Reactive)
        Quality: check, validate, review, team-rules (rules and findings; validation; a change before and after; company rules)
        Service Studio: pull, install, publish, references, studio (save to a file; install after checks; publish; refresh references; open modules)
        Server and LifeTime: server, lifetime (any module version on the server; the LifeTime API)
        Test: test, browser (BDD specs, what to test after a change, dev test data; look at, drive and replay a running app)
        Agents: code, mcp, memory, learn (Python programs over many oml calls; an MCP server; project facts and traps; lessons and the usage log)
        Look things up: doc (OutSystems rules by topic, search; cause and fix of a warning; the OutSystems 11 docs)
        Other files: app, extension, openapi, translations, font, image (.oap and .osp packages; .xif extensions; OpenAPI; translations; woff2 fonts; images)
        For people: dashboard, menu (the local web dashboard and change walkthroughs; a menu of tasks; Explorer right-click entries)

        Usage of one command, with its options: oml <command> --help
        Every usage line, by area: oml help --all
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        --timings on a command that loads a module (apply, inspect, describe, validate, install, batch, ...) adds "timings": milliseconds per phase.
        """;

    // Usage lines of commands that were merged into another one (the new form is in Text). Only `oml <old> --help` and
    // `oml help <old>` print them, so the old name keeps its exact help; help, search and docs show the new form.
    const string MergedText = """
        oml templates           (local template/sample modules and bundles)
        oml stats <module.oml>
        oml header <file.oml>...   (the module header without loading the model: name, version, upgrade level, key, hashes, signature, type)
        oml header --diff <a.oml> <b.oml>   (which header fields differ)
        oml expression "<text>"   (parse an expression: ok and its canonical form, or the parse error; exit 1 on invalid)
        oml mock <Api.Method> | --api <Api> | --all [--personas <file.json>] [--out <folder>] [--project <name>]   (mock responses for a mock server such as Castlemock, chained by the map: one persona keeps its values across APIs)
        oml outsystems-docs [status]   (the local copy of the OutSystems 11 docs for doc --search --library: sources, commit, files)
        oml outsystems-docs fetch [--source product|support|howtos]... [--from <unzipped repository>]   (download or update the OutSystems 11 docs from GitHub: Markdown, about 15 MB)
        oml outsystems-docs update [--check] [--source product|support|howtos]...   (fetch again only the sources OutSystems changed; --check lists the changed pages and downloads nothing)
        oml explain-warning <id | title words>       (cause and fix of a validation message, such as NoErrorHandlingInFlow)
        oml explain-warning --catalog [<prefix>]      (list the verification ids oml knows; a prefix narrows, such as Entity_)
        oml model-types [<TypeName>...]   (members of model/plugin types on the installed build; no name lists the useful types)
        oml widget-types             (registered widget descriptors)
        oml selftest   (build check; also runs by itself on a Service Studio build not seen before)
        oml show-change <file.md> [--port N] [--no-open]   (an agent's change walkthrough on the dashboard: call stacks, flows, element links, review, comments)
        oml explorer-menu [--remove | --status]   (Explorer right-click entries for the current user: fonts, images, .oml files, folders)
        oml log summary [--hours N] [--file <usage.jsonl>]   (per command: count, failures, median ms, top failures; --hours: oml vs agent time)
        """;

    // Merged commands: the new form (the command, then subcommand words and flags) and the command that runs it. Normalize
    // turns the new form into that command, so the two run the same code and give the same output and exit code; the old
    // name stays a hidden alias. A flag of the form may stand anywhere after the command, a subcommand word comes first.
    public static readonly (string Form, string Runs)[] Merged =
    [
        ("inspect --header", "header"), ("inspect --stats", "stats"), ("doc library", "outsystems-docs"), ("doc warning", "explain-warning"),
        ("spec model-types", "model-types"), ("spec widget-types", "widget-types"), ("spec expr", "expression"), ("new --templates", "templates"),
        ("learn usage", "log summary"), ("menu explorer", "explorer-menu"), ("doctor --selftest --show", "selftest"),
        ("dashboard --change", "show-change"), ("map mock", "mock"),
    ];

    // The areas of Text in order, each with the command words of its lines.
    public static readonly (string Area, string[] Commands)[] Areas = ReadAreas();

    // An area heading of Text: "Read a module:".
    public static bool IsArea(string line) => line.Length > 1 && char.IsUpper(line[0]) && line.EndsWith(':') && !line.Contains("  ");

    static (string, string[])[] ReadAreas()
    {
        var areas = new List<(string Area, List<string> Commands)>();
        foreach (var line in Text.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (IsArea(line)) areas.Add((line[..^1], []));
            else if (line.StartsWith("oml ") && areas.Count > 0 && line[4..].Split(' ')[0] is { Length: > 0 } word && !areas[^1].Commands.Contains(word))
                areas[^1].Commands.Add(word);
        }
        return areas.Select(a => (a.Area, a.Commands.ToArray())).ToArray();
    }

    // The area of a command word, or null.
    public static string? AreaOf(string command) => Areas.FirstOrDefault(a => a.Commands.Contains(command)).Area;

    // Old command names that still work, hidden: they are never in help, search or docs (one "Old names" note in
    // skill/reference/commands.md lists them). Normalize turns one into its new name once, at the start of argument
    // handling (the launcher, Cli.Run and the steps of `oml batch`), so everything after it sees the new name only.
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["census"] = "stats", ["meta"] = "header", ["expr"] = "expression", ["api"] = "model-types", ["widgets"] = "widget-types",
        ["setup"] = "explorer-menu", ["run"] = "batch", ["evolve"] = "learn", ["skills"] = "team-rules", ["library"] = "outsystems-docs",
        ["warn"] = "explain-warning", ["lt"] = "lifetime", ["dif"] = "show-change", ["qa"] = "browser",
    };

    // The new name of a command word, or the word itself.
    public static string NewName(string command) => Aliases.TryGetValue(command, out var name) ? name : command;

    // The arguments with an old command name replaced: `lt` -> `lifetime`, `test scaffold` -> `test bdd`; also the
    // command asked about in `oml help <command>`. A merged command's new form becomes the command that runs it
    // (`inspect a.oml --stats` -> `stats a.oml`); with --help it asks for the new form's lines (`help "inspect --stats"`).
    // A copy; the input is not changed.
    public static string[] Normalize(string[] args)
    {
        if (args.Length == 0) return args;
        var copy = (string[])args.Clone();
        var at = copy[0] == "help" && copy.Length > 1 ? 1 : 0;
        copy[at] = NewName(copy[at]);
        if (copy[0] == "test" && copy.Length > 1 && copy[1] == "scaffold") copy[1] = "bdd";
        return at == 0 ? Merge(copy) : copy;
    }

    static string[] Merge(string[] a)
    {
        foreach (var (form, runs) in Merged)
        {
            var words = form.Split(' ');
            if (a[0] != words[0] || a.Length < 2) continue;
            var rest = a.Skip(1).ToList();
            var fits = true;
            foreach (var word in words.Skip(1))
            {
                if (word.StartsWith("--") ? !rest.Remove(word) : rest.Count == 0 || rest[0] != word) { fits = false; break; }
                if (!word.StartsWith("--")) rest.RemoveAt(0);
            }
            // `oml doc warning` alone is the warnings topic (prefix of `warnings`), as before; an id or option follows the new form.
            if (!fits || form == "doc warning" && rest.Count == 0) continue;
            if (rest.Contains("--help") || rest.Contains("-h")) return ["help", form];
            return [.. runs.Split(' '), .. rest];
        }
        return a;
    }

    // The help request: no arguments, or help, --help or -h first. Such a request prints Overview, or Text with --all.
    public static bool IsHelp(string[] args) => args.Length == 0 || args[0] is "help" or "--help" or "-h";
    // `oml help <command>` is the same as `oml <command> --help`.
    public static string HelpText(string[] args) =>
        args.Contains("--all") ? Text : args.Length > 1 && args[0] == "help" && !args[1].StartsWith('-') ? For(args[1]) : Overview;

    // `oml <command> --help`: only that command's usage lines; the whole usage when the command has none. A merged
    // command's old name gets its old lines; a new form ("inspect --stats", "doc library") the lines that hold all its words.
    public static string For(string? command)
    {
        if (command is null or "help" or "--help" or "-h") return Text;
        var lines = Lines(Text, command);
        if (lines.Count == 0) lines = Lines(MergedText, command);
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : Text;
    }

    static List<string> Lines(string text, string command)
    {
        var words = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l == "oml " + command || l.StartsWith("oml " + command + " ")
            || words.Length > 1 && l.StartsWith("oml " + words[0] + " ") && words.Skip(1).All(UsageWords(l).Contains)).ToList();
    }

    // The words of a usage line before its description in parentheses.
    static List<string> UsageWords(string line) => line[4..].Split(' ', StringSplitOptions.RemoveEmptyEntries).TakeWhile(w => !w.StartsWith('(')).ToList();

    // The new form a usage line of Text shows ("inspect --stats" for `oml inspect <module.oml> --stats`), or null.
    public static string? FormOf(string line)
    {
        if (!line.StartsWith("oml ")) return null;
        var words = UsageWords(line);
        foreach (var (form, _) in Merged)
        {
            var parts = form.Split(' ');
            if (words.Count > 0 && words[0] == parts[0] && parts.Skip(1).All(p => p.StartsWith("--") ? words.Contains(p) : words.Count > 1 && words[1] == p)) return form;
        }
        return null;
    }

    // A command name as an older usage log wrote it ("lt status", "test scaffold", "census"): the new name.
    public static string NewLogName(string cmd)
    {
        if (cmd == "test scaffold") return "test bdd";
        var space = cmd.IndexOf(' ');
        return space < 0 ? NewName(cmd) : NewName(cmd[..space]) + cmd[space..];
    }
}
