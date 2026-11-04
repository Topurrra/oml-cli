using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmlCli;

// `oml map ...` queries: short answers from an application map that `oml map build` (Core) wrote, without loading a
// module or Service Studio. overview, uses, impact, data, api and list. The project comes from --project, else from
// .oml/map.json in the current folder (written by map build), else the only map there is.
static class MapCommand
{
    sealed class MapException(string message, int code = 2, string? hint = null) : Exception(message)
    {
        public int Code { get; } = code;
        public string? Hint { get; } = hint;
    }

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args[0] is "--help" or "-h" or "help") { Console.WriteLine(CliUsage.For("map")); return args.Length == 0 ? 2 : 0; }
            var (words, project) = Options(args[1..]);
            return args[0] switch
            {
                "list" => List(),
                "overview" => Overview(Load(project)),
                "uses" => Uses(Load(project), One(words, "uses <element>")),
                "impact" => Impact(Load(project), One(words, "impact <element>")),
                "data" => Data(Load(project), One(words, "data <Entity>")),
                "api" => Api(Load(project), One(words, "api <RestMethod>")),
                _ => throw new MapException($"Unknown map command '{args[0]}'.", 2, "Run 'oml map --help'."),
            };
        }
        catch (MapException e)
        {
            TerminalOutput.PrintObject(new { error = e.Message, hint = e.Hint });
            return e.Code;
        }
    }

    static (List<string> words, string? project) Options(string[] args)
    {
        var words = new List<string>();
        string? project = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--project" && i + 1 < args.Length) project = args[++i].ToLowerInvariant();
            else if (args[i].StartsWith("--")) throw new MapException($"Unknown option {args[i]}.", 2, "Run 'oml map --help'.");
            else words.Add(args[i]);
        }
        return (words, project);
    }

    static string One(List<string> words, string usage) =>
        words.Count == 1 ? words[0] : throw new MapException($"Usage: oml map {usage} [--project <name>]", 2);

    static string Root => Environment.GetEnvironmentVariable("OML_MAPS") is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "maps");

    static int List()
    {
        var maps = Directory.Exists(Root) ? Directory.EnumerateDirectories(Root).Where(d => File.Exists(Path.Combine(d, "project.json"))).Order(StringComparer.OrdinalIgnoreCase)
            .Select(d => JsonNode.Parse(File.ReadAllText(Path.Combine(d, "project.json")))!.AsObject())
            .Select(p => (JsonNode)new JsonObject { ["project"] = p["project"]?.DeepClone(), ["files"] = p["files"]?.DeepClone(), ["builtAt"] = p["builtAt"]?.DeepClone(), ["inputs"] = p["inputs"]?.DeepClone() }).ToArray() : [];
        var output = new JsonObject { ["folder"] = Root, ["maps"] = new JsonArray(maps) };
        if (maps.Length == 0) output["hint"] = "No map yet. Build one: oml map build <project> --in <folder of the project's .oap files>";
        TerminalOutput.PrintNode(output);
        return 0;
    }

    // ---- the map in memory ----

    sealed record FlowRef(string Module, string App, JsonObject Flow)
    {
        public string Path => Flow["path"]!.GetValue<string>();
        public string Id => Module + "/" + Path;
    }

    sealed class Map
    {
        public required string Project;
        public required JsonObject Info;
        public List<JsonObject> Modules = [];            // architecture snapshots (layer, kind, counts), own modules only
        public List<JsonObject> Facts = [];              // MapBuild facts, one per module
        public List<JsonObject> Edges = [];
        public List<FlowRef> Flows = [];
        public List<string> Stale = [];
        public List<string> Missing = [];
    }

    static Map Load(string? project)
    {
        if (project is null)
        {
            var here = Path.Combine(Environment.CurrentDirectory, ".oml", "map.json");
            if (File.Exists(here)) project = JsonNode.Parse(File.ReadAllText(here))?["project"]?.GetValue<string>();
            else if (Directory.Exists(Root) && Directory.GetDirectories(Root).Where(d => File.Exists(Path.Combine(d, "project.json"))).ToList() is { Count: 1 } only)
                project = Path.GetFileName(only[0]);
        }
        if (project is null) throw new MapException("Which project? This folder has no .oml/map.json.", 2, "Give --project <name>, or list the maps: oml map list");
        var folder = Path.Combine(Root, project);
        var projectFile = Path.Combine(folder, "project.json");
        if (!File.Exists(projectFile)) throw new MapException($"No map named '{project}'.", 1, $"Build it: oml map build {project} --in <folder of the project's .oap files>");
        var map = new Map { Project = project, Info = JsonNode.Parse(File.ReadAllText(projectFile))!.AsObject() };
        var modules = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(folder, "sources"), "*.json").Order(StringComparer.Ordinal))
        {
            var doc = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            var input = doc["file"]!.GetValue<string>();
            if (!File.Exists(input)) map.Missing.Add(input);
            else if (new FileInfo(input) is var fi && (fi.Length != doc["size"]?.GetValue<long>() || fi.LastWriteTimeUtc.Ticks != doc["mtime"]?.GetValue<long>())) map.Stale.Add(input);
            foreach (var m in doc["modules"]!.AsArray().OfType<JsonObject>())
            {
                var name = m["name"]!.GetValue<string>();
                // An own module wins over the same module seen from outside (a reference of another input).
                if (!modules.TryGetValue(name, out var have) || have["external"]?.GetValue<bool>() == true) modules[name] = m;
            }
            map.Edges.AddRange(doc["edges"]!.AsArray().OfType<JsonObject>());
            foreach (var f in doc["facts"]!.AsArray().OfType<JsonObject>())
            {
                map.Facts.Add(f);
                foreach (var flow in f["flows"]!.AsArray().OfType<JsonObject>())
                    map.Flows.Add(new FlowRef(f["name"]!.GetValue<string>(), f["application"]?.GetValue<string>() ?? "", flow));
            }
        }
        map.Modules = modules.Values.Where(m => m["external"]?.GetValue<bool>() != true).ToList();
        return map;
    }

    static string S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    static bool Is(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    static IEnumerable<JsonObject> Each(JsonNode? array) => (array as JsonArray)?.OfType<JsonObject>() ?? [];
    static IEnumerable<string> Strings(JsonNode? array) => (array as JsonArray)?.Select(S).Where(s => s.Length > 0) ?? [];

    // A name the person typed: Element or Module.Element.
    static (string? module, string name) Split(string text)
    {
        var dot = text.IndexOf('.');
        return dot > 0 ? (text[..dot], text[(dot + 1)..]) : (null, text);
    }

    static JsonObject Freshness(Map map)
    {
        var o = new JsonObject { ["project"] = map.Project, ["builtAt"] = map.Info["builtAt"]?.DeepClone() };
        if (map.Stale.Count + map.Missing.Count > 0)
            o["stale"] = $"{map.Stale.Count} input file(s) changed and {map.Missing.Count} are gone since the map was built. Run: oml map build {map.Project}";
        return o;
    }

    static int Print(Map map, JsonObject body)
    {
        var output = Freshness(map);
        foreach (var (k, v) in body) output[k] = v?.DeepClone();
        TerminalOutput.PrintNode(output);
        return 0;
    }

    // ---- overview ----

    static int Overview(Map map)
    {
        var facts = map.Facts.ToDictionary(f => S(f["name"]), StringComparer.OrdinalIgnoreCase);
        var apps = map.Modules.GroupBy(m => S(m["application"]), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (JsonNode)new JsonObject
            {
                ["application"] = g.Key,
                ["modules"] = new JsonArray(g.OrderBy(m => S(m["name"]), StringComparer.OrdinalIgnoreCase).Select(m =>
                {
                    var f = facts.GetValueOrDefault(S(m["name"]));
                    var c = m["counts"];
                    var parts = new List<string>();
                    void Add(string label, JsonNode? n) { if (n?.GetValue<int>() is > 0 and var v) parts.Add($"{v} {label}"); }
                    Add("entities", c?["entities"]); Add("screens", c?["screens"]); Add("server actions", c?["serverActions"]); Add("service actions", c?["serviceActions"]);
                    Add("processes", c?["processes"]); Add("timers", c?["timers"]); Add("consumed APIs", c?["restConsumed"]); Add("exposed APIs", c?["restExposed"]);
                    return (JsonNode)new JsonObject { ["name"] = S(m["name"]), ["layer"] = S(m["layer"]), ["kind"] = S(m["kind"]), ["holds"] = string.Join(", ", parts) };
                }).ToArray()),
            }).ToArray();
        var apis = map.Facts.SelectMany(f => Each(f["restConsumed"]).Select(a => (JsonNode)new JsonObject
        {
            ["api"] = S(a["name"]), ["module"] = S(f["name"]), ["baseUrl"] = S(a["baseUrl"]), ["methods"] = Each(a["methods"]).Count(),
        })).ToArray();
        return Print(map, new JsonObject
        {
            ["applications"] = new JsonArray(apps), ["consumedApis"] = new JsonArray(apis),
            ["totals"] = new JsonObject { ["applications"] = apps.Length, ["modules"] = map.Modules.Count, ["flows"] = map.Flows.Count,
                ["processes"] = map.Facts.Sum(f => Each(f["processes"]).Count()), ["apiMethods"] = map.Facts.Sum(f => Each(f["restConsumed"]).Sum(a => Each(a["methods"]).Count())) },
            ["next"] = "oml map uses <element> | impact <element> | data <Entity> | api <RestMethod>",
        });
    }

    // ---- uses and impact ----

    // Flows that call the element (an action, entity action, REST method or process launch) by name.
    static List<(FlowRef flow, JsonObject call)> CallersOf(Map map, string? module, string name) =>
        map.Flows.SelectMany(f => Each(f.Flow["calls"]).Where(c => Is(S(c["target"]), name) && (module is null || Is(S(c["module"]), module) || Is(S(c["api"]), module))).Select(c => (f, c))).ToList();

    static List<JsonObject> Definitions(Map map, string? module, string name)
    {
        var found = new List<JsonObject>();
        foreach (var f in map.Facts.Where(f => module is null || Is(S(f["name"]), module) || Each(f["restConsumed"]).Any(a => Is(S(a["name"]), module))))
        {
            var m = S(f["name"]);
            foreach (var e in Each(f["entities"]).Where(e => Is(S(e["name"]), name))) found.Add(new JsonObject { ["kind"] = "Entity", ["module"] = m, ["public"] = e["public"]?.DeepClone() });
            foreach (var s in Each(f["structures"]).Where(s => Is(S(s["name"]), name))) found.Add(new JsonObject { ["kind"] = "Structure", ["module"] = m });
            foreach (var p in Each(f["processes"]).Where(p => Is(S(p["name"]), name))) found.Add(new JsonObject { ["kind"] = "Process", ["module"] = m });
            foreach (var t in Each(f["timers"]).Where(t => Is(S(t["name"]), name))) found.Add(new JsonObject { ["kind"] = "Timer", ["module"] = m, ["action"] = S(t["action"]) });
            foreach (var a in Each(f["restConsumed"]))
                foreach (var meth in Each(a["methods"]).Where(x => Is(S(x["name"]), name)))
                    found.Add(new JsonObject { ["kind"] = "REST method", ["module"] = m, ["api"] = S(a["name"]), ["verb"] = S(meth["verb"]), ["url"] = S(meth["url"]) });
            foreach (var flow in map.Flows.Where(x => Is(x.Module, m) && Is(x.Path, name)))
                found.Add(new JsonObject { ["kind"] = S(flow.Flow["kind"]), ["module"] = m, ["public"] = flow.Flow["public"]?.DeepClone() });
        }
        return found;
    }

    static int Uses(Map map, string text)
    {
        var (module, name) = Split(text);
        var defined = Definitions(map, module, name);
        var callers = CallersOf(map, module, name);
        var referencedBy = map.Edges.Where(e => Each(e["elements"]).Any(x => Is(S(x["name"]), name)) && (module is null || Is(S(e["to"]), module)))
            .Select(e => S(e["from"])).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var readers = map.Flows.Where(f => Each(f.Flow["reads"]).Any(r => Strings(r["entities"]).Any(e => Is(e, name)))).Select(f => f.Id).Distinct().ToArray();
        var body = new JsonObject
        {
            ["element"] = text,
            ["definedIn"] = new JsonArray(defined.Select(d => (JsonNode)d).ToArray()),
            ["calledBy"] = new JsonArray(callers.Select(c => (JsonNode)new JsonObject { ["flow"] = c.flow.Id, ["application"] = c.flow.App, ["node"] = S(c.call["node"]) }).ToArray()),
            ["readBy"] = readers.Length == 0 ? null : new JsonArray(readers.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
            ["referencedBy"] = new JsonArray(referencedBy.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
        };
        if (defined.Count == 0 && callers.Count == 0 && referencedBy.Length == 0) body["hint"] = $"Nothing named '{text}' in the map. Names are exact (letters case ignored); use Module.Element to pick one module.";
        return Print(map, body);
    }

    // Everything that runs the element, up through callers (flows calling flows), processes launched on what it writes,
    // and what starts those flows (timers, process activities, launches). Depth 5.
    static int Impact(Map map, string text)
    {
        var (module, name) = Split(text);
        var affected = new Dictionary<string, (FlowRef flow, int depth, string via)>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string? module, string name, int depth, string via)>();
        queue.Enqueue((module, name, 0, text));
        // An entity: every flow that writes or reads it is affected, and the processes launched on it.
        var launched = new List<JsonObject>();
        foreach (var f in map.Facts)
            foreach (var p in Each(f["processes"]).Where(p => Is(S(p["launchOn"]?["entity"]), name)))
                launched.Add(new JsonObject { ["process"] = S(p["name"]), ["module"] = S(f["name"]), ["on"] = S(p["launchOn"]?["event"]) });
        foreach (var f in map.Flows.Where(f => Each(f.Flow["calls"]).Any(c => Is(S(c["entity"]), name)) || Each(f.Flow["reads"]).Any(r => Strings(r["entities"]).Any(e => Is(e, name)))))
            if (affected.TryAdd(f.Id, (f, 1, "uses entity " + name))) queue.Enqueue((f.Module, Leaf(f.Path), 1, f.Id));
        while (queue.TryDequeue(out var item))
        {
            if (item.depth >= 5) continue;
            foreach (var (flow, _) in CallersOf(map, item.module, item.name))
                if (affected.TryAdd(flow.Id, (flow, item.depth + 1, item.via)))
                    queue.Enqueue((flow.Module, Leaf(flow.Path), item.depth + 1, flow.Id));
        }
        // What starts the affected flows: timers on them, process activities that call them.
        var starters = new List<JsonObject>();
        foreach (var f in map.Facts)
            foreach (var t in Each(f["timers"]).Where(t => affected.Values.Any(a => Is(a.flow.Module, S(f["name"])) && Is(Leaf(a.flow.Path), S(t["action"])))))
                starters.Add(new JsonObject { ["timer"] = S(t["name"]), ["module"] = S(f["name"]), ["runs"] = S(t["action"]) });
        var body = new JsonObject
        {
            ["element"] = text,
            ["affected"] = new JsonArray(affected.Values.OrderBy(a => a.depth).ThenBy(a => a.flow.Id, StringComparer.OrdinalIgnoreCase)
                .Select(a => (JsonNode)new JsonObject { ["flow"] = a.flow.Id, ["application"] = a.flow.App, ["kind"] = S(a.flow.Flow["kind"]), ["public"] = a.flow.Flow["public"]?.DeepClone(), ["depth"] = a.depth, ["via"] = a.via }).ToArray()),
            ["processesLaunchedOn"] = launched.Count == 0 ? null : new JsonArray(launched.Select(l => (JsonNode)l).ToArray()),
            ["timers"] = starters.Count == 0 ? null : new JsonArray(starters.Select(s => (JsonNode)s).ToArray()),
            ["modules"] = new JsonArray(affected.Values.Select(a => a.flow.Module).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
            ["referencedBy"] = new JsonArray(map.Edges.Where(e => Each(e["elements"]).Any(x => Is(S(x["name"]), name)) && (module is null || Is(S(e["to"]), module)))
                .Select(e => S(e["from"])).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).Select(m => (JsonNode)JsonValue.Create(m)!).ToArray()),
        };
        if (affected.Count == 0 && launched.Count == 0) body["hint"] = $"No flow in the map calls, reads or writes '{text}'. Screens and blocks that only show it are not traced.";
        return Print(map, body);
    }

    static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

    // ---- data lineage ----

    // A link in the data: where a value comes from and where it goes, as Service Studio sees it in the flows.
    internal sealed record Link(string From, string To, string Via);

    static readonly Regex NodeRef = new(@"\b([A-Za-z_][A-Za-z0-9_]*)((?:\.[A-Za-z_][A-Za-z0-9_]*)+)", RegexOptions.CultureInvariant);

    static readonly Regex Bare = new(@"(?<![\w.""])([A-Za-z_][A-Za-z0-9_]*)(?![\w.(""])", RegexOptions.CultureInvariant);

    // Every link of every flow:
    //   api:Api.Method.out:<path>   -> entity:<Entity>.<Attribute>   (assigned to a record that a Create/Update saves)
    //   entity:<Entity>.<Attribute> -> api:Api.Method.in:<Param>     (an aggregate or Get result passed to the call)
    //   api:...out                  -> api:...in                     (one call's output passed to the next)
    // A wrapper action in between (an integration module's server action that calls the API) is followed through: its
    // outputs and inputs are action:Module.Action.out:/in: ends, and the last step replaces them with what they carry.
    internal static List<Link> Links(IEnumerable<(string module, JsonObject flow)> flows)
    {
        var raw = new List<Link>();
        foreach (var (module, flow) in flows)
        {
            var path = S(flow["path"]);
            var via = module + "/" + path;
            var self = $"action:{module}.{Leaf(path)}";
            var inputs = Strings(flow["inputs"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var outputs = Strings(flow["outputs"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var calls = Each(flow["calls"]).ToList();
            var restNodes = calls.Where(c => S(c["type"]) == "rest").ToDictionary(c => S(c["node"]), c => S(c["api"]) + "." + S(c["target"]), StringComparer.OrdinalIgnoreCase);
            var actionNodes = calls.Where(c => S(c["type"]) == "action").GroupBy(c => S(c["node"]), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => $"action:{S(g.First()["module"])}.{S(g.First()["target"])}", StringComparer.OrdinalIgnoreCase);
            // Nodes whose output is records of an entity: aggregates (List.Current.<Entity>.<Attr>) and entity Get actions (Record.<Entity>.<Attr>).
            var entityNodes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Each(flow["reads"])) entityNodes[S(r["node"])] = Strings(r["entities"]).ToArray();
            foreach (var c in calls.Where(c => S(c["type"]) == "entity" && S(c["op"]) == "get")) entityNodes[S(c["node"])] = [S(c["entity"])];

            IEnumerable<string> Sources(string expression)
            {
                foreach (Match m in NodeRef.Matches(expression))
                {
                    var (node, rest) = (m.Groups[1].Value, m.Groups[2].Value.TrimStart('.'));
                    if (restNodes.TryGetValue(node, out var api)) yield return $"api:{api}.out:{rest}";
                    else if (actionNodes.TryGetValue(node, out var action)) yield return $"{action}.out:{rest}";
                    else if (inputs.Contains(node)) yield return $"{self}.in:{node}.{rest}";
                    else if (entityNodes.TryGetValue(node, out var entities))
                    {
                        var parts = rest.Split('.');
                        var at = Array.FindIndex(parts, p => entities.Contains(p, StringComparer.OrdinalIgnoreCase));
                        if (at >= 0 && at + 1 < parts.Length) yield return $"entity:{parts[at]}.{parts[at + 1]}";
                    }
                }
                foreach (Match m in Bare.Matches(expression))
                    if (inputs.Contains(m.Value)) yield return $"{self}.in:{m.Value}";
            }
            // Calls: arguments that come from an API output, an entity attribute or this action's own inputs.
            foreach (var c in calls.Where(c => S(c["type"]) is "rest" or "action"))
            {
                var target = S(c["type"]) == "rest" ? $"api:{S(c["api"])}.{S(c["target"])}" : $"action:{S(c["module"])}.{S(c["target"])}";
                foreach (var (param, value) in (c["args"] as JsonObject) ?? [])
                    foreach (var source in Sources(S(value)))
                        raw.Add(new Link(source, $"{target}.in:{param}", via));
            }
            // Entity writes: the record passed to Create/Update, and what was assigned into it.
            foreach (var c in calls.Where(c => S(c["type"]) == "entity" && S(c["op"]) is "create" or "update" or "createOrUpdate"))
            {
                var entity = S(c["entity"]);
                var record = S((c["args"] as JsonObject)?["Source"]);
                if (record.Length == 0) continue;
                foreach (var a in Each(flow["assigns"]))
                {
                    var to = S(a["to"]);
                    if (!to.StartsWith(record + ".", StringComparison.OrdinalIgnoreCase)) continue;
                    var attribute = to.Split('.')[^1];
                    foreach (var source in Sources(S(a["value"]))) raw.Add(new Link(source, $"entity:{entity}.{attribute}", via));
                }
            }
            // This action's outputs: what was assigned to them.
            foreach (var a in Each(flow["assigns"]))
            {
                var to = S(a["to"]);
                if (!outputs.Contains(to.Split('.')[0])) continue;
                foreach (var source in Sources(S(a["value"]))) raw.Add(new Link(source, $"{self}.out:{to}", via));
            }
        }
        return Resolve(raw);
    }

    // Replaces action ends with what they carry, by path prefix: a caller reading Wrapper.Customer.CustomerId reads what
    // the wrapper assigned to Customer (api:CrmApi.GetClient.out:Response) plus .CustomerId. Four passes reach through
    // wrappers of wrappers. Only links between APIs and entities are kept.
    static List<Link> Resolve(List<Link> raw)
    {
        var all = new List<Link>(raw);
        var seen = all.Select(l => (l.From, l.To)).ToHashSet();
        static bool Under(string path, string prefix) => path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase);
        for (var pass = 0; pass < 4; pass++)
        {
            var added = new List<Link>();
            foreach (var l in all)
            {
                if (l.From.StartsWith("action:") && l.From.Contains(".out:"))
                    foreach (var k in all.Where(k => k.To.StartsWith("action:") && Under(l.From, k.To)))
                        added.Add(new Link(k.From + l.From[k.To.Length..], l.To, k.Via + " -> " + l.Via));
                if (l.To.StartsWith("action:") && l.To.Contains(".in:"))
                    foreach (var k in all.Where(k => k.From.StartsWith("action:") && Under(k.From, l.To)))
                        added.Add(new Link(l.From + k.From[l.To.Length..], k.To, l.Via + " -> " + k.Via));
            }
            var fresh = added.Where(a => seen.Add((a.From, a.To))).ToList();
            if (fresh.Count == 0) break;
            all.AddRange(fresh);
        }
        return all.Where(l => !l.From.StartsWith("action:") && !l.To.StartsWith("action:")).ToList();
    }

    static List<Link> Links(Map map) => Links(map.Flows.Select(f => (f.Module, f.Flow)));

    // For oml mock: the facts of every module and the resolved links, or why the map cannot be read.
    internal sealed class MapLoadException(string message, int code, string? hint) : Exception(message)
    {
        public int Code { get; } = code;
        public string? Hint { get; } = hint;
    }
    internal sealed record Facts(string Project, List<JsonObject> Modules, List<Link> Links);

    internal static Facts LoadFacts(string? project)
    {
        try { var map = Load(project); return new Facts(map.Project, map.Facts, Links(map)); }
        catch (MapException e) { throw new MapLoadException(e.Message, e.Code, e.Hint); }
    }

    static int Data(Map map, string entity)
    {
        var writers = map.Flows.SelectMany(f => Each(f.Flow["calls"]).Where(c => S(c["type"]) == "entity" && Is(S(c["entity"]), entity) && S(c["op"]) != "get")
            .Select(c => (JsonNode)new JsonObject { ["flow"] = f.Id, ["op"] = S(c["op"]) })).ToArray();
        var readers = map.Flows.Where(f => Each(f.Flow["reads"]).Any(r => Strings(r["entities"]).Any(e => Is(e, entity)))
                || Each(f.Flow["calls"]).Any(c => S(c["type"]) == "entity" && Is(S(c["entity"]), entity) && S(c["op"]) == "get"))
            .Select(f => (JsonNode)JsonValue.Create(f.Id)!).ToArray();
        var launches = map.Facts.SelectMany(f => Each(f["processes"]).Where(p => Is(S(p["launchOn"]?["entity"]), entity))
            .Select(p => (JsonNode)new JsonObject { ["process"] = S(p["name"]), ["module"] = S(f["name"]), ["on"] = S(p["launchOn"]?["event"]) })).ToArray();
        var links = Links(map);
        var prefix = $"entity:{entity}.";
        var into = links.Where(l => l.To.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(l => (JsonNode)new JsonObject { ["attribute"] = l.To[prefix.Length..], ["from"] = l.From, ["via"] = l.Via }).ToArray();
        var outOf = links.Where(l => l.From.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(l => (JsonNode)new JsonObject { ["attribute"] = l.From[prefix.Length..], ["to"] = l.To, ["via"] = l.Via }).ToArray();
        var definedIn = map.Facts.Where(f => Each(f["entities"]).Any(e => Is(S(e["name"]), entity))).Select(f => S(f["name"])).ToArray();
        var body = new JsonObject
        {
            ["entity"] = entity, ["definedIn"] = new JsonArray(definedIn.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["writtenBy"] = new JsonArray(writers), ["readBy"] = new JsonArray(readers), ["launches"] = new JsonArray(launches),
            ["valuesFrom"] = new JsonArray(into), ["valuesTo"] = new JsonArray(outOf),
        };
        if (definedIn.Length == 0) body["hint"] = $"No entity named '{entity}' in the map.";
        return Print(map, body);
    }

    // ---- an API method and its chain ----

    static int Api(Map map, string text)
    {
        var (apiName, method) = Split(text);
        var defs = map.Facts.SelectMany(f => Each(f["restConsumed"]).Where(a => apiName is null || Is(S(a["name"]), apiName))
            .SelectMany(a => Each(a["methods"]).Where(m => Is(S(m["name"]), method)).Select(m => (module: S(f["name"]), api: S(a["name"]), method: m)))).ToList();
        if (defs.Count == 0) throw new MapException($"No consumed REST method named '{text}' in the map.", 1, "Use Api.Method, such as CrmApi.GetClient. List them: oml map overview");
        if (defs.Count > 1 && defs.Select(d => d.api).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            throw new MapException($"'{text}' is a method of several APIs: {string.Join(", ", defs.Select(d => d.api + "." + method).Distinct())}.", 2, "Write it as Api.Method.");
        var d = defs[0];
        var key = $"api:{d.api}.{S(d.method["name"])}";
        var links = Links(map);
        var callers = CallersOf(map, d.api, S(d.method["name"]));
        var body = new JsonObject
        {
            ["api"] = d.api, ["method"] = S(d.method["name"]), ["module"] = d.module, ["verb"] = S(d.method["verb"]), ["url"] = S(d.method["url"]),
            ["inputs"] = d.method["inputs"]?.DeepClone(), ["outputs"] = d.method["outputs"]?.DeepClone(),
            ["hasSample"] = d.method["sampleResponse"] is not null,
            ["calledBy"] = new JsonArray(callers.Select(c => (JsonNode)new JsonObject { ["flow"] = c.flow.Id, ["args"] = c.call["args"]?.DeepClone() }).ToArray()),
            ["inputsFrom"] = new JsonArray(links.Where(l => l.To.StartsWith(key + ".in:", StringComparison.OrdinalIgnoreCase))
                .Select(l => (JsonNode)new JsonObject { ["input"] = l.To[(key.Length + 4)..], ["from"] = l.From, ["via"] = l.Via }).ToArray()),
            ["outputsTo"] = new JsonArray(links.Where(l => l.From.StartsWith(key + ".out:", StringComparison.OrdinalIgnoreCase))
                .Select(l => (JsonNode)new JsonObject { ["output"] = l.From[(key.Length + 5)..], ["to"] = l.To, ["via"] = l.Via }).ToArray()),
            ["chain"] = Chain(map, links, key),
        };
        return Print(map, body);
    }

    // The APIs that run after this one, following its outputs into entities, the processes launched on those entities,
    // and the APIs whose inputs read those entity attributes. Each step names how it continues.
    static JsonArray Chain(Map map, List<Link> links, string start)
    {
        var steps = new JsonArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        var frontier = new Queue<string>([start]);
        while (frontier.TryDequeue(out var api) && steps.Count < 50)
        {
            foreach (var l in links.Where(l => l.From.StartsWith(api + ".out:", StringComparison.OrdinalIgnoreCase)))
            {
                if (l.To.StartsWith("api:"))
                {
                    var next = l.To[..l.To.IndexOf(".in:", StringComparison.Ordinal)];
                    steps.Add(new JsonObject { ["from"] = l.From, ["to"] = l.To, ["via"] = l.Via });
                    if (seen.Add(next)) frontier.Enqueue(next);
                    continue;
                }
                // Into an entity: the processes launched on it, and every API that reads that attribute.
                var entityAttr = l.To["entity:".Length..];
                var entity = entityAttr.Split('.')[0];
                var launches = map.Facts.SelectMany(f => Each(f["processes"]).Where(p => Is(S(p["launchOn"]?["entity"]), entity)).Select(p => S(p["name"]) + " (on " + S(p["launchOn"]?["event"]) + ")")).ToArray();
                steps.Add(new JsonObject { ["from"] = l.From, ["to"] = l.To, ["via"] = l.Via, ["launches"] = launches.Length == 0 ? null : new JsonArray(launches.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()) });
                foreach (var read in links.Where(r => Is(r.From, l.To) && r.To.StartsWith("api:")))
                {
                    var next = read.To[..read.To.IndexOf(".in:", StringComparison.Ordinal)];
                    steps.Add(new JsonObject { ["from"] = read.From, ["to"] = read.To, ["via"] = read.Via });
                    if (seen.Add(next)) frontier.Enqueue(next);
                }
            }
        }
        return steps;
    }
}
