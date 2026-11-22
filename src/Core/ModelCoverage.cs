using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json.Nodes;

namespace OmlCli;

// `oml model-coverage [--out <folder>]` (hidden, for maintainers): how much of the installed Model API a spec can change.
// Every public Model API interface reachable from IESpace (through property types, method return types, generic
// arguments, base interfaces and the interfaces that extend a reachable one) is listed with each of its members, and
// each member is classified:
//   covered     a spec key changes it: oml's apply code calls it (found in oml's own IL, starting from Apply), or the
//               generic `set` of edits and widget/node properties reaches it (Reflect.Plan: writable properties of a
//               type it converts, Set*(string) and Set*(bool) methods, on any element a path reaches).
//   rule        nothing in a spec changes it, and a production rule (skill/rules.md) forbids the change.
//   read-only   a getter-only property or a query method (Get*, Is*, Find*, ...).
//   not-covered a member that changes the model and no spec key reaches; `reason` says what is missing.
// A covered member may still be refused on existing elements: `existingRule` names that rule. The rules come from the
// same tables ProductionRules enforces. Nothing here is written by hand per member, so a new Service Studio build
// gives a new file that can be diffed with the old one.
static class ModelCoverage
{
    static readonly string[] Assemblies =
    {
        "OutSystems.Model.V1", "ServiceStudio.PluginAPI", "ServiceStudio.Plugin.NRWidgets", "ServiceStudio.Plugin.Widgets",
        "ServiceStudio.Plugin.REST", "ServiceStudio.Plugin.RESTService", "ServiceStudio.Plugin.SOAP",
    };

    // Query verbs: a method whose name starts with one of these reads the model.
    static readonly string[] ReadVerbs =
    {
        "Get", "Find", "Is", "Has", "Can", "Contains", "Try", "Equals", "ToString", "Lookup", "Resolve", "Validate", "Count",
        "Exists", "Describe", "Compare", "Matches", "Evaluate", "Calculate", "Compute", "Serialize", "Export", "Format",
        "Search", "Query", "List", "Enumerate", "Inspect", "Check", "Should", "Needs", "Allows", "Supports", "As", "To",
        "Accept", "Visit", "Dispose", "Load", "Read", "Clone", "Parse", "Build", "Enter", "Begin", "End", "Using",
    };

    // oml's source files (src/Core) and the spec keys their code serves. The file of each method comes from
    // OmlCli.Core.pdb; without it the outermost class name stands in. A file not listed shows as "apply (<file>)".
    static readonly (string file, string keys)[] FileKeys =
    {
        ("Apply", "entities, structures"), ("EntityOptions", "entities"), ("StaticEntities", "staticEntities"),
        ("Builder", "actions and flows"), ("ListLambdas", "flow nodes (list actions)"), ("Queries", "aggregate"),
        ("Sql", "sql"), ("SqlSelect", "sql"), ("Rest", "restClients, restServices"), ("RestImport", "restClients"),
        ("OpenApi", "restServices"), ("Emails", "emails, sendEmail"), ("ExternalSites", "externalSites"),
        ("Edits", "edits"), ("FieldMappings", "edits"), ("WebUi", "screens, blocks [Traditional Web]"),
        ("UiFlows", "uiFlows"), ("BlockEvents", "events, triggerEvent"), ("Tests", "tests"), ("TestScaffold", "tests"),
        ("ProcessFlow", "processes"), ("Platform", "timers, roles, siteProperties"), ("LogicExtensions", "clientVariables, sessionVariables, systemEvents, onException"),
        ("Content", "images, scripts, themes, resources, folders, locales, translations"), ("ImageAssets", "images"),
        ("ModuleSettings", "module"), ("ModuleReferences", "references"), ("Reuse", "copy"), ("Reuse.Builder", "copy"), ("Packages", "copy"),
        ("Scaffolding", "template"), ("TemplateCreation", "template"), ("Templates", "layout, template"),
        ("EntityDiagrams", "entityDiagrams"), ("Extensions", "extensions"), ("FlowLayout", "tidyFlows"),
        ("DataDeletion", "edits.delete"), ("ElementPaths", "edits"), ("Reflect", "edits.set"),
        ("Authorship", "apply (authorship)"), ("NameClash", "apply (names)"), ("History", "apply (history)"),
        ("StudioUi", "screens, blocks"),
    };

    // Method token -> source file stem, from the portable PDB next to OmlCli.Core.dll.
    static Dictionary<int, string> SourceFiles(Assembly core)
    {
        var map = new Dictionary<int, string>();
        var pdb = Path.ChangeExtension(core.Location, ".pdb");
        if (!File.Exists(pdb)) return map;
        using var stream = File.OpenRead(pdb);
        using var provider = System.Reflection.Metadata.MetadataReaderProvider.FromPortablePdbStream(stream);
        var reader = provider.GetMetadataReader();
        foreach (var h in reader.MethodDebugInformation)
        {
            var info = reader.GetMethodDebugInformation(h);
            var document = info.Document;
            if (document.IsNil)
                foreach (var point in info.GetSequencePoints()) { document = point.Document; break; }
            if (document.IsNil) continue;
            var name = reader.GetString(reader.GetDocument(document).Name);
            map[System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(h.ToDefinitionHandle())] =
                Path.GetFileNameWithoutExtension(name.Replace('\\', '/').Split('/')[^1]);
        }
        return map;
    }

    // What Refs.Find resolves by name, so a property of one of these types (or a base of one) takes an element name in
    // `set`. Keep in step with Refs.Owned.
    static readonly string[] ReferableTypes =
    {
        "OutSystems.Model.Logic.IServerAction", "OutSystems.Model.Logic.IClientAction", "OutSystems.Model.Logic.IServiceAction",
        "OutSystems.Model.Logic.IRole", "OutSystems.Model.Logic.IUserException", "OutSystems.Model.Logic.IException",
        "OutSystems.Model.Data.IEntity", "OutSystems.Model.Data.IStaticEntity", "OutSystems.Model.Data.IStructure",
        "OutSystems.Model.UI.IImage", "OutSystems.Model.UI.IScript", "OutSystems.Model.Data.IResource",
        "OutSystems.Model.UI.Mobile.IMobileTheme", "OutSystems.Model.UI.Web.IWebTheme", "OutSystems.Model.UI.Mobile.IMobileScreen",
        "OutSystems.Model.UI.Mobile.IMobileBlock", "OutSystems.Model.UI.Web.IWebScreen", "OutSystems.Model.UI.Web.IWebBlock",
        "OutSystems.Model.Processes.IProcess", "OutSystems.Model.Processes.ITimer",
    };

    // Rules on existing elements, from the tables ProductionRules enforces. (interface name or "*", member, rule, why)
    static IEnumerable<(string iface, string member, int rule, string why)> RuleTable()
    {
        foreach (var c in ProductionRules.ContractTypes)
        {
            yield return (c, "Name", 3, "renaming an existing element breaks its users");
            yield return (c, "Delete", c.Contains("Entity") ? 2 : 3, "deleting an existing element breaks its users and loses data");
        }
        foreach (var p in ProductionRules.AttributeShape)
            yield return ("IAttribute", p, 5, "existing columns hold production data");
        foreach (var (p, _) in ProductionRules.EntityShape)
            yield return ("IEntity", p, 5, "changes how existing rows are keyed, read or written");
        foreach (var p in new[] { "DataType", "IsMandatory" })
        {
            yield return ("IInputParameter", p, 4, "an existing public element's signature is used by other modules");
            yield return ("IOutputParameter", p, 4, "an existing public element's signature is used by other modules");
        }
        yield return ("*", "Public", 4, "an existing public element must not become private");
        yield return ("IBlockEvent", "IsMandatory", 4, "a mandatory event breaks instances without a handler");
        yield return ("ISQLNode", "Statement", 6, "only read-only SQL and INSERT");
    }

    // Members that change the model but are not a change a spec should make, and why. By member name.
    static string? OutOfScope(string member) => member switch
    {
        _ when History.IsField(member) => "history field: Service Studio records who changed what and when",
        "Close" or "Save" or "Lock" or "Unlock" or "Dispose" or "DoReadOperation" or "ForceValidate" or "SaveTextResources" =>
            "a session or file operation, not a change to the module",
        "RefreshReferences" or "RefreshDependencies" => "refreshes references to producer modules: `oml references` does it, after the user says yes (rule 10)",
        "Merge" => "Service Studio's Compare and Merge of two versions, not a spec change",
        "CreateMetadata" or "CreateInitialMetadata" or "CreateMissingMetadata" => "metadata other tools keep on an element (managedBy), not part of the module's behavior",
        "AddUsage" or "RemoveUsage" => "Service Studio's own feature-usage tracking",
        "CreateNewKeyBasedOnThis" or "SubstituteAndSimplify" => "returns a new value and changes nothing in the module",
        "Hash" => "set by Service Studio when it refreshes a reference",
        "Hidden" => "a validation message flag in Service Studio's UI, not part of the module",
        _ => null,
    };

    // Namespaces to areas, first match wins.
    static readonly (string prefix, string area)[] Areas =
    {
        ("OutSystems.Model.Logic.Aggregates", "data queries"), ("OutSystems.Model.Logic.Nodes", "logic"), ("OutSystems.Model.Logic", "logic"),
        ("OutSystems.Model.Data", "data"), ("OutSystems.Model.Types", "data"), ("OutSystems.Model.Processes", "processes"),
        ("OutSystems.Model.UI.Web", "UI (Traditional Web)"), ("OutSystems.Model.UI.Mobile", "UI (Reactive and Mobile)"), ("OutSystems.Model.UI", "UI (shared)"),
        ("OutSystems.Model.Integrations", "integrations"), ("ServiceStudio.Plugin.REST", "integrations"), ("ServiceStudio.Plugin.SOAP", "integrations"),
        ("ServiceStudio.Plugin.NRWidgets", "UI (Reactive and Mobile)"), ("ServiceStudio.Plugin.Widgets", "UI (Traditional Web)"),
        ("ServiceStudio.Plugin", "plugins"), ("OutSystems.Model.Applications", "module"), ("OutSystems.Model", "module"),
    };
    static string Area(Type t) => Areas.FirstOrDefault(a => (t.Namespace ?? "").StartsWith(a.prefix, StringComparison.Ordinal)).area ?? "other";

    public static int? Run(List<string> args)
    {
        string? outDir = null;
        for (var i = 1; i < args.Count; i++)
            if (args[i] == "--out" && i + 1 < args.Count) outDir = args[++i];
            else return null;
        Model.Services();
        var report = Build();
        if (outDir is null) { TerminalOutput.PrintNodeRaw(new JsonObject { ["build"] = report["build"]!.DeepClone(), ["areas"] = report["areas"]!.DeepClone(), ["total"] = report["total"]!.DeepClone() }); return 0; }
        Directory.CreateDirectory(outDir);
        var json = Path.Combine(outDir, "coverage.json");
        File.WriteAllText(json, report.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
        var md = Path.Combine(outDir, "coverage.md");
        File.WriteAllText(md, Markdown(report));
        TerminalOutput.PrintNodeRaw(new JsonObject { ["written"] = new JsonArray(json, md), ["total"] = report["total"]!.DeepClone() });
        return 0;
    }

    sealed record Member(Type type, string name, string kind, string signature, MemberInfo info, bool change, bool writable);

    internal static JsonObject Build()
    {
        var api = ApiTypes();
        var reachable = Reachable(api);
        var (calls, names) = OmlCalls();
        var rules = RuleTable().ToList();
        var objectSignature = typeof(OutSystems.Model.IObjectSignature);
        var referable = ReferableTypes.Select(n => api.FirstOrDefault(t => t.FullName == n)).OfType<Type>().ToList();

        var types = new JsonArray();
        var totals = new Dictionary<string, Dictionary<string, int>>();
        foreach (var t in reachable.OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var members = Members(t).ToList();
            var list = new JsonArray();
            var isElement = objectSignature.IsAssignableFrom(t);
            foreach (var m in members)
            {
                var entry = new JsonObject { ["member"] = m.name, ["kind"] = m.kind, ["signature"] = m.signature };
                var keys = new SortedSet<string>(StringComparer.Ordinal);
                if (calls.TryGetValue(m.info, out var users)) foreach (var u in users) keys.Add(u);
                var outOfScope = m.change ? OutOfScope(m.name) : null;
                // Called by name: a method through Reflect.Call or CallGeneric; a property where oml writes one by name.
                if (m.change && outOfScope is null && names.TryGetValue(m.name, out var uses))
                    foreach (var u in uses.Where(u => Means(u, t, m.kind == "property", isElement) && (m.kind == "method" || u.writesProperty))) keys.Add(u.keys + " (by name)");
                // The generic set (Reflect.Plan): any element a path reaches.
                string? setGap = null;
                if (m.change && isElement && outOfScope is null)
                {
                    if (m.kind == "property" && m.writable)
                    {
                        if (SetConverts(((PropertyInfo)m.info).PropertyType, referable) is { } gap) setGap = gap;
                        else keys.Add("edits.set");
                    }
                    else if (m.kind == "method" && m.name.StartsWith("Set", StringComparison.Ordinal) && m.info is MethodInfo mi
                             && !mi.IsGenericMethodDefinition && mi.GetParameters() is [{ } p] && (p.ParameterType == typeof(string) || p.ParameterType == typeof(bool)))
                        keys.Add("edits.set");
                }
                var rule = rules.FirstOrDefault(r => r.member == (m.name.StartsWith("Set") && m.kind == "method" && m.name.Length > 3 ? m.name[3..] : m.name)
                    && (r.iface == "*" || t.Name == r.iface || t.Name == r.iface + "Signature" || t.GetInterfaces().Any(i => i.Name == r.iface || i.Name == r.iface + "Signature")));
                string status;
                if (!m.change) status = "read-only";
                else if (outOfScope is not null) status = "out-of-scope";
                else if (keys.Count > 0) status = "covered";
                else if (rule.member is not null) status = "rule";
                else status = "not-covered";
                entry["status"] = status;
                if (keys.Count > 0) entry["keys"] = new JsonArray(keys.Select(k => (JsonNode?)k).ToArray());
                if (rule.member is not null && m.change)
                    entry[status == "covered" ? "existingRule" : "rule"] = $"rule {rule.rule}: {rule.why}";
                if (status == "not-covered") entry["reason"] = setGap ?? (isElement ? "no spec key calls it" : "not an element: no element path reaches it");
                if (status == "out-of-scope") entry["reason"] = outOfScope;
                list.Add(entry);
                var area = Area(t);
                if (!totals.TryGetValue(area, out var c)) totals[area] = c = new();
                Bump(c, "members"); Bump(c, status);
                if (m.change) Bump(c, "changes");
                if (status == "covered" && entry["existingRule"] is not null) Bump(c, "coveredWithRule");
            }
            if (!totals.TryGetValue(Area(t), out var tc)) totals[Area(t)] = tc = new();
            Bump(tc, "types");
            types.Add(new JsonObject
            {
                ["type"] = t.FullName, ["area"] = Area(t), ["assembly"] = t.Assembly.GetName().Name, ["element"] = isElement,
                ["members"] = list,
            });
        }
        var areas = new JsonObject();
        var total = new Dictionary<string, int>();
        foreach (var (area, c) in totals.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            areas[area] = Counts(c);
            foreach (var (k, v) in c) total[k] = total.GetValueOrDefault(k) + v;
        }
        return new JsonObject
        {
            ["build"] = Model.InstalledBuild(), ["generatedBy"] = "oml model-coverage --out tests/model-api",
            ["statuses"] = new JsonObject
            {
                ["covered"] = "a spec key changes it (keys); existingRule: refused on elements that existed before the spec",
                ["rule"] = "no spec key changes it and a production rule forbids the change",
                ["read-only"] = "a getter-only property or a query method",
                ["not-covered"] = "changes the model, no spec key reaches it (reason)",
                ["out-of-scope"] = "changes the model but is not a change a spec should make (reason)",
            },
            ["areas"] = areas, ["total"] = Counts(total), ["types"] = types,
        };
    }

    static void Bump(Dictionary<string, int> c, string k) => c[k] = c.GetValueOrDefault(k) + 1;
    static JsonObject Counts(Dictionary<string, int> c)
    {
        var o = new JsonObject();
        foreach (var k in new[] { "types", "members", "read-only", "changes", "out-of-scope", "covered", "coveredWithRule", "rule", "not-covered" }) o[k] = c.GetValueOrDefault(k);
        // Percent of the changes a spec should be able to make (out-of-scope members left out).
        var changes = c.GetValueOrDefault("changes") - c.GetValueOrDefault("out-of-scope");
        o["coveredPercent"] = changes == 0 ? 0 : Math.Round(100.0 * c.GetValueOrDefault("covered") / changes, 1);
        return o;
    }

    // What Reflect.Plan converts for a property of this type, or why it cannot.
    static string? SetConverts(Type type, List<Type> referable)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string) || t == typeof(bool) || t == typeof(int) || t.IsEnum) return null;
        if (typeof(OutSystems.Model.Types.ITypeSignature).IsAssignableFrom(t)) return null;
        if (referable.Any(r => t.IsAssignableFrom(r))) return null;
        return $"set cannot convert a value to {Short(t)}";
    }

    static string Short(Type t) => t.IsGenericType
        ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(", ", t.GetGenericArguments().Select(Short)) + ">" : t.Name;

    // Plugin API parts that extend Service Studio itself (descriptors, drag and drop, commands), not the model.
    static readonly string[] NotModel = [".Descriptors", ".DragDrop", ".Commands"];

    static List<Type> ApiTypes()
    {
        var context = AssemblyLoadContext.GetLoadContext(typeof(ModelCoverage).Assembly)!;
        var all = new List<Type>();
        foreach (var name in Assemblies)
        {
            Assembly asm;
            try { asm = context.LoadFromAssemblyName(new AssemblyName(name)); }
            catch (FileNotFoundException) { continue; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types.OfType<Type>().ToArray(); }
            all.AddRange(types.Where(t => t.IsInterface && t.IsPublic && (t.Namespace?.StartsWith("OutSystems.Model") == true || t.Namespace?.StartsWith("ServiceStudio.Plugin") == true)
                && !NotModel.Any(n => t.Namespace!.Contains(n, StringComparison.Ordinal))));
        }
        return all;
    }

    static HashSet<Type> Reachable(List<Type> api)
    {
        var known = api.ToHashSet();
        var root = api.First(t => t.FullName == "OutSystems.Model.IESpace");
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>();
        void Visit(Type? t)
        {
            if (t is null) return;
            if (t.IsArray || t.IsByRef) { Visit(t.GetElementType()); return; }
            if (t.IsGenericType) foreach (var a in t.GetGenericArguments()) Visit(a);
            if (t.IsGenericParameter) { foreach (var c in t.GetGenericParameterConstraints()) Visit(c); return; }
            var def = t.IsGenericType ? t.GetGenericTypeDefinition() : t;
            if (known.Contains(def) && seen.Add(def)) pending.Enqueue(def);
        }
        Visit(root);
        while (pending.TryDequeue(out var t))
        {
            foreach (var b in t.GetInterfaces()) Visit(b);
            foreach (var sub in api.Where(x => x != t && t.IsAssignableFrom(x))) Visit(sub);
            foreach (var p in SafeProperties(t)) Visit(p.PropertyType);
            foreach (var m in SafeMethods(t))
            {
                try { Visit(m.ReturnType); } catch (Exception) { }
                if (m.IsGenericMethodDefinition) foreach (var g in m.GetGenericArguments()) Visit(g);
            }
        }
        return seen;
    }

    const BindingFlags Declared = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    static PropertyInfo[] SafeProperties(Type t) { try { return t.GetProperties(Declared); } catch (Exception) { return []; } }
    static MethodInfo[] SafeMethods(Type t) { try { return t.GetMethods(Declared).Where(m => !m.IsSpecialName).ToArray(); } catch (Exception) { return []; } }

    static IEnumerable<Member> Members(Type t)
    {
        foreach (var p in SafeProperties(t).OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            var writable = p.CanWrite && p.SetMethod?.IsPublic == true;
            yield return new(t, p.Name, "property", Signature(() => $"{Short(p.PropertyType)} {p.Name} {{ get;{(writable ? " set;" : "")} }}"), p, writable, writable);
        }
        foreach (var m in SafeMethods(t).OrderBy(m => m.Name, StringComparer.Ordinal).ThenBy(m => m.GetParameters().Length))
        {
            var read = ReadVerbs.Any(v => m.Name.StartsWith(v, StringComparison.Ordinal) && (m.Name.Length == v.Length || char.IsUpper(m.Name[v.Length])));
            yield return new(t, m.Name, "method", Signature(() => $"{Short(m.ReturnType)} {m.Name}{(m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "")}({string.Join(", ", m.GetParameters().Select(x => Short(x.ParameterType) + " " + x.Name))})"), m, !read, false);
        }
    }

    static string Signature(Func<string> f) { try { return f(); } catch (Exception) { return "?"; } }

    // A name oml uses as a string (Reflect.Call, CallGeneric, Reflect.Property), with the static type of the object it
    // is used on when the IL shows it (the value loaded just before the string), and the keys of the calling code.
    sealed record NameUse(Type? receiver, string keys, bool writesProperty);

    // oml's own code: every Model API member that the apply path calls, with the spec keys of the calling code; and
    // the names it uses as strings.
    static (Dictionary<MemberInfo, HashSet<string>> calls, Dictionary<string, List<NameUse>> names) OmlCalls()
    {
        var core = typeof(ModelCoverage).Assembly;
        var calls = new Dictionary<MemberInfo, HashSet<string>>();
        var names = new Dictionary<string, List<NameUse>>(StringComparer.Ordinal);
        var start = core.GetTypes().Where(t => Outer(t).Name == "Apply").SelectMany(MethodsOf).ToList();
        var files = SourceFiles(core);
        var seen = new HashSet<MethodBase>();
        var pending = new Stack<MethodBase>(start);
        while (pending.TryPop(out var method))
        {
            if (!seen.Add(method)) continue;
            var owner = method.Module.Assembly == core && files.TryGetValue(method.MetadataToken, out var file) ? file : Outer(method.DeclaringType!).Name;
            var keys = FileKeys.FirstOrDefault(c => c.file == owner).keys ?? $"apply ({owner}.cs)";
            var used = new List<(string name, Type? receiver)>();
            var setsProperty = false;
            var code = Instructions(method).ToList();
            for (var i = 0; i < code.Count; i++)
            {
                var (op, operand) = code[i];
                if (op.OperandType == OperandType.InlineString)
                {
                    try
                    {
                        var s = method.Module.ResolveString((int)operand);
                        if (s.Length is > 2 and < 60 && s.All(char.IsLetterOrDigit) && char.IsUpper(s[0]))
                            used.Add((s, i > 0 ? Receiver(method, code[i - 1]) : null));
                    }
                    catch (Exception) { }
                    continue;
                }
                if (op.OperandType != OperandType.InlineMethod) continue;
                var target = ResolveMethod(method, (int)operand);
                if (target is null) continue;
                if (target is MethodInfo { IsGenericMethod: true } g && !g.IsGenericMethodDefinition) target = g.GetGenericMethodDefinition();
                var declaring = target.DeclaringType;
                if (declaring is null) continue;
                if (declaring == typeof(PropertyInfo) && target.Name == "SetValue") setsProperty = true;
                if (declaring.Assembly == core)
                {
                    // A helper that writes a property it is given by name (Content.Set(element, "Folder", value)).
                    if (WritesProperty(target)) setsProperty = true;
                    if (declaring.IsGenericType && !declaring.IsGenericTypeDefinition) continue; // reached through the definition
                    pending.Push(target);
                    continue;
                }
                if (!IsApi(declaring)) continue;
                MemberInfo member = target;
                if (target.IsSpecialName && target.Name.Length > 4 && target.Name[3] == '_')
                    member = (MemberInfo?)declaring.GetProperty(target.Name[4..], Declared) ?? target;
                (calls.TryGetValue(member, out var users) ? users : calls[member] = new()).Add(keys);
            }
            // A name built from two strings of the same method ("Create" + "OnSessionStart") counts too, on the prefix's receiver.
            foreach (var prefix in used.Where(u => u.name is "Create" or "Set" or "Add").ToList())
                used.AddRange(used.Where(u => !u.name.StartsWith(prefix.name, StringComparison.Ordinal)).Select(u => (prefix.name + u.name, prefix.receiver)).ToList());
            foreach (var (name, receiver) in used)
                (names.TryGetValue(name, out var list) ? list : names[name] = new()).Add(new(receiver, keys, setsProperty));
        }
        return (calls, names);
    }

    static readonly Dictionary<MethodBase, bool> writers = new();
    // Whether a method of oml calls PropertyInfo.SetValue itself.
    static bool WritesProperty(MethodBase method)
    {
        if (writers.TryGetValue(method, out var known)) return known;
        var writes = false;
        foreach (var (op, operand) in Instructions(method))
            if (op.OperandType == OperandType.InlineMethod && ResolveMethod(method, (int)operand) is { Name: "SetValue" } m && m.DeclaringType == typeof(PropertyInfo))
            { writes = true; break; }
        return writers[method] = writes;
    }

    static bool IsApi(Type t) => t.Namespace?.StartsWith("OutSystems.Model") == true || t.Namespace?.StartsWith("ServiceStudio.Plugin") == true;

    static MethodBase? ResolveMethod(MethodBase method, int token)
    {
        try
        {
            return method.Module.ResolveMethod(token, method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                method.IsGenericMethod ? method.GetGenericArguments() : null);
        }
        catch (Exception) { return null; }
    }

    // The static type of the value an instruction loads, when it is a Model API interface.
    static Type? Receiver(MethodBase method, (OpCode op, long operand) load)
    {
        Type? type = null;
        try
        {
            var name = load.op.Name ?? "";
            int? arg = name switch { "ldarg.0" => 0, "ldarg.1" => 1, "ldarg.2" => 2, "ldarg.3" => 3, "ldarg.s" or "ldarg" => (int)load.operand, _ => null };
            int? local = name switch { "ldloc.0" => 0, "ldloc.1" => 1, "ldloc.2" => 2, "ldloc.3" => 3, "ldloc.s" or "ldloc" => (int)load.operand, _ => null };
            if (arg is { } a)
            {
                var ps = method.GetParameters();
                type = method.IsStatic ? ps.ElementAtOrDefault(a)?.ParameterType : a == 0 ? method.DeclaringType : ps.ElementAtOrDefault(a - 1)?.ParameterType;
            }
            else if (local is { } l) type = method.GetMethodBody()?.LocalVariables.ElementAtOrDefault(l)?.LocalType;
            else if (name is "ldfld" or "ldsfld") type = method.Module.ResolveField((int)load.operand)?.FieldType;
            else if (name is "call" or "callvirt") type = (ResolveMethod(method, (int)load.operand) as MethodInfo)?.ReturnType;
            else if (name is "castclass" or "isinst" or "unbox.any") type = method.Module.ResolveType((int)load.operand);
        }
        catch (Exception) { return null; }
        return type is not null && type.IsInterface && IsApi(type) ? type : null;
    }

    // Whether a name used on `receiver` can mean the member of `t`: the same type, a base or a derived one.
    // A method name used on an object of unknown type counts for every type that has it; a property name needs the type.
    // A property name used on an object of unknown type counts for elements (a path reaches them), not for other types.
    static bool Means(NameUse use, Type t, bool property, bool element) =>
        use.receiver is { } r ? r.IsAssignableFrom(t) || t.IsAssignableFrom(r) : !property || element;

    static Type Outer(Type t) { while (t.DeclaringType is not null) t = t.DeclaringType; return t; }

    static IEnumerable<MethodBase> MethodsOf(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Cast<MethodBase>()
            .Concat(t.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));

    static readonly Dictionary<short, OpCode> OpCodesByValue = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    // The instructions of a method body with their operand (a token, a local or argument index, or 0).
    static IEnumerable<(OpCode op, long operand)> Instructions(MethodBase method)
    {
        byte[]? il;
        try { il = method.GetMethodBody()?.GetILAsByteArray(); } catch (Exception) { yield break; }
        if (il is null) yield break;
        for (var i = 0; i < il.Length;)
        {
            short value = il[i++];
            if (value == 0xFE && i < il.Length) value = (short)(0xFE00 | il[i++]);
            if (!OpCodesByValue.TryGetValue(value, out var op)) yield break;
            long operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineVar: operand = il[i]; i += 1; break;
                case OperandType.ShortInlineBrTarget or OperandType.ShortInlineI: i += 1; break;
                case OperandType.InlineVar: operand = BitConverter.ToUInt16(il, i); i += 2; break;
                case OperandType.InlineI8 or OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: var n = BitConverter.ToInt32(il, i); i += 4 + 4 * n; break;
                default: operand = BitConverter.ToInt32(il, i); i += 4; break;
            }
            yield return (op, operand);
        }
    }

    static string Markdown(JsonObject report)
    {
        var b = new StringBuilder();
        b.AppendLine("# Model API coverage");
        b.AppendLine();
        b.AppendLine($"Service Studio {report["build"]}. Generated by `oml model-coverage --out tests/model-api`; do not edit by hand. The full list is coverage.json.");
        b.AppendLine();
        b.AppendLine("Every public Model API interface reachable from `IESpace`, and each of its members. A member that changes the model is");
        b.AppendLine("**covered** when a spec key changes it, **rule** when only a production rule (skill/rules.md) stands in the way, and");
        b.AppendLine("**not covered** otherwise. Getter-only properties and query methods are **read-only**. **Out of scope** members change");
        b.AppendLine("the model but are not a change a spec should make (history stamps, session and file operations, reference refresh).");
        b.AppendLine("*With rule* counts covered members that are refused on elements that existed before the spec (renames, deletes,");
        b.AppendLine("attribute types, signatures). *Covered %* is covered / (changes - out of scope).");
        b.AppendLine();
        b.AppendLine("How a member counts as covered: oml's apply code calls it (read from oml's own IL, starting at `Apply`; *by name* means");
        b.AppendLine("through `Reflect.Call` or a property written by name), or the generic `set` of `edits` and of widget and node");
        b.AppendLine("properties reaches it (a writable property of a type `set` converts, or a `Set*(string)` / `Set*(bool)` method, on any");
        b.AppendLine("element an element path reaches). A covered member is reachable from a spec; it is not a promise that every value works.");
        b.AppendLine();
        b.AppendLine("| Area | Types | Members | Read-only | Changes | Out of scope | Covered | With rule | Rule | Not covered | Covered % |");
        b.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        void Row(string name, JsonNode c) => b.AppendLine($"| {name} | {c["types"]} | {c["members"]} | {c["read-only"]} | {c["changes"]} | {c["out-of-scope"]} | {c["covered"]} | {c["coveredWithRule"]} | {c["rule"]} | {c["not-covered"]} | {c["coveredPercent"]} |");
        foreach (var (area, c) in report["areas"]!.AsObject()) Row(area, c!);
        Row("**total**", report["total"]!);
        b.AppendLine();
        var types = report["types"]!.AsArray().Select(t => t!.AsObject()).ToList();
        var gaps = types.SelectMany(t => t["members"]!.AsArray().Select(m => (type: t["type"]!.GetValue<string>(), area: t["area"]!.GetValue<string>(), m: m!.AsObject())))
            .Where(x => x.m["status"]!.GetValue<string>() is "not-covered" or "out-of-scope").ToList();
        b.AppendLine("## Why members are not covered");
        b.AppendLine();
        b.AppendLine("| Status | Reason | Members |");
        b.AppendLine("|---|---|---:|");
        foreach (var g in gaps.GroupBy(x => (status: x.m["status"]!.GetValue<string>(), reason: x.m["reason"]!.GetValue<string>()))
                     .OrderBy(g => g.Key.status, StringComparer.Ordinal).ThenByDescending(g => g.Count()).ThenBy(g => g.Key.reason, StringComparer.Ordinal))
            b.AppendLine($"| {g.Key.status} | {g.Key.reason} | {g.Count()} |");
        gaps = gaps.Where(x => x.m["status"]!.GetValue<string>() == "not-covered").ToList();
        b.AppendLine();
        b.AppendLine("## Forbidden by a production rule");
        b.AppendLine();
        b.AppendLine("| Rule | Not covered at all | Covered on new elements only |");
        b.AppendLine("|---|---:|---:|");
        var ruled = types.SelectMany(t => t["members"]!.AsArray().Select(m => m!.AsObject()))
            .Select(m => (text: (m["rule"] ?? m["existingRule"])?.GetValue<string>(), covered: m["existingRule"] is not null)).Where(x => x.text is not null).ToList();
        foreach (var g in ruled.GroupBy(x => x.text!).OrderBy(g => g.Key, StringComparer.Ordinal))
            b.AppendLine($"| {g.Key} | {g.Count(x => !x.covered)} | {g.Count(x => x.covered)} |");
        b.AppendLine();
        b.AppendLine("## Types with the most members not covered");
        b.AppendLine();
        b.AppendLine("| Type | Area | Not covered | Examples |");
        b.AppendLine("|---|---|---:|---|");
        foreach (var g in gaps.GroupBy(x => (x.type, x.area)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key.type, StringComparer.Ordinal).Take(40))
            b.AppendLine($"| {g.Key.type} | {g.Key.area} | {g.Count()} | {string.Join(", ", g.Select(x => x.m["member"]!.GetValue<string>()).Distinct().Take(6))} |");
        b.AppendLine();
        return b.ToString();
    }
}
