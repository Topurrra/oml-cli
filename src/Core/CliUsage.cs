namespace OmlCli;

// The usage text. No model types: the launcher also compiles this file, so `oml <command> --help` works for the
// commands it runs itself (lt, font, doctor, setup, image, dashboard), even when Service Studio cannot load.
static class CliUsage
{
    public const string Text = """
        oml version
        oml new <out.oml> [--name N] [--kind WebResponsive|Mobile|Service|ReactiveLibrary|...]
        oml new <out.oml> --template <name-or-exported.oml> [--name N]
        oml new <out.oml> --from-template <template.oml> [--name N] [--kind K]
        oml app list <file.oap|file.osp>
        oml app extract <file.oap|file.osp> <outdir>
        oml app pack <out.oap> --name <AppName> <module.oml|extension.xif>...
        oml extension inspect <file.xif>
        oml extension new <out.xif> --name <Name> [--spec <spec.json>]
        oml templates           (local template/sample modules and bundles)
        oml inspect <module.oml> [--own] [--only entities,structures,actions,screens,blocks,references,timers,roles,processes,rest,static,siteProperties] [--history] [--changed-since <yyyy-mm-dd>]
        oml describe <module.oml> <Element>... [--depth N] [--in <folder|file.oml>]... [--history] [--css]
        oml find <Name|Pat*> [module.oml] [--in <folder|file.oml>]...  (which module has it: own, referenced, producer files)
        oml find <module.oml> --text "<words>" [--top N]  (this module's elements ranked by name, description, expressions, SQL, labels)
        oml census <module.oml>
        oml architecture [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--out <file.html>] [--json <file.json>]
        oml check [<module.oml>] [--in <folder|file.oml|file.oap>]... [--app <Name>]... [--team <Name>] [--layers <layers.json>] [--rules <oml-rules.json>] [--baseline <old-check.json>] [--out <report.html>] [--json <report.json>] [--fail-on high|medium|low]
        oml check --init-rules [--out <oml-rules.json>]   (writes the team rules file: every rule, on/off, severity, options)
        oml review <before.oml> <after.oml> [--md <file.md>] [--out <file.html>] [--notes <file.md>] [--rules <oml-rules.json>]   (what changed, production-rule risks, new check findings)
        oml openapi <module.oml> [--api <RestServiceName>] [--out <file.json|folder>] [--server <url>]...
        oml preview <module.oml> <Block|Screen|Email> [--arg Name=<expression>]... [--theme light|dark] [--width <px>] [--height <px>] [--out <file.html>] [--screenshot <file.png>] [--wait <ms>] [--click <selector>] [--type <selector>=<text>] [--eval <js>] [--serve [--no-open]]
        oml test scaffold <module.oml> <ServerAction>... --bdd <BDDFramework.oml> [--out <spec.json>] [--module-name <TestModule>] [--producer <path>]   (spec for BDD test screens in a separate test module)
        oml tree <module.oml> <ElementName> [--depth N]   (object tree of one action, screen, entity, ...; at most 250 parts)
        oml validate <module.oml> [--warnings]
        oml apply <source.oml> <spec.json>... <staged-out.oml> [--describe A,B,...] [--describe-created]
        oml spec [<key>]                   (reference for one spec key; no key lists them. Runs without Service Studio)
        oml spec --search "<words>" [--top N]   (ranked search of the spec keys and sections)
        oml spec check <spec.json>        (checks a spec's shape without Service Studio; exit 1 on problems)
        oml spec schema [--out <file.json>] [--force]   (JSON Schema for spec files)
        oml doc [<topic>[#section]]       (OutSystems rules for agents by topic; no topic lists them. Runs without Service Studio)
        oml doc --search "<words>" [--top N] [--in <folder>]   (ranked search of doc sections, spec keys, recipes, warnings and commands; compact JSON)
        oml warn <id | title words>       (cause and fix of a validation message, such as NoErrorHandlingInFlow)
        oml run <steps.json> [--continue]   (several commands in one process, so Service Studio loads once; "-" reads the steps from stdin)
        oml translations export <module.oml> <file> [--format Excel|xlsx|ResX]
        oml install <staged.oml> <target.oml>

        oml studio                          (Service Studio's MCP server: reachable? open modules)
        oml pull [Module] [--out file.oml]  (save the module open in Service Studio to a file; needs 11.55.93+)
        oml publish <Module | file.oml>     (1-Click Publish through Service Studio)
        oml references <Module | file.oml>  (Refresh All references through Service Studio)
        oml lt environments|teams|team-apps|apps|download|tag|deploy|status|abort|continue (LifeTime API v2; run 'oml lt' for usage)
        oml font woff2 <input> [output.woff2] [--force]
        oml font batch <folder> [--out folder] [--force]
        oml image <input> [--to png|jpeg|webp|svg|ico] [--max-width N] [--max-height N] [--quality 1-100] [--out file] [--force] [--precision 0-10] [--strip-title] [--remove-ids] [--aggressive]   (convert, resize or optimize a raster image or SVG)
        oml image <logo.png> --to svg [--mode trace|embed] [--colors 1-32] [--tolerance 0.1-5] [--min-area N] [--alpha-threshold 1-255] [--transparent-bg]   (trace a logo or icon to vector paths)
        oml image <input> --to ico [--sizes 16,32,48]   (multi-size Windows icon or favicon; sizes 1-256)
        oml image batch <folder> [same options] [--out folder]

        oml api [<TypeName>...]   (members of model/plugin types on the installed build; no name lists the useful types)
        oml widgets             (registered widget descriptors)
        oml selftest   (build check; also runs by itself on a Service Studio build not seen before)
        oml doctor [--text] [--selftest]   (checks the setup and says what to fix)
        oml dashboard [--port N] [--no-open] [--module <file.oml>]   (local web dashboard for developers; reads only, on 127.0.0.1)
        oml setup [--remove | --status]   (Explorer right-click entries for the current user: fonts, images, .oml files, folders)
        oml log summary [--file <usage.jsonl>]   (per command: count, failures, median ms, top failures; set OML_LOG=1 to write the log)
        Output is compact JSON on stdout. Global --pretty includes empty fields and indents. Exit 0 ok, 1 model/validation failure, 2 usage/environment.
        --timings on a command that loads a module (apply, inspect, describe, validate, install, run, ...) adds "timings": milliseconds per phase.
        """;

    // `oml <command> --help`: only that command's usage lines; the whole usage when the command has none.
    public static string For(string? command)
    {
        if (command is null or "help" or "--help" or "-h") return Text;
        var lines = Text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l == "oml " + command || l.StartsWith("oml " + command + " ")).ToList();
        return lines.Count > 0 ? string.Join(Environment.NewLine, lines) : Text;
    }
}
