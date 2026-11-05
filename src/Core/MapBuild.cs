using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OutSystems.Model;
using OutSystems.Model.Data;
using OutSystems.Model.Logic;
using OutSystems.Model.Logic.Nodes;
using OutSystems.Model.Processes;
using OutSystems.Model.Types;

namespace OmlCli;

// `oml map build <project> [--in <folder|file.oap|file.oml>]... [--app <Name>]...`: an application map of every module of
// a project, for agents to query without loading modules (`oml map overview|uses|impact|data|api`, in the launcher).
// The map holds what Service Studio itself sees: references, the public elements, every flow's calls (actions, REST
// methods, entity actions, process launches), aggregate sources, assignments and argument expressions (the lineage of
// an API's output into an entity attribute and into the next API's input), processes (launch on an entity event, the
// launch action, the activities), timers, REST methods with their sample request and response, structures and entities.
// One file per input file, so a rebuild reads only the inputs whose bytes changed. Read only: no module is saved.
static class MapBuild
{
    // 2: a file with a module that could not be read is marked partial and read again (1.16.0 kept such files as complete).
    const string Schema = "2";
    static readonly Regex ProjectName = new("^[a-z0-9][a-z0-9-]{0,39}$", RegexOptions.CultureInvariant);

    internal static string Root => Environment.GetEnvironmentVariable("OML_MAPS") is { Length: > 0 } root
        ? Path.GetFullPath(root)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "oml-cli", "maps");

    public static int Run(List<string> args)
    {
        // args: map build <project> [--in x]... [--app y]...
        if (args.Count < 3 || args[2].StartsWith("--"))
            throw new CliError("Usage: oml map build <project> [--in <folder|file.oap|file.oml>]... [--app <Name>]...", 2);
        var project = args[2].ToLowerInvariant();
        if (!ProjectName.IsMatch(project)) throw new CliError($"Project name '{args[2]}': use 1-40 lower-case letters, digits and hyphens, such as falcon.", 2);
        var sources = new List<string>();
        var apps = new List<string>();
        for (var i = 3; i < args.Count; i++)
        {
            if (args[i] is not ("--in" or "--app") || i + 1 >= args.Count) throw new CliError($"map build accepts --in <folder|file> and --app <Name>; got {args[i]}.", 2);
            (args[i] == "--in" ? sources : apps).Add(args[++i]);
        }
        var folder = Path.Combine(Root, project);
        var projectFile = Path.Combine(folder, "project.json");
        var previous = File.Exists(projectFile) ? JsonNode.Parse(File.ReadAllText(projectFile)) as JsonObject : null;
        if (sources.Count == 0)
        {
            sources = (previous?["inputs"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
            if (sources.Count == 0) throw new CliError($"The map '{project}' has no inputs yet. Give --in <folder of the project's .oap files> (download them with oml lifetime download).", 2);
            if (apps.Count == 0) apps = (previous?["apps"] as JsonArray)?.Select(x => x!.GetValue<string>()).ToList() ?? [];
        }
        sources = sources.Select(Path.GetFullPath).ToList();
        foreach (var s in sources) if (!Directory.Exists(s) && !File.Exists(s)) throw new CliError($"Not found: {s}", 2);

        var files = sources.SelectMany(s => Directory.Exists(s)
                ? Directory.EnumerateFiles(s, "*", SearchOption.AllDirectories).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".oap" or ".oml")
                : [s])
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) throw new CliError("No .oap or .oml file in the inputs.", 2);

        var store = Path.Combine(folder, "sources");
        Directory.CreateDirectory(store);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var (read, reused, warnings) = (new List<string>(), new List<string>(), new List<string>());
        var failed = new List<(string file, string reason)>();
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var appFilter = string.Join("|", apps.Order(StringComparer.OrdinalIgnoreCase));
        foreach (var file in files)
        {
            var key = Key(file);
            keep.Add(key + ".json");
            var target = Path.Combine(store, key + ".json");
            var hash = Sha(file);
            if (File.Exists(target) && JsonNode.Parse(File.ReadAllText(target)) is JsonObject old
                && old["sha256"]?.GetValue<string>() == hash && old["schema"]?.GetValue<string>() == Schema && old["apps"]?.GetValue<string>() == appFilter
                && old["partial"] is null)
            {
                reused.Add(file);
                continue;
            }
            var facts = new JsonArray();
            var result = Architecture.Read([file], apps, null, new Dictionary<string, string>(), visit: (m, app) => facts.Add(Module(m, app)));
            warnings.AddRange(result.warnings);
            // A file no module could be read from stays out of the map (and out of keep), so the next build tries it again.
            if (facts.Count == 0 && result.skipped.Count > 0) { keep.Remove(key + ".json"); failed.Add((file, result.skipped[0].reason)); continue; }
            // Some modules read, some not: the map keeps what was read, the file is marked partial and read again next time.
            foreach (var (input, reason) in result.skipped) failed.Add((input == file ? file : file + "!" + Path.GetFileName(input), reason));
            var doc = new JsonObject
            {
                ["schema"] = Schema, ["file"] = file, ["sha256"] = hash, ["size"] = new FileInfo(file).Length, ["mtime"] = File.GetLastWriteTimeUtc(file).Ticks, ["apps"] = appFilter, ["readAt"] = DateTime.UtcNow.ToString("O"),
                ["modules"] = result.document["modules"]!.DeepClone(), ["edges"] = result.document["edges"]!.DeepClone(),
                ["applications"] = result.document["applications"]!.DeepClone(), ["facts"] = facts, ["partial"] = result.skipped.Count > 0 ? true : null,
            };
            File.WriteAllText(target, doc.ToJsonString());
            read.Add(file);
        }
        if (read.Count + reused.Count == 0)
            throw new CliError("No module could be read: " + string.Join("; ", failed.Select(f => $"{Path.GetFileName(f.file)}: {f.reason}")), 1);
        // An input that is gone (or no longer in the inputs) leaves the map.
        var removed = Directory.EnumerateFiles(store, "*.json").Where(f => !keep.Contains(Path.GetFileName(f))).ToList();
        foreach (var f in removed) File.Delete(f);
        var info = new JsonObject
        {
            ["project"] = project, ["inputs"] = new JsonArray(sources.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
            ["apps"] = new JsonArray(apps.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["builtAt"] = DateTime.UtcNow.ToString("O"), ["files"] = files.Count,
        };
        File.WriteAllText(projectFile, info.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        // The current folder remembers its project, so later map commands here need no --project.
        try
        {
            Directory.CreateDirectory(Path.Combine(Environment.CurrentDirectory, ".oml"));
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".oml", "map.json"), new JsonObject { ["project"] = project }.ToJsonString() + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { warnings.Add("Could not write .oml/map.json in this folder: give --project <name> to later map commands."); }
        return Cli.Emit(new
        {
            project, map = folder, files = files.Count, read = read.Count, reused = reused.Count, removed = removed.Count,
            failed = failed.Select(f => new { file = f.file, reason = f.reason }).ToArray(),
            warnings, seconds = Math.Round(watch.Elapsed.TotalSeconds, 1),
            next = $"oml map overview --project {project}",
        });
    }

    static string Key(string file) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(file.ToLowerInvariant()))).ToLowerInvariant()[..16];

    static string Sha(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    // ---- one module ----

    static string Name(object? o) => o is null ? "" : Model.NameOf(o) ?? "";

    // The module that owns an element: this module, or the reference it comes from.
    static string Owner(object element)
    {
        for (var o = element as IObjectSignature; o is not null; o = o.Parent)
            if (o is IESpace || o is IReference) return Name(o);
        return "";
    }

    internal static JsonObject Module(IESpace m, string app)
    {
        var name = Name(m);
        var restMethods = new Dictionary<object, (string api, string method)>(ReferenceEqualityComparer.Instance);
        var consumed = new JsonArray();
        foreach (var integration in RestModel.Integrations(m, false))
        {
            var methods = new JsonArray();
            foreach (var a in RestModel.Actions(integration))
            {
                restMethods[a] = (Name(integration), Name(a));
                methods.Add(new JsonObject
                {
                    ["name"] = Name(a), ["verb"] = Reflect.Get(a, "HTTPMethod")?.ToString(), ["url"] = Reflect.Get(a, "URLPath") as string,
                    ["inputs"] = Params(m, Reflect.Get(a, "InputParameters"), "InputPlacement", Reflect.Get(a, "URLPath") as string),
                    ["outputs"] = Params(m, Reflect.Get(a, "OutputParameters"), "OutputPlacement", null),
                    ["sampleRequest"] = Clip(Reflect.Get(a, "SampleRequest") as string), ["sampleResponse"] = Clip(Reflect.Get(a, "SampleResponse") as string),
                });
            }
            // The structures the API brought with it (CrmApiCustomerDto): what its requests and responses hold.
            var dtos = new JsonArray(((Reflect.Get(integration, "Structures") as System.Collections.IEnumerable)?.Cast<object>() ?? []).OfType<IStructureSignature>()
                .Select(d => (JsonNode)new JsonObject { ["name"] = Name(d), ["attributes"] = Attributes(m, (Reflect.Get(d, "Attributes") as System.Collections.IEnumerable)?.Cast<object>().OfType<IAttribute>() ?? [], null) }).ToArray());
            consumed.Add(new JsonObject { ["name"] = Name(integration), ["baseUrl"] = Reflect.Get(integration, "BaseURL") as string, ["methods"] = methods, ["structures"] = dtos });
        }
        var exposed = new JsonArray(RestModel.Integrations(m, true).Select(i => (JsonNode)new JsonObject
        {
            ["name"] = Name(i),
            ["methods"] = new JsonArray(RestModel.Actions(i).Select(a => (JsonNode)new JsonObject
            {
                ["name"] = Name(a), ["verb"] = Reflect.Get(a, "HTTPMethod")?.ToString(), ["url"] = Reflect.Get(a, "URLPath") as string,
            }).ToArray()),
        }).ToArray());

        var flows = new JsonArray();
        foreach (var (owner, kind) in Flows(m))
            if (Flow(m, owner, kind, restMethods) is { } f) flows.Add(f);

        var processes = new JsonArray();
        foreach (IProcess p in m.Processes)
        {
            var trigger = p.LaunchOn;
            processes.Add(new JsonObject
            {
                ["name"] = Name(p),
                ["launchOn"] = trigger?.Entity is { } e && trigger.Action != EntityActionKind.None ? new JsonObject { ["entity"] = Name(e), ["event"] = trigger.Action.ToString() } : null,
                ["launchAction"] = Name(Reflect.Get(p, "LaunchProcessAction")),
                ["activities"] = new JsonArray(p.Nodes.Select(n => (JsonNode)new JsonObject
                {
                    ["name"] = Name(n), ["kind"] = ElementPaths.Kind(n),
                    ["startsOn"] = Reflect.Get(n, "StartOn") is ITrigger t && t.Entity is { } te ? Name(te) + " " + t.Action : null,
                    ["process"] = n is OutSystems.Model.Processes.Nodes.IExecuteProcessNode x ? Name(x.Process) : null,
                }).ToArray()),
            });
        }
        var timers = new JsonArray(m.Timers.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = Name(t), ["action"] = Name(Reflect.Get(t, "Action")), ["schedule"] = Reflect.Get(t, "Schedule") as string,
        }).ToArray());

        return new JsonObject
        {
            ["name"] = name, ["application"] = app,
            ["entities"] = new JsonArray(m.Entities.Select(e => (JsonNode)new JsonObject
            {
                ["name"] = e.Name, ["public"] = e.Public, ["static"] = Reflect.Get(e, "EntityKind")?.ToString() == "Static",
                ["attributes"] = Attributes(m, e.Attributes.Cast<IAttribute>(), e.IdentifierAttribute),
            }).ToArray()),
            ["structures"] = new JsonArray(m.Structures.Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["public"] = Reflect.Get(s, "Public") is true,
                ["attributes"] = Attributes(m, s.Attributes.Cast<IAttribute>(), null),
            }).ToArray()),
            ["restConsumed"] = consumed, ["restExposed"] = exposed,
            ["processes"] = processes, ["timers"] = timers, ["flows"] = flows,
        };
    }

    static string? Clip(string? s) => s is null || s.Length == 0 ? null : s.Length <= 20000 ? s : s[..20000];

    static JsonArray Attributes(IESpace m, IEnumerable<IAttribute> attributes, object? id) => new(attributes.Select(a =>
    {
        var o = new JsonObject { ["name"] = a.Name, ["type"] = Model.Describe(m, a.DataType) };
        if (a.Length is { } length) o["length"] = length;
        if (a.IsMandatory) o["mandatory"] = true;
        if (ReferenceEquals(a, id)) o["identifier"] = true;
        // The name a REST structure uses on the wire ("Name in JSON"), when the build exposes it.
        foreach (var p in a.GetType().GetProperties().Where(p => p.PropertyType == typeof(string) && p.Name.Contains("Json", StringComparison.OrdinalIgnoreCase)))
            if (p.GetValue(a) is string wire && wire.Length > 0 && wire != a.Name) { o["json"] = wire; break; }
        return (JsonNode)o;
    }).ToArray());

    // in: path, query, body or header. A consumed method keeps path and query inputs in its URL (/credits?CustomerId={CustomerId}).
    static JsonArray Params(IESpace m, object? list, string placement, string? url) => new((list as System.Collections.IEnumerable)?.Cast<object>().Select(p =>
    {
        var o = new JsonObject { ["name"] = Name(p), ["type"] = Model.Describe(m, Reflect.Get(p, "DataType") as ITypeSignature) };
        if (Reflect.Get(p, placement)?.ToString()?.Replace("Placement", "").ToLowerInvariant() is { } where)
        {
            var wireName = Reflect.Get(p, "OriginalName") as string is { Length: > 0 } w ? w : Name(p);
            o["in"] = where != "url" ? where : url is not null && url.IndexOf("{" + wireName + "}", StringComparison.Ordinal) is var at and >= 0
                && (url.IndexOf('?') < 0 || at < url.IndexOf('?')) ? "path" : "query";
        }
        if (Reflect.Get(p, "OriginalName") is string wire && wire.Length > 0 && wire != Name(p)) o["wire"] = wire;
        if (Reflect.Get(p, "IsMandatory") is true) o["mandatory"] = true;
        return (JsonNode)o;
    }).ToArray() ?? []);

    // Every flow of the module: actions, screen actions and preparations, process activities (automatic activities, and
    // the callbacks of processes and human activities). A flow is an owner with a Start node among its children.
    static IEnumerable<(IObjectSignature owner, string kind)> Flows(IESpace m)
    {
        var seen = new HashSet<IObjectSignature>();
        var pending = new Stack<IObjectSignature>(((IObjectSignature)m).Children);
        while (pending.TryPop(out var o))
        {
            if (o is IReference || !seen.Add(o)) continue;
            if (ElementPaths.Children(o).Any(c => c is IStartNode)) yield return (o, Architecture.ElementKind(o) is var k && k != "Other" ? k : ElementPaths.Kind(o));
            foreach (var c in o.Children) pending.Push(c);
        }
    }

    // A Reactive screen action has no outputs, and the model throws NotImplementedException when asked for them.
    static object? Parameters(object owner, string property)
    {
        try { return Reflect.Get(owner, property); }
        catch (Exception e) when ((e as System.Reflection.TargetInvocationException)?.InnerException is NotImplementedException || e is NotImplementedException) { return null; }
    }

    static string Text(object? expression) => expression is IExpression e ? (e.Text ?? "").Trim() : "";

    static JsonObject? Flow(IESpace m, IObjectSignature owner, string kind, Dictionary<object, (string api, string method)> rest)
    {
        var calls = new JsonArray();
        var reads = new JsonArray();
        var assigns = new JsonArray();
        foreach (var node in ElementPaths.Children(owner).Where(c => c is IFlowNodeSignature))
        {
            try
            {
                if (node is IAssignNode assign)
                {
                    foreach (var a in assign.Assignments) assigns.Add(new JsonObject { ["to"] = Text(a.Variable), ["value"] = Text(a.Value) });
                    continue;
                }
                if (Reflect.Property(node, "AsDatabaseAggregate") is not null && Reflect.Get(node, "AsDatabaseAggregate") is { } db)
                {
                    var sources = (Reflect.Get(db, "Sources") as System.Collections.IEnumerable)?.Cast<object>().Select(s => Name(Reflect.Get(s, "Source"))).Where(s => s.Length > 0).Distinct().ToArray() ?? [];
                    reads.Add(new JsonObject { ["node"] = Name(node), ["entities"] = new JsonArray(sources.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()) });
                    continue;
                }
                if (node is IExecuteServerActionNode or IExecuteClientActionNode && Reflect.Get(node, "Action") is { } action)
                {
                    var call = new JsonObject { ["node"] = Name(node), ["target"] = Name(action), ["module"] = Owner(action) };
                    if (rest.TryGetValue(action, out var r)) { call["type"] = "rest"; call["api"] = r.api; call["target"] = r.method; call["module"] = Name(m); }
                    else if (action is IObjectSignature { Parent: IEntitySignature entity })
                    {
                        call["type"] = "entity"; call["entity"] = Name(entity);
                        var actionName = Name(action);
                        call["op"] = actionName.StartsWith("CreateOrUpdate") ? "createOrUpdate" : actionName.StartsWith("Create") ? "create"
                            : actionName.StartsWith("Update") ? "update" : actionName.StartsWith("Delete") ? "delete" : "get";
                    }
                    else if (action is IObjectSignature { Parent: IProcessSignature process }) { call["type"] = "launch"; call["process"] = Name(process); }
                    else call["type"] = "action";
                    var args = new JsonObject();
                    foreach (var arg in (Reflect.Get(node, "Arguments") as System.Collections.IEnumerable)?.Cast<object>() ?? [])
                    {
                        string parameter;
                        try { parameter = Name(Reflect.Get(arg, "Parameter")); }
                        catch (Exception e) when (e is System.Reflection.TargetInvocationException or NullReferenceException) { continue; } // a broken reference
                        var value = Text(Reflect.Get(arg, "Value"));
                        if (parameter.Length > 0 && value.Length > 0) args[parameter] = value;
                    }
                    if (args.Count > 0) call["args"] = args;
                    calls.Add(call);
                }
            }
            catch (Exception e) when (e is System.Reflection.TargetInvocationException or NullReferenceException or InvalidCastException or InvalidOperationException or NotImplementedException) { }
        }
        if (calls.Count == 0 && reads.Count == 0 && assigns.Count == 0) return null;
        // Its parameters, so a wrapper action (an API call behind a server action in another module) passes the lineage on.
        static JsonArray Names(object? list) => new(((list as System.Collections.IEnumerable)?.Cast<object>() ?? []).Select(p => Name(p)).Where(n => n.Length > 0).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
        return new JsonObject
        {
            ["path"] = CheckRules.PathOf(owner), ["kind"] = kind, ["public"] = Reflect.Get(owner, "Public") is true ? true : null,
            ["inputs"] = Names(Parameters(owner, "InputParameters")), ["outputs"] = Names(Parameters(owner, "OutputParameters")),
            ["calls"] = calls, ["reads"] = reads, ["assigns"] = assigns,
        };
    }
}
