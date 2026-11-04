namespace OmlCli;

// The usage text. No model types: the launcher also compiles this file, so `oml <command> --help` works for the
// commands it runs itself (lifetime, font, doctor, explorer-menu, image, dashboard), even when Service Studio cannot load.
static class CliUsage
{
    public const string Text = """
        oml version
        oml new <out.oml> [--name N] [--kind WebResponsive|Mobile|Service|ReactiveLibrary|...]
        oml new <out.oml> --template <name-or-exported.oml> [--name N]
        oml new <out.oml> --from-template <template.oml> [--name N] [--kind K]
        oml app list <file.oap|file.osp>
        oml app extract <file.oap|file.osp> <outdir>
        oml app check <file.oap|file.osp> --producers <folder|package> [--deep]
        oml app pack <out.oap> --name <AppName> <module.oml|extension.xif>...
        oml extension inspect <file.xif>
        oml extension new <out.xif> --name <Name> [--spec <spec.json>]
        oml templates           (local template/sample modules and bundles)
        oml inspect <module.oml> [--own] [--only entities,structures,actions,screens,blocks,references,timers,roles,processes,rest,static,siteProperties] [--history] [--changed-since <yyyy-mm-dd>]
        oml inspect --in <folder> [--recursive] [--own] [--only ...]   (every .oml in the folder in one process: {modules:[{file,...}]}, a broken file gives an error entry)
        oml describe <module.oml> <Element>... [--depth N] [--in <folder|file.oml>]... [--history] [--css]
        oml find <Name|Pat*> [module.oml] [--in <folder|file.oml>]...  (which module has it: own, referenced, producer files)
        oml find <module.oml> --text "<words>" [--top N]  (this module's elements ranked by name, description, expressions, SQL, labels)
        oml stats <module.oml>
        oml header <file.oml>...   (the module header without loading the model: name, version, upgrade level, key, hashes, signature, type)
        oml header --diff <a.oml> <b.oml>   (which header fields differ)
        oml expression "<text>"   (parse an expression: ok and its canonical form, or the parse error; exit 1 on invalid)
        oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>] [--force]
        oml architecture --in <folder|file.oml|file.oap>... --publish-order [--changed <names file|folder>] [--app <Name>]... [--layers <layers.json>]   (JSON: the modules producers first, cycles; writes no file)
        oml check [<module.oml>] [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--rules <oml-rules.json>] [--baseline <old-check.json>] [--out <report.html>] [--json <report.json>] [--fail-on high|medium|low]
        oml check --init-rules [--out <oml-rules.json>]   (writes the team rules file: every rule, on/off, severity, options)
        oml check --diff <before.oml> <after.oml> [--rules <oml-rules.json>]   (findings fixed and introduced per rule; exit 1 when one is introduced)
        oml review <before.oml> <after.oml> [--md <file.md>] [--out <file.html>] [--notes <file.md>] [--rules <oml-rules.json>]   (what changed, production-rule risks, new check findings)
        oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...
        oml preview <module.oml> <Block|Screen|Email> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>] [--wait <ms>] [--click <selector>] [--type <selector>=<text>] [--eval <js>] [--serve [--no-open]]
        oml test bdd <module.oml> <ServerAction>... --bdd <BDDFramework.oml> [--out <spec.json>] [--module-name <TestModule>] [--producer <path>]   (spec for BDD test screens in a separate test module)
        oml flow <module.oml> <Action>   (one flow as a graph: nodes with their positions and what they do, and the connectors; the Dif page draws it)
        oml tree <module.oml> <ElementName> [--depth N]   (object tree of one action, screen, entity, ...; at most 250 parts)
        oml validate <module.oml> [--warnings]
        oml apply <source.oml> <spec.json>... <staged-out.oml> [--describe A,B,...] [--describe-created] [--report <report.json>]
        oml apply --batch <index.json>   (many modules in one process: {items:[{source,spec,staged}]}; one combined summary; exit 1 if any item failed)
        oml spec [<key>]                   (reference for one spec key; no key lists them. Runs without Service Studio)
        oml spec --search "<words>" [--top N]   (ranked search of the spec keys and sections)
        oml spec check <spec.json> [--module <source.oml>]   (checks a spec's shape; with --module also its names, nothing saved; exit 1 on problems)
        oml spec schema [--out <file.json>] [--force]   (JSON Schema for spec files)
        oml doc [<topic>[#section]]       (OutSystems rules for agents by topic; no topic lists them. Runs without Service Studio)
        oml doc --search "<words>" [--top N] [--in <folder> | --library]   (ranked search of doc sections, spec keys, recipes, warnings and commands; --library searches the OutSystems 11 docs instead; compact JSON)
        oml outsystems-docs [status]   (the local copy of the OutSystems 11 docs for doc --search --library: sources, commit, files)
        oml outsystems-docs fetch [--source product|support|howtos]... [--from <unzipped repository>]   (download or update the OutSystems 11 docs from GitHub: Markdown only, about 15 MB)
        oml outsystems-docs update [--check] [--source product|support|howtos]...   (fetch again only the sources OutSystems changed; --check lists the changed pages and downloads nothing)
        oml team-rules [list]   (your company skills: Markdown rules that override OutSystems best practices; oml doc <name> prints one)
        oml team-rules add <file.md> [--force]   (check a skill file (name and description frontmatter, size, secrets) and install it for this user)
        oml team-rules remove <name>   (uninstall one company skill; the source file stays)
        oml explain-warning <id | title words>       (cause and fix of a validation message, such as NoErrorHandlingInFlow)
        oml explain-warning --catalog [<prefix>]      (list the verification ids oml knows; a prefix narrows, such as Entity_)
        oml memory init [--module <file.oml> | --dir <folder>] [--force]   (create the project's memory store: .oml/memory/memory.log in the project root folder (the current folder); the only command that creates one)
        oml memory add "<one fact>" [--kind fact|decision|pref|trap|log|glossary|answer] [--module <Name>] [--elements A,B] [--why "<reason>"] [--tags a,b] [--supersedes <id>] [--conf 0-100] [--src <agent>] [--valid-from <date>] [--valid-until <date>]   (write one record of at most 300 bytes; secrets are refused)
        oml memory recall "<question>" [--module <Name>] [--kind K] [--top N] [--include-stale]   (the best records for a question, at most 20, ranked with how they helped; read only)
        oml memory list [--module <Name>] [--kind K] [--top N] [--all]   (records, newest first; --all adds replaced and retracted ones; read only)
        oml memory show <id>   (one record in full, with its status; read only)
        oml memory history <id>   (what replaced what; read only)
        oml memory retract <id> --why "<reason>"   (mark a record as no longer true; it stays in the file)
        oml memory doctor [--module <file.oml>] [--file <usage.jsonl>]   (check the hash chain, the file end and the index; with a module, that the elements still exist; traps that repeat in the log)
        oml memory handoff [--module <Name>] [--top N]   (a short brief for the next agent from the newest records; read only)
        oml memory traps [--command "<command>"] [--module <Name>] [--top N]   (live trap records; with --command, those the command may run into, by words; read only, never blocks)
        oml batch <steps.json> [--continue]   (several commands in one process, so Service Studio loads once; "-" reads the steps from stdin)
        oml translations export <module.oml> <file> [--format Excel|xlsx|ResX]
        oml install <staged.oml> <target.oml> [--allow-existing-errors]
        oml install --batch <index.json> [--allow-existing-errors]   (items {staged,target}; checks all first, installs none if any is refused)

        oml studio                          (Service Studio's MCP server: reachable? open modules)
        oml pull [Module] [--out file.oml]  (save the module open in Service Studio to a file; needs 11.55.93+)
        oml publish <Module | file.oml>     (1-Click Publish through Service Studio)
        oml publish --in <folder> --order <order.json>   (publish many modules in that order; stops at the first failure)
        oml references <Module | file.oml>  (Refresh All references through Service Studio)
        oml lifetime environments|teams|team-apps|apps|download|tag|deploy|status|abort|continue (LifeTime API v2; run 'oml lifetime' for usage)
        oml qa page <url> [--headed] [--width N] [--height N] [--timeout <ms>] [--screenshot <file.png>] [--engine auto|cdp|uia] [--no-session]   (test a running app: what the screen shows, compactly: actions, inputs, tables, messages, layout, the OutSystems calls it made, errors)
        oml qa run <qa.json> [--base-url <url>] [--headed] [--width N] [--height N] [--timeout <ms>] [--out <folder>] [--continue] [--engine auto|cdp|uia] [--no-session]   (run QA steps like a manual tester: open, click, fill, select, press, wait, expect; exit 1 when a step fails)
        oml qa record <url> [--out <qa.jsonc>] [--engine auto|cdp|uia] [--width N] [--height N]   (open the app in a window and write a QA file from what you click and type; close the window or press Ctrl+C to finish)
        oml qa start [--engine auto|cdp|uia] [--headed] [--width N] [--height N]   (keep one browser open: later qa run and qa page skip the browser start and keep the login)
        oml qa stop   (close the browser of oml qa start)
        oml font woff2 <input> [output.woff2] [--force]
        oml font batch <folder> [--out folder] [--force]
        oml image <input> [--to png|jpeg|webp|svg|ico] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force] [--precision 0-10] [--strip-title] [--remove-ids] [--aggressive]   (convert, resize or optimize a raster image or SVG)
        oml image <logo.png> --to svg [--mode trace|embed] [--colors 1-32] [--tolerance 0.1-5] [--min-area N] [--alpha-threshold 1-255] [--transparent-bg]   (trace a logo or icon to vector paths)
        oml image <input> --to ico [--sizes 16,32,48]   (multi-size Windows icon or favicon; sizes 1-256)
        oml image batch <folder> [same options] [--out folder]

        oml model-types [<TypeName>...]   (members of model/plugin types on the installed build; no name lists the useful types)
        oml widget-types             (registered widget descriptors)
        oml selftest   (build check; also runs by itself on a Service Studio build not seen before)
        oml doctor [--text] [--selftest]   (checks the setup and says what to fix)
        oml show-change <file.md> [--port N] [--no-open]   (an agent's change walkthrough on the dashboard: call stacks, flows, element links, review, comments)
        oml dashboard [--port N] [--no-open] [--module <file.oml>]   (local web dashboard for developers; reads only, on 127.0.0.1)
        oml hooks [status|install|remove] [--dir <project>]   (Claude Code hooks for this project: refuse reading the large skill references whole and changing .oml files)
        oml explorer-menu [--remove | --status]   (Explorer right-click entries for the current user: fonts, images, .oml files, folders)
        oml log summary [--file <usage.jsonl>]   (per command: count, failures, median ms, top failures; OML_LOG=0 turns the log off)
        oml learn report [--since <date>] [--file <usage.jsonl>]... [--check <report.json>]... [--md <file.md>]   (where agents fail and retry, from the local log and memory; changes nothing)
        oml learn propose [--maintainer [--repo <folder>]] [report options]   (proposals with evidence: trap records, a draft company skill, doc patches; nothing applied)
        oml learn show [<id>] | accept <id> | reject <id> [--why <text>] | history | undo <id>   (a person accepts; each accepted change can be undone)
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        --timings on a command that loads a module (apply, inspect, describe, validate, install, batch, ...) adds "timings": milliseconds per phase.
        """;

    // `oml`, `oml help` and `oml --help`: the overview, about 600 tokens. Every command word of Text appears here
    // (tests/doc-check.ps1 checks it). `oml help --all` prints Text instead.
    public const string Overview = """
        Commands by area (OutSystems 11 modules; compact JSON out, a readable view in a console):
        Read a module: inspect, describe, find, tree, flow, stats, header, expression, openapi (what a module holds, one element, search, counts, the header, an expression, OpenAPI)
        Change a module: new, apply, install, validate, spec, translations (new module; edit by spec into a staged copy; install after checks; spec keys; translation files)
        Check and review: check, review, architecture (rules and findings; a change before and after; module map and publish order)
        Preview and UI: preview (a screen or block in light and dark, screenshot, serve it)
        Docs and search: doc, spec, explain-warning, outsystems-docs, team-rules (OutSystems rules by topic and search; spec keys; cause and fix of a warning; the OutSystems 11 docs; company rules)
        Memory and learning: memory, learn (project facts, decisions and traps for agents; lessons from where agents fail and retry)
        Agents: batch, hooks, show-change, dashboard (several commands in one process; Claude Code hooks; a change walkthrough; the local web dashboard)
        Service Studio and LifeTime: studio, pull, publish, references, lifetime (Studio's open modules; save, publish and refresh references; the LifeTime API)
        Testing: qa, test bdd (test a running app in a browser; BDD test specs for server actions)
        Assets: font, image (woff2 fonts; convert, resize, trace and ICO images)
        Packages: app, extension, templates (.oap and .osp packages; .xif extensions; sample modules)
        Setup: version, selftest, doctor, explorer-menu, log, model-types, widget-types (the build and its checks; Explorer right-click entries; the usage log; OutSystems API members)

        Usage of one command, with its options: oml <command> --help
        Every usage line: oml help --all
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        --timings on a command that loads a module (apply, inspect, describe, validate, install, batch, ...) adds "timings": milliseconds per phase.
        """;

    // Old command names that still work, hidden: they are never in help, search or docs (one "Old names" note in
    // skill/reference/commands.md lists them). Normalize turns one into its new name once, at the start of argument
    // handling (the launcher, Cli.Run and the steps of `oml batch`), so everything after it sees the new name only.
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["census"] = "stats", ["meta"] = "header", ["expr"] = "expression", ["api"] = "model-types", ["widgets"] = "widget-types",
        ["setup"] = "explorer-menu", ["run"] = "batch", ["evolve"] = "learn", ["skills"] = "team-rules", ["library"] = "outsystems-docs",
        ["warn"] = "explain-warning", ["lt"] = "lifetime", ["dif"] = "show-change",
    };

    // The new name of a command word, or the word itself.
    public static string NewName(string command) => Aliases.TryGetValue(command, out var name) ? name : command;

    // The arguments with an old command name replaced: `lt` -> `lifetime`, `test scaffold` -> `test bdd`; also the
    // command asked about in `oml help <command>`. A copy; the input is not changed.
    public static string[] Normalize(string[] args)
    {
        if (args.Length == 0) return args;
        var copy = (string[])args.Clone();
        var at = copy[0] == "help" && copy.Length > 1 ? 1 : 0;
        copy[at] = NewName(copy[at]);
        if (copy[0] == "test" && copy.Length > 1 && copy[1] == "scaffold") copy[1] = "bdd";
        return copy;
    }

    // A command name as an older usage log wrote it ("lt status", "test scaffold", "census"): the new name.
    public static string NewLogName(string cmd)
    {
        if (cmd == "test scaffold") return "test bdd";
        var space = cmd.IndexOf(' ');
        return space < 0 ? NewName(cmd) : NewName(cmd[..space]) + cmd[space..];
    }

    // The help request: no arguments, or help, --help or -h first. Such a request prints Overview, or Text with --all.
    public static bool IsHelp(string[] args) => args.Length == 0 || args[0] is "help" or "--help" or "-h";
    // `oml help <command>` is the same as `oml <command> --help`.
    public static string HelpText(string[] args) =>
        args.Contains("--all") ? Text : args.Length > 1 && args[0] == "help" && !args[1].StartsWith('-') ? For(args[1]) : Overview;

    // `oml <command> --help`: only that command's usage lines; the whole usage when the command has none.
    public static string For(string? command)
    {
        if (command is null or "help" or "--help" or "-h") return Text;
        var lines = Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l == "oml " + command || l.StartsWith("oml " + command + " ")).ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : Text;
    }
}
