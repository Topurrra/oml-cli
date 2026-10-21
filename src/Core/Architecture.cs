using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Applications;
using OutSystems.Model.Types;
using OutSystems.Model.UI;

namespace OmlCli;

// Only snapshots leave ReadModule: no model objects survive the per-file using scope.
static class Architecture
{
    internal sealed record Element(string kind, string name);
    internal sealed record Edge(string from, string to, bool strong, Element[] elements);
    internal sealed record Finding(string id, string severity, string title, string[] modules, string detail, string fix);
    internal sealed record Snapshot(JsonObject data, Element[] exports, int missingDescriptions);
    // skipped: inputs that could not be read, with the reason (also listed in warnings).
    internal sealed record Result(JsonObject document, List<string> warnings, List<(string input, string reason)> skipped);
    static readonly string[] Layers = ["EndUser", "Core", "Foundation"];
    // OutSystems docs: these kinds make a dependency strong (consumer must be republished). Screens, service actions, entities and structures are weak.
    static readonly string[] StrongKinds = ["ServerAction", "ClientAction", "Block", "Theme", "Script", "Image", "Resource", "Role", "Process"];
    internal static bool Strong(Element[] elements) => elements.Any(e => StrongKinds.Contains(e.kind));
    static string Text(JsonObject o, string key) => o[key]!.GetValue<string>();
    static int Count(JsonObject o, string key) => o["counts"]![key]!.GetValue<int>();
    static JsonObject Obj(object value) => JsonSerializer.SerializeToNode(value)!.AsObject();
    static string Name(object o) => Model.NameOf(o) ?? "";

    public static int Run(List<string> args)
    {
        var watch = Stopwatch.StartNew();
        var sources = new List<string>();
        var apps = new List<string>();
        var singles = new Dictionary<string, string>();
        for (var i = 1; i < args.Count; i++)
        {
            var option = args[i];
            if (option is not ("--in" or "--app" or "--layers" or "--out" or "--json" or "--team-label")
                || i + 1 >= args.Count || args[i + 1].StartsWith("--"))
                throw new CliError("architecture accepts repeated --in <folder|file.oml|file.oap>, --app <Name>, --layers <file.json>, --out <file.html>, --json <file.json>, and --team-label <Name>.", 2);
            var value = args[++i];
            if (option == "--in") sources.Add(value);
            else if (option == "--app") apps.Add(value);
            else if (!singles.TryAdd(option, value)) throw new CliError($"Duplicate option {option}.", 2);
        }
        if (sources.Count == 0) throw new CliError("architecture requires --in <folder|file.oml|file.oap>.", 2);
        var html = Path.GetFullPath(singles.GetValueOrDefault("--out", "architecture.html"));
        var jsonPath = singles.TryGetValue("--json", out var jp) ? Path.GetFullPath(jp) : null;
        if (string.Equals(html, jsonPath, StringComparison.OrdinalIgnoreCase)) throw new CliError("HTML and JSON outputs must have different paths.", 2);
        foreach (var path in new[] { html, jsonPath }.OfType<string>()) Fresh(path);
        try
        {
            var overrides = ReadOverrides(singles.GetValueOrDefault("--layers"));
            var result = Read(sources, apps, singles.GetValueOrDefault("--team-label"), overrides);
            var json = result.document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var page = ArchitecturePage.Render(json);
            using (var stream = new FileStream(html, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream)) writer.Write(page);
            if (jsonPath is not null)
            {
                using var stream = new FileStream(jsonPath, FileMode.CreateNew);
                using var writer = new StreamWriter(stream);
                writer.Write(json);
            }
            var findings = result.document["findings"]!.AsArray();
            return Cli.Emit(new {
                modules = result.document["modules"]!.AsArray().Count,
                applications = result.document["applications"]!.AsArray().Count,
                edges = result.document["edges"]!.AsArray().Count,
                findings = new { high = findings.Count(f => f!["severity"]!.GetValue<string>() == "high"),
                    medium = findings.Count(f => f!["severity"]!.GetValue<string>() == "medium"),
                    low = findings.Count(f => f!["severity"]!.GetValue<string>() == "low") },
                warnings = result.warnings, html, json = jsonPath, elapsedSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3)
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { throw new CliError(e.Message, 2); }
    }

    static void Fresh(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
            throw new CliError($"Output already exists: {path}. Use a new output path, or delete that file first.", 2);
    }

    internal static Dictionary<string, string> ReadOverrides(string? path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (path is null) return result;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new CliError("Layers must be a JSON object mapping module patterns to EndUser, Core, or Foundation.", 2);
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String || !Layers.Contains(p.Value.GetString()))
                throw new CliError($"Invalid layer for '{p.Name}'; use EndUser, Core, or Foundation.", 2);
            if (!result.TryAdd(p.Name, p.Value.GetString()!)) throw new CliError($"Duplicate layer pattern '{p.Name}'.", 2);
        }
        return result;
    }

    internal static (string layer, string reason) Layer(string name, string kind, int screens, int entities,
        int actions, IReadOnlyDictionary<string, string> overrides)
    {
        if (overrides.TryGetValue(name, out var exact)) return (exact, "override");
        // Exact names win; otherwise the most specific wildcard wins, then lexical order.
        foreach (var p in overrides.Where(p => p.Key.Contains('*')).OrderByDescending(p => p.Key.Count(c => c != '*')).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
            if (Regex.IsMatch(name, "^" + Regex.Escape(p.Key).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return (p.Value, "override");
        if (kind is "Library" or "Extension" || name.Equals("OutSystemsUI", StringComparison.OrdinalIgnoreCase) || name.Trim('(', ')').Equals("System", StringComparison.OrdinalIgnoreCase))
            return ("Foundation", "content: " + (kind is "Library" or "Extension" ? kind.ToLowerInvariant() : "platform module"));
        var suffixes = new[] { new[] { "_UI", "_Portal", "_App" }, new[] { "_CW", "_CS", "_BL", "_Core", "_API", "_IS", "_Sync" }, new[] { "_Lib", "_Th", "_Theme", "_Pat", "_Ext", "_Drv" } };
        for (var i = 0; i < suffixes.Length; i++)
            foreach (var suffix in suffixes[i])
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return (Layers[i], "suffix " + suffix);
        if (screens > 0) return ("EndUser", "content: has screens");
        if (entities > 0 || actions > 0) return ("Core", "content: has entities or business logic");
        return ("Foundation", "content: only blocks, themes, scripts or utilities");
    }

    // visit: called for each loaded .oml module while it is open (oml check reads its flows there).
    internal static Result Read(IReadOnlyList<string> sources, IReadOnlyList<string> apps, string? team,
        IReadOnlyDictionary<string, string> overrides, Action<IESpace, string>? visit = null)
    {
        var warnings = new List<string>();
        var skipped = new List<(string input, string reason)>();
        void Skip(string input, string reason) { warnings.Add($"Skipped {input}: {reason}"); skipped.Add((input, reason)); }
        var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            try
            {
                var full = Path.GetFullPath(source);
                if (Directory.Exists(full))
                {
                    foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories).Where(IsInput)) files.Add(file);
                }
                else if (File.Exists(full) && IsInput(full)) files.Add(full);
                else Skip(source, "expected an existing folder, .oml or .oap file.");
            }
            catch (Exception e) { Skip(source, e.Message); }
        }
        var temp = Path.Combine(Path.GetTempPath(), "oml-architecture-" + Guid.NewGuid().ToString("N"));
        var pending = files.Where(f => Path.GetExtension(f).Equals(".oml", StringComparison.OrdinalIgnoreCase)).Select(f => (path: f, app: "(no application)")).ToList();
        var membership = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var n = 0;
            foreach (var package in files.Where(f => Path.GetExtension(f).Equals(".oap", StringComparison.OrdinalIgnoreCase)))
            {
                var folder = Path.Combine(temp, (n++).ToString());
                try
                {
                    var members = Packages.Read(package, (applications, _) => applications.SelectMany(a => a.Modules.Select(m => (name: m.Name, app: a.Name))).ToArray());
                    Packages.Extract(package, folder);
                    foreach (var m in members)
                    {
                        if (membership.TryGetValue(m.name, out var old) && old != m.app)
                            warnings.Add($"Module {m.name} is listed in both {old} and {m.app}; using {old}.");
                        else membership[m.name] = m.app;
                    }
                    foreach (var file in Directory.EnumerateFiles(folder).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".oml" or ".xif")) pending.Add((file, ""));
                }
                catch (Exception e) { Skip(package, (e.InnerException ?? e).Message); }
            }
            var snapshots = new Dictionary<string, Snapshot>(StringComparer.OrdinalIgnoreCase);
            var allEdges = new List<Edge>();
            var producerKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Loose files win duplicate names; package membership still supplies their application.
            foreach (var file in pending)
            {
                try
                {
                    if (Path.GetExtension(file.path).Equals(".xif", StringComparison.OrdinalIgnoreCase))
                    {
                        using var extension = (OutSystems.Model.Extensions.IExtension)Reflect.Call(Model.Services(), "LoadExtension", File.ReadAllBytes(file.path))!;
                        var extensionName = ((IModule)extension).Name;
                        var extensionApp = membership.GetValueOrDefault(extensionName, "(no application)");
                        if (apps.Count > 0 && !apps.Contains(extensionApp, StringComparer.OrdinalIgnoreCase)) continue;
                        if (!snapshots.TryAdd(extensionName, ReadExtension(extension, extensionApp, overrides))) warnings.Add($"Skipped duplicate module {extensionName}: {file.path}");
                        continue;
                    }
                    using var module = Model.Load(file.path);
                    var name = Name(module);
                    if (snapshots.ContainsKey(name)) { warnings.Add($"Skipped duplicate module {name}: {file.path}"); continue; }
                    var app = membership.GetValueOrDefault(name, "(no application)");
                    if (apps.Count > 0 && !apps.Contains(app, StringComparer.OrdinalIgnoreCase)) continue;
                    var snapshot = ReadModule(module, app, overrides);
                    var edges = ReadEdges(module);
                    visit?.Invoke(module, app);
                    foreach (var reference in module.References)
                        producerKinds[reference.Name] = reference.Kind.ToString().Contains("Extension", StringComparison.OrdinalIgnoreCase) ? "Extension"
                            : reference.ModuleType.ToString().Contains("Library", StringComparison.OrdinalIgnoreCase) ? "Library" : "Unknown";
                    snapshots.Add(name, snapshot);
                    allEdges.AddRange(edges);
                }
                catch (Exception e) { Skip(file.path, (e.InnerException ?? e).Message); }
            }
            var modules = snapshots.ToDictionary(p => p.Key, p => p.Value.data, StringComparer.OrdinalIgnoreCase);
            foreach (var producer in allEdges.Select(e => e.to).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!modules.ContainsKey(producer))
                {
                    var layer = Layer(producer, producerKinds.GetValueOrDefault(producer, "Unknown"), 0, 0, 0, overrides);
                    modules[producer] = Obj(new { name = producer, application = "(external)", external = true, layer = layer.layer, layerReason = layer.reason });
                }
            var findings = Analyze(snapshots, modules, allEdges);
            var document = Obj(new {
                schema = 1, generatedAt = DateTimeOffset.UtcNow.ToString("O"), omlVersion = typeof(Cli).Assembly.GetName().Version?.ToString(3),
                input = new { sources, apps, team },
                applications = snapshots.Values.GroupBy(s => Text(s.data, "application"), StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase).Select(g => new { name = g.Key, modules = g.Select(s => Text(s.data, "name")).Order(StringComparer.OrdinalIgnoreCase).ToArray() }),
                modules = modules.Values.OrderBy(m => Text(m, "name"), StringComparer.OrdinalIgnoreCase).ToArray(),
                edges = allEdges.OrderBy(e => e.from, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.to, StringComparer.OrdinalIgnoreCase).ToArray(), findings
            });
            document["layout"] = ArchitectureLayout.Compute(document);
            return new(document, warnings, skipped);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    static bool IsInput(string path) => Path.GetExtension(path).ToLowerInvariant() is ".oml" or ".oap";

    static Snapshot ReadExtension(OutSystems.Model.Extensions.IExtension extension, string application, IReadOnlyDictionary<string, string> overrides)
    {
        var module = (IModule)extension;
        var layer = Layer(module.Name, "Extension", 0, 0, 0, overrides);
        var exports = extension.Entities.Select(e => new Element("Entity", e.Name))
            .Concat(extension.Actions.Select(a => new Element("ServerAction", a.Name)))
            .Concat(extension.Structures.Select(s => new Element("Structure", s.Name))).ToArray();
        var descriptions = extension.Actions.Select(a => a.Description).Concat(extension.Entities.Select(e => Reflect.Get(e, "Description") as string))
            .Concat(extension.Structures.Select(s => Reflect.Get(s, "Description") as string));
        return new(Obj(new {
            name = module.Name, application, external = false, kind = "Extension", layer = layer.layer, layerReason = layer.reason, description = module.Description ?? "",
            counts = new { entities = extension.Entities.Count(), staticEntities = 0, structures = extension.Structures.Count(), serverActions = extension.Actions.Count(), serviceActions = 0,
                clientActions = 0, screens = 0, blocks = 0, restExposed = 0, restConsumed = 0, timers = 0, processes = 0, siteProperties = 0, roles = 0 },
            @public = new { entities = extension.Entities.Select(e => e.Name).ToArray(), actions = extension.Actions.Select(a => a.Name).ToArray(), blocks = Array.Empty<string>(),
                structures = extension.Structures.Select(s => s.Name).ToArray(), staticEntities = Array.Empty<string>() },
            entities = extension.Entities.Select(e => new { name = e.Name, @public = true, attributes = e.Attributes.Count(), references = Array.Empty<string>() }).ToArray(),
            screens = Array.Empty<string>(), restExposed = Array.Empty<object>(), restConsumed = Array.Empty<object>(), timers = Array.Empty<string>(), processes = Array.Empty<string>()
        }), exports, descriptions.Count(string.IsNullOrWhiteSpace));
    }

    internal static Snapshot ReadModule(IESpace m, string application, IReadOnlyDictionary<string, string> overrides)
    {
        var name = Name(m);
        var rawKind = m.Kind.ToString();
        var kind = rawKind.Contains("Library", StringComparison.OrdinalIgnoreCase) ? "Library" : rawKind switch {
            "CrossDevice" => "Reactive", "Mobile" => "Mobile", "WebResponsive" or "Web" => "Traditional Web", "Service" => "Service", _ => "Unknown"
        };
        var nodes = m.MobileFlows.SelectMany(f => f.Nodes).Cast<IObjectSignature>().Concat(m.WebFlows.SelectMany(f => f.Nodes)).Distinct().ToArray();
        var screens = nodes.Where(s => s is IScreenSignature && !Inspect.IsBlock(s)).ToArray();
        var blocks = nodes.Where(Inspect.IsBlock).ToArray();
        bool Static(object e) => Reflect.Get(e, "EntityKind")?.ToString() == "Static";
        var entities = m.Entities.Where(e => !Static(e)).ToArray();
        var statics = m.Entities.Where(e => Static(e)).ToArray();
        var exports = ModuleReferences.Elements(m).Where(e => Reflect.Get(e, "Public") is true).Distinct().ToArray();
        var publicElements = exports.Select(e => new Element(ElementKind(e), Name(e))).ToArray();
        string[] Public(params string[] kinds) => publicElements.Where(e => kinds.Contains(e.kind)).Select(e => e.name).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var restExposed = RestModel.Integrations(m, true).Where(i => ReferenceEquals(((IObjectSignature)i).GetESpace(), m)).ToArray();
        var restConsumed = RestModel.Integrations(m, false).Where(i => ReferenceEquals(((IObjectSignature)i).GetESpace(), m)).ToArray();
        var businessLogic = m.ServiceActions.Count() + m.Timers.Count() + m.Processes.Count() + restExposed.Length + restConsumed.Length
            + m.ServerActions.Count(a => ModuleReferences.Descendants(a).Any(n => n.GetType().GetInterfaces().Any(i =>
                i.Name is "IAggregateNode" or "IExecuteServerActionNode" || i.Name.Equals("ISqlNode", StringComparison.OrdinalIgnoreCase))));
        var layer = Layer(name, kind, screens.Length, entities.Length, businessLogic, overrides);
        var data = Obj(new {
            name, application, external = false, kind, layer = layer.layer, layerReason = layer.reason,
            description = Reflect.Get(m, "Description") as string ?? "",
            counts = new { entities = entities.Length, staticEntities = statics.Length, structures = m.Structures.Count(),
                serverActions = m.ServerActions.Count(), serviceActions = m.ServiceActions.Count(), clientActions = m.ClientActions.Count(),
                screens = screens.Length, blocks = blocks.Length, restExposed = restExposed.Length, restConsumed = restConsumed.Length,
                timers = m.Timers.Count(), processes = m.Processes.Count(), siteProperties = m.SiteProperties.Count(), roles = m.Roles.Count() },
            @public = new { entities = Public("Entity"), actions = Public("ServerAction", "ServiceAction", "ClientAction"), blocks = Public("Block"), structures = Public("Structure"), staticEntities = Public("StaticEntity") },
            entities = m.Entities.Select(e => new { name = e.Name, @public = e.Public, attributes = e.Attributes.Count(),
                references = e.Attributes.Where(a => !ReferenceEquals(a, e.IdentifierAttribute) && a.DataType is IIdentifierType id && id.Entity is not null)
                    .Select(a => ((IIdentifierType)a.DataType).Entity!.Name).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToArray() }).ToArray(),
            screens = screens.Select(Name).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            restExposed = restExposed.Select(i => new { name = Name(i), methods = RestModel.Actions(i).Count() }).ToArray(),
            restConsumed = restConsumed.Select(i => new { name = Name(i), methods = RestModel.Actions(i).Count(), baseUrl = Reflect.Get(i, "BaseURL") as string ?? "" }).ToArray(),
            timers = m.Timers.Select(Name).ToArray(), processes = m.Processes.Select(Name).ToArray()
        });
        return new(data, publicElements, exports.Count(e => string.IsNullOrWhiteSpace(Reflect.Get(e, "Description") as string)));
    }

    internal static string ElementKind(IObjectSignature e)
    {
        if (Inspect.IsBlock(e)) return "Block";
        if (e is OutSystems.Model.UI.IScreenSignature) return "Screen";
        var interfaces = e.GetType().GetInterfaces().Select(i => i.Name).ToHashSet();
        bool Has(string n) => interfaces.Contains("I" + n) || interfaces.Contains("I" + n + "Signature");
        if (Has("Entity")) return Reflect.Get(e, "EntityKind")?.ToString() == "Static" ? "StaticEntity" : "Entity";
        foreach (var kind in new[] { "ServiceAction", "ServerAction", "ClientAction", "Structure", "Theme", "Script", "Image", "Resource", "Role", "Process" })
            if (Has(kind) || interfaces.Any(i => i == "IMobile" + kind + "Signature" || i == "IWeb" + kind + "Signature")) return kind;
        return "Other";
    }

    internal static Edge[] ReadEdges(IESpace m)
    {
        // Use the shared name lookup plus the wider module inventory (themes/resources).
        // Generated entity/role helpers from Refs are not separate imported contracts.
        var refs = Refs.All(m).Where(r => r.owner != Name(m)).ToLookup(r => r.owner, r => r.element, StringComparer.OrdinalIgnoreCase);
        return m.References.Select(r => {
            // Processes are read here only; apply's reference lookup does not list them.
            var topLevel = ModuleReferences.Elements(r).Concat(r.Processes).ToArray();
            var keys = topLevel.Select(e => e.GlobalKey).ToHashSet();
            var elements = topLevel.Concat(refs[r.Name].OfType<IObjectSignature>().Where(e => keys.Contains(e.GlobalKey))).Distinct()
                .Select(e => new Element(ElementKind(e), Name(e))).Distinct().OrderBy(e => e.kind).ThenBy(e => e.name, StringComparer.OrdinalIgnoreCase).ToArray();
            return new Edge(Name(m), r.Name, Strong(elements), elements);
        }).ToArray();
    }

    internal static List<Finding> Analyze(Dictionary<string, Snapshot> snapshots, Dictionary<string, JsonObject> modules, List<Edge> edges)
    {
        var result = new List<Finding>();
        void Add(string id, string severity, string title, string[] names, string detail, string fix) => result.Add(new(id, severity, title, names, detail, fix));
        int Rank(string name) => Array.IndexOf(Layers, Text(modules[name], "layer"));
        foreach (var e in edges)
        {
            if (Rank(e.from) > Rank(e.to)) Add("upward-reference", "high", "A module references a higher layer", [e.from, e.to],
                $"{e.from} ({Text(modules[e.from], "layer")}) references {e.to} ({Text(modules[e.to], "layer")}). Higher layers should depend on lower layers so lower layers stay reusable.", "Move the shared contract and implementation to a lower layer, or remove this dependency.");
            // Screen destinations are weak links between user modules (OutSystems docs); any other element is a side reference.
            if (Rank(e.from) == 0 && Rank(e.to) == 0 && e.elements.Any(x => x.kind != "Screen")) Add("side-reference-enduser", "high", "EndUser modules reference each other", [e.from, e.to],
                $"{e.from} uses elements from {e.to}. End-user modules should not provide reusable elements; this couples the user applications.", "Move the shared elements into a Core or Foundation module.");
        }
        foreach (var s in snapshots.Values)
        {
            var m = s.data;
            var name = Text(m, "name");
            if (Text(m, "layer") == "Core" && Count(m, "screens") > 0) Add("core-ui", "medium", "A Core module contains screens", [name], "Core screens couple reusable business logic to a user interface.", "Move user screens into an EndUser module.");
            if (Text(m, "layer") == "Foundation" && Count(m, "entities") > 0) Add("foundation-business", "medium", "A Foundation module owns business entities", [name], "Non-static entities in Foundation can tie technical utilities to business data.", "Move business entities into a Core module.");
            var size = new[] { "entities", "staticEntities", "serverActions", "serviceActions", "clientActions", "screens" }.Sum(k => Count(m, k));
            if (size > 150) Add("large-module", "low", "A module has more than 150 actions, screens and entities", [name], $"{name} has {size} actions, screens and entities, which can make maintenance harder.", "Split independent responsibilities into smaller modules.");
            if (s.missingDescriptions > 0) Add("missing-description", "low", "Public elements have no description", [name], $"{s.missingDescriptions} public elements in {name} have no description. Consumers lack guidance about their contract.", "Describe the purpose and expected use of public elements.");
            if (snapshots.Count >= 2)
            {
                var used = edges.Where(e => e.to.Equals(name, StringComparison.OrdinalIgnoreCase) && snapshots.ContainsKey(e.from) && !e.from.Equals(name, StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.elements).ToHashSet();
                var unused = s.exports.Where(e => e.kind is "Entity" or "StaticEntity" or "ServerAction" or "ServiceAction" or "ClientAction" or "Block").Where(e => !used.Contains(e)).ToArray();
                if (unused.Length > 0) Add("unused-public", "low", "Public elements have no input consumer", [name],
                    $"No other input module imports these elements: {string.Join(", ", unused.Select(e => e.name))}. Consumers outside the input may still use them.", "Check other applications before making unused contracts private.");
            }
        }
        // Tarjan SCC: one finding per cyclic component, with an actual closed path in the detail.
        var adjacency = modules.Keys.ToDictionary(k => k, k => edges.Where(e => e.from.Equals(k, StringComparison.OrdinalIgnoreCase)).Select(e => e.to).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var low = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>();
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(string v)
        {
            index[v] = low[v] = index.Count; stack.Push(v); active.Add(v);
            foreach (var w in adjacency[v])
                if (!index.ContainsKey(w)) { Visit(w); low[v] = Math.Min(low[v], low[w]); }
                else if (active.Contains(w)) low[v] = Math.Min(low[v], index[w]);
            if (low[v] != index[v]) return;
            var component = new List<string>();
            string w2;
            do { w2 = stack.Pop(); active.Remove(w2); component.Add(w2); } while (!w2.Equals(v, StringComparison.OrdinalIgnoreCase));
            if (component.Count == 1 && !adjacency[v].Contains(v, StringComparer.OrdinalIgnoreCase)) return;
            component.Sort(StringComparer.OrdinalIgnoreCase);
            var allowed = component.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var path = new List<string>();
            var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool Cycle(string current)
            {
                path.Add(current); onPath.Add(current);
                foreach (var next in adjacency[current].Where(allowed.Contains))
                {
                    if (onPath.Contains(next)) { path.RemoveRange(0, path.FindIndex(p => p.Equals(next, StringComparison.OrdinalIgnoreCase))); path.Add(next); return true; }
                    if (Cycle(next)) return true;
                }
                onPath.Remove(current); path.RemoveAt(path.Count - 1); return false;
            }
            Cycle(component[0]);
            Add("cycle", "high", "Modules form a dependency cycle", component.ToArray(), "Dependency cycle: " + string.Join(" → ", path) + ". These modules cannot evolve independently.", "Move shared contracts to a lower module and remove one direction of the cycle.");
        }
        foreach (var name in modules.Keys.Order(StringComparer.OrdinalIgnoreCase)) if (!index.ContainsKey(name)) Visit(name);
        return result.OrderBy(f => f.severity == "high" ? 0 : f.severity == "medium" ? 1 : 2).ThenBy(f => f.id).ThenBy(f => f.modules[0], StringComparer.OrdinalIgnoreCase).ToList();
    }
}
