using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OmlCli;

// The key and type knowledge comes from the Spec record types in OmlCli.Core (src/Core/Apply.cs, Builder.cs, ...),
// read by reflection. Loading Core and reading those types needs no Service Studio assembly: the records only use
// System types. Only the parts that apply keeps as raw JsonElement (flow nodes, widgets, edits, platform elements)
// get a small hand-written shape check below; they hold no key lists except the edit operations.
sealed record SpecProp(string Name, string Json, Type Type, bool Required);

static class SpecModel
{
    static Assembly? core;
    static Assembly Core => core ??= Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "OmlCli.Core.dll"));

    public static Type Root => Type("Spec");
    public static Type Type(string name) => Core.GetType("OmlCli." + name, throwOnError: true)!;

    public static Type Unwrap(Type t) => Nullable.GetUnderlyingType(t) ?? t;
    public static bool IsList(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>);
    public static bool IsMap(Type t) => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>);
    public static bool IsRecord(Type t) => t.GetMethod("<Clone>$") is not null;

    // A constructor parameter without a default value is a required key.
    public static List<SpecProp> Props(Type record) =>
        record.GetConstructors().Single().GetParameters()
            .Select(p => new SpecProp(p.Name!, char.ToLowerInvariant(p.Name![0]) + p.Name[1..], Unwrap(p.ParameterType), !p.HasDefaultValue))
            .ToList();
}

enum RawKind { Any, Flow, ProcessList, Named, Widgets, Edits, Object, Elements }

static class SpecShapes
{
    // How a JsonElement-typed spec member is read. Keyed by the member name, so a new typed record that
    // reuses a member name (Flow, Widgets, ...) is checked the same way with no change here.
    public static RawKind Kind(string member) => member switch
    {
        "Flow" or "Nodes" or "Preparation" => RawKind.Flow,
        "Processes" => RawKind.ProcessList,
        "Timers" or "Roles" or "SiteProperties" or "Aggregates" => RawKind.Named,
        "Widgets" or "Placeholders" => RawKind.Widgets,
        "Edits" => RawKind.Edits,
        "Module" => RawKind.Object,
        "Elements" => RawKind.Elements,
        _ => RawKind.Any,
    };

    public static readonly string[] NodeKinds =
    {
        "assign", "if", "forEach", "switch", "aggregate", "sql", "execute", "refreshData", "destination", "raiseException",
        "end", "jsonDeserialize", "jsonSerialize", "javaScript", "exceptionHandler", "comment", "message", "download",
        "excelToRecordList", "recordListToExcel", "sendEmail", "triggerEvent",
    };

    public static readonly string[] ProcessKinds = { "humanActivity", "automaticActivity", "decision", "end", "start" };

    // Keys a node of this kind cannot be built without (spec.md, "Logic and flows").
    public static readonly Dictionary<string, string[]> NodeRequired = new(StringComparer.OrdinalIgnoreCase)
    {
        ["execute"] = new[] { "action" }, ["if"] = new[] { "condition", "true", "false" },
        ["destination"] = new[] { "screen" }, ["raiseException"] = new[] { "exception" },
        ["triggerEvent"] = new[] { "event" }, ["sendEmail"] = new[] { "email", "from", "to" },
    };

    // Edit operations (spec.md, "Edits"; the switch in src/Core/Edits.cs). Typed: the rest of the keys follow that record.
    public sealed record EditOp(string[] Required, string[] Optional, string? Typed = null);

    public static readonly Dictionary<string, EditOp> EditOps = new()
    {
        ["addExceptionHandler"] = new(new[] { "action", "flow" }, new string[0]),
        ["setAccess"] = new(new[] { "screen" }, new string[0], "AccessSpec"),
        ["insertNodes"] = new(new[] { "action", "after", "flow" }, new[] { "connector" }),
        ["addWidgets"] = new(new[] { "screen", "widgets" }, new[] { "parent", "area", "index" }),
        ["replaceWidgets"] = new(new[] { "at", "widgets" }, new[] { "area" }),
        ["addVariables"] = new(new[] { "action" }, new string[0], "ActionSpec"),
        ["addParts"] = new(new[] { "screen" }, new string[0], "ScreenSpec"),
        ["set"] = new(new[] { "element", "properties" }, new string[0]),
        ["rename"] = new(new[] { "element", "to" }, new string[0]),
        ["delete"] = new(new[] { "element" }, new[] { "allowDataLoss" }),
        ["addToAggregate"] = new(new[] { "aggregate" }, new[] { "sources", "joins", "filters", "sorts", "groupBy", "aggregates", "calculated", "groupFilters", "groupSorts", "groupCalculated" }),
        ["setArgs"] = new(new[] { "element", "args" }, new string[0]),
    };

    // Value type of the non-string keys of an edit.
    public static readonly Dictionary<string, JsonValueKind> EditValueKinds = new()
    {
        ["flow"] = JsonValueKind.Array, ["widgets"] = JsonValueKind.Array, ["properties"] = JsonValueKind.Object,
        ["index"] = JsonValueKind.Number, ["allowDataLoss"] = JsonValueKind.True, ["args"] = JsonValueKind.Object,
        ["sources"] = JsonValueKind.Array, ["joins"] = JsonValueKind.Array, ["filters"] = JsonValueKind.Array, ["sorts"] = JsonValueKind.Array,
        ["groupBy"] = JsonValueKind.Array, ["aggregates"] = JsonValueKind.Array, ["calculated"] = JsonValueKind.Array,
        ["groupFilters"] = JsonValueKind.Array, ["groupSorts"] = JsonValueKind.Array, ["groupCalculated"] = JsonValueKind.Array,
    };
}

sealed class SpecChecker
{
    readonly List<(string path, string message)> problems = new();
    public IReadOnlyList<(string path, string message)> Problems => problems;

    void Add(string path, string message) => problems.Add((path, message));
    static string Key(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";
    static string Item(string path, int i) => $"{path}[{i}]";

    static string Describe(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => "a string", JsonValueKind.Number => "a number", JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Array => "an array", JsonValueKind.Object => "an object", _ => "null",
    };

    void Expect(string path, string what, JsonElement v) => Add(path, $"expected {what}, got {Describe(v)}");

    static string Suggest(string wrong, IEnumerable<string> candidates, string noun = "key")
    {
        var best = candidates.Select(c => (c, d: SpecDocs.Distance(c.ToLowerInvariant(), wrong.ToLowerInvariant())))
            .OrderBy(x => x.d).FirstOrDefault();
        return best.c is not null && best.d <= Math.Max(1, wrong.Length / 3) ?$"; did you mean '{best.c}'?" : "";
    }

    public static IReadOnlyList<(string path, string message)> Check(string text)
    {
        var checker = new SpecChecker();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException e) { return new[] { ("$", $"Invalid spec JSON: {e.Message}") }; }
        if (JsonOutput.UnpairedSurrogatePath(System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })) is { } bad) // the same check as apply
        { doc.Dispose(); return new[] { (bad, JsonOutput.UnpairedSurrogate) }; }
        using (doc) checker.Record(doc.RootElement, SpecModel.Root, "", root: true);
        return checker.Problems;
    }

    // ---- typed values ----

    void Value(JsonElement v, Type type, string path, string member)
    {
        type = SpecModel.Unwrap(type);
        if (v.ValueKind == JsonValueKind.Null)
        {
            if (type.IsValueType && type != typeof(JsonElement)) Add(path, "must not be null");
            return;
        }
        if (type == typeof(string)) { if (v.ValueKind != JsonValueKind.String) Expect(path, "a string", v); }
        else if (type == typeof(bool)) { if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Expect(path, "true or false", v); }
        else if (type == typeof(int)) { if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out _)) Expect(path, "a whole number", v); }
        else if (type == typeof(JsonElement)) Raw(v, member, path);
        else if (SpecModel.IsList(type))
        {
            var item = type.GetGenericArguments()[0];
            if (v.ValueKind != JsonValueKind.Array) { Expect(path, "an array", v); return; }
            if (item == typeof(JsonElement)) RawList(v, member, path);
            else { var i = 0; foreach (var e in v.EnumerateArray()) Value(e, item, Item(path, i++), member); }
        }
        else if (SpecModel.IsMap(type))
        {
            if (v.ValueKind != JsonValueKind.Object) { Expect(path, "an object", v); return; }
            foreach (var p in v.EnumerateObject()) Value(p.Value, type.GetGenericArguments()[1], Key(path, p.Name), member);
        }
        else if (SpecModel.IsRecord(type)) Record(v, type, path);
    }

    void Record(JsonElement v, Type type, string path, bool root = false, IEnumerable<string>? extra = null, bool requireAll = true)
    {
        if (v.ValueKind != JsonValueKind.Object) { Expect(path, "an object", v); return; }
        var props = SpecModel.Props(type);
        var known = props.Select(p => p.Json).Concat(extra ?? Enumerable.Empty<string>()).ToList();
        foreach (var p in v.EnumerateObject())
        {
            var prop = props.FirstOrDefault(x => x.Json.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (prop is null)
            {
                // Same rule as apply: keys starting with "_" or "$" are notes ("$schema" included).
                if (p.Name.StartsWith('_') || p.Name.StartsWith('$') || extra?.Contains(p.Name) == true) continue;
                Add(Key(path, p.Name), $"unknown key '{p.Name}'{Suggest(p.Name, known)}");
            }
            else Value(p.Value, prop.Type, Key(path, p.Name), prop.Name);
        }
        if (!requireAll) return;
        foreach (var prop in props.Where(x => x.Required))
            if (!v.EnumerateObject().Any(p => p.Name.Equals(prop.Json, StringComparison.OrdinalIgnoreCase)))
                Add(path, $"missing required key '{prop.Json}'");
    }

    // ---- raw JSON members ----

    void Raw(JsonElement v, string member, string path)
    {
        switch (SpecShapes.Kind(member))
        {
            case RawKind.Object when v.ValueKind != JsonValueKind.Object: Expect(path, "an object", v); break;
            case RawKind.Elements when v.ValueKind == JsonValueKind.String && v.GetString() != "*":
                Add(path, "a string value must be \"*\" (all supported public elements); use an array of names otherwise"); break;
            case RawKind.Elements when v.ValueKind is not (JsonValueKind.String or JsonValueKind.Array): Expect(path, "\"*\" or an array of names", v); break;
            case RawKind.Elements when v.ValueKind == JsonValueKind.Array:
                var i = 0;
                foreach (var e in v.EnumerateArray()) { if (e.ValueKind != JsonValueKind.String) Expect(Item(path, i), "a string", e); i++; }
                break;
        }
    }

    void RawList(JsonElement list, string member, string path)
    {
        switch (SpecShapes.Kind(member))
        {
            case RawKind.Flow: Flow(list, path, process: false, targets: true); break;
            case RawKind.ProcessList:
                var i = 0;
                foreach (var p in list.EnumerateArray())
                {
                    var at = Item(path, i++);
                    if (!Named(p, at)) continue;
                    if (p.TryGetProperty("flow", out var flow))
                    {
                        if (flow.ValueKind != JsonValueKind.Array) Expect(Key(at, "flow"), "an array", flow);
                        else Flow(flow, Key(at, "flow"), process: true, targets: true);
                    }
                }
                break;
            case RawKind.Named:
                var n = 0;
                foreach (var p in list.EnumerateArray())
                {
                    var at = Item(path, n++);
                    if (Named(p, at) && member == "Aggregates") Query(p, at);
                }
                break;
            case RawKind.Widgets: Widgets(list, path); break;
            case RawKind.Edits: Edits(list, path); break;
        }
    }

    bool Named(JsonElement item, string path)
    {
        if (item.ValueKind != JsonValueKind.Object) { Expect(path, "an object", item); return false; }
        if (!item.TryGetProperty("name", out var name)) { Add(path, "missing required key 'name'"); return false; }
        if (name.ValueKind != JsonValueKind.String) { Expect(Key(path, "name"), "a string", name); return false; }
        return true;
    }

    // The objects inside an aggregate's lists (sorts, groupBy, joins, ...): the same check apply runs, read from Core.
    void Query(JsonElement aggregate, string path)
    {
        var found = (List<string>)SpecModel.Type("QueryShapes").GetMethod("Problems", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object[] { aggregate })!;
        foreach (var problem in found) Add(path, problem);
    }

    void Widgets(JsonElement list, string path)
    {
        var i = 0;
        foreach (var w in list.EnumerateArray())
        {
            var at = Item(path, i++);
            if (w.ValueKind != JsonValueKind.Object) { Expect(at, "an object", w); continue; }
            if (!w.TryGetProperty("type", out var type)) Add(at, "missing required key 'type'");
            else if (type.ValueKind != JsonValueKind.String) Expect(Key(at, "type"), "a string", type);
            if (w.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array) Expect(Key(at, "children"), "an array", children);
                else Widgets(children, Key(at, "children"));
            }
            if (w.TryGetProperty("placeholders", out var holders) && holders.ValueKind == JsonValueKind.Object)
                foreach (var h in holders.EnumerateObject())
                {
                    if (h.Value.ValueKind != JsonValueKind.Array) Expect(Key(Key(at, "placeholders"), h.Name), "an array", h.Value);
                    else Widgets(h.Value, Key(Key(at, "placeholders"), h.Name));
                }
        }
    }

    void Flow(JsonElement list, string path, bool process, bool targets)
    {
        var kinds = process ? SpecShapes.ProcessKinds.Concat(SpecShapes.NodeKinds).ToArray() : SpecShapes.NodeKinds;
        var nodes = new List<(JsonElement node, string at, string name)>();
        var seen = new HashSet<string>();
        var i = 0;
        foreach (var n in list.EnumerateArray())
        {
            var at = Item(path, i++);
            if (n.ValueKind != JsonValueKind.Object) { Expect(at, "an object", n); continue; }
            string? kind = null;
            if (!n.TryGetProperty("kind", out var k)) Add(at, "missing required key 'kind'");
            else if (k.ValueKind != JsonValueKind.String) Expect(Key(at, "kind"), "a string", k);
            else if (!kinds.Contains(kind = k.GetString()!, StringComparer.OrdinalIgnoreCase))
                Add(Key(at, "kind"), $"unknown node kind '{kind}'{Suggest(kind, kinds)}. Other kinds resolve as I<Kind>Node; see 'oml api \"OutSystems.Model.Logic.Nodes.I*Node\"'.");
            if (kind is not null && SpecShapes.NodeRequired.TryGetValue(kind, out var required) && !process)
                foreach (var key in required.Where(r => !n.TryGetProperty(r, out _))) Add(at, $"missing required key '{key}' for kind '{kind}'");
            if (n.TryGetProperty("name", out var nm) && nm.ValueKind != JsonValueKind.String) Expect(Key(at, "name"), "a string", nm);
            if (n.TryGetProperty("assign", out var assign) && assign.ValueKind != JsonValueKind.Object) Expect(Key(at, "assign"), "an object", assign);
            if (kind?.Equals("aggregate", StringComparison.OrdinalIgnoreCase) == true) Query(n, at);

            // A node's name defaults to its kind; an execute node's to the called action.
            var name = nm.ValueKind == JsonValueKind.String ? nm.GetString()!
                : kind is null ? "" : kind.Equals("execute", StringComparison.OrdinalIgnoreCase) && n.TryGetProperty("action", out var act) && act.ValueKind == JsonValueKind.String
                    ? act.GetString()![(act.GetString()!.LastIndexOf('.') + 1)..] : kind;
            var isEnd = kind?.Equals("end", StringComparison.OrdinalIgnoreCase) == true && name == "End";
            if (name.Length > 0 && !isEnd && !seen.Add(name)) Add(Key(at, "name"), $"duplicate node name '{name}'; give each node in a flow a unique name");
            nodes.Add((n, at, name));
        }
        if (!targets) return;
        var names = nodes.Select(x => x.name).Concat(new[] { "Start", "End" }).ToHashSet();
        foreach (var (n, at, _) in nodes)
        {
            foreach (var key in new[] { "next", "true", "false", "cycle", "otherwise" })
                if (n.TryGetProperty(key, out var t)) Target(t, Key(at, key), names);
            if (!n.TryGetProperty("cases", out var cases)) continue;
            if (cases.ValueKind != JsonValueKind.Array) { Expect(Key(at, "cases"), "an array", cases); continue; }
            var c = 0;
            foreach (var one in cases.EnumerateArray())
            {
                var cat = Item(Key(at, "cases"), c++);
                if (one.ValueKind != JsonValueKind.Object) Expect(cat, "an object", one);
                else if (one.TryGetProperty("next", out var t)) Target(t, Key(cat, "next"), names);
                else Add(cat, "missing required key 'next'");
            }
        }
    }

    void Target(JsonElement t, string path, HashSet<string> names)
    {
        if (t.ValueKind != JsonValueKind.String) { Expect(path, "a node name (string)", t); return; }
        var target = t.GetString()!;
        if (!names.Contains(target))
            Add(path, $"no node named '{target}' in this flow{Suggest(target, names)}");
    }

    void Edits(JsonElement list, string path)
    {
        var i = 0;
        foreach (var e in list.EnumerateArray())
        {
            var at = Item(path, i++);
            if (e.ValueKind != JsonValueKind.Object) { Expect(at, "an object", e); continue; }
            if (!e.TryGetProperty("op", out var op)) { Add(at, "missing required key 'op'"); continue; }
            if (op.ValueKind != JsonValueKind.String) { Expect(Key(at, "op"), "a string", op); continue; }
            if (!SpecShapes.EditOps.TryGetValue(op.GetString()!, out var def))
            {
                Add(Key(at, "op"), $"unknown edit op '{op.GetString()}'{Suggest(op.GetString()!, SpecShapes.EditOps.Keys)}. Valid: {string.Join(", ", SpecShapes.EditOps.Keys)}");
                continue;
            }
            var own = def.Required.Concat(def.Optional).Append("op").ToList();
            if (def.Typed is not null) Record(e, SpecModel.Type(def.Typed), at, extra: own, requireAll: false);
            else
                foreach (var p in e.EnumerateObject().Where(p => !own.Contains(p.Name)))
                    Add(Key(at, p.Name), $"unknown key '{p.Name}' for op '{op.GetString()}'{Suggest(p.Name, own)}");
            foreach (var key in def.Required.Where(r => !e.TryGetProperty(r, out _))) Add(at, $"missing required key '{key}' for op '{op.GetString()}'");
            foreach (var key in own.Where(k => k != "op" && e.TryGetProperty(k, out _)))
            {
                var v = e.GetProperty(key);
                if (SpecShapes.EditValueKinds.TryGetValue(key, out var want))
                {
                    var ok = want == JsonValueKind.True ? v.ValueKind is JsonValueKind.True or JsonValueKind.False : v.ValueKind == want;
                    if (!ok) Expect(Key(at, key), want switch { JsonValueKind.Array => "an array", JsonValueKind.Object => "an object", JsonValueKind.Number => "a number", _ => "true or false" }, v);
                }
                else if (v.ValueKind != JsonValueKind.String) Expect(Key(at, key), "a string", v);
            }
            if (def.Required.Contains("flow") || def.Optional.Contains("flow"))
                if (e.TryGetProperty("flow", out var flow) && flow.ValueKind == JsonValueKind.Array)
                    Flow(flow, Key(at, "flow"), process: false, targets: false); // targets may name nodes already in the action
            if (e.TryGetProperty("widgets", out var widgets) && widgets.ValueKind == JsonValueKind.Array) Widgets(widgets, Key(at, "widgets"));
            if (op.GetString() == "addToAggregate") Query(e, at);
            if (op.GetString() == "setArgs" && e.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Object)
                foreach (var a in args.EnumerateObject().Where(a => a.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)))
                    Expect(Key(Key(at, "args"), a.Name), "an expression string, a number or true/false", a.Value);
        }
    }
}

// JSON Schema (draft 2020-12) from the same record types.
static class SpecSchema
{
    public static JsonObject Build()
    {
        var defs = new JsonObject();
        var descriptions = SpecDocs.KeyDescriptions();
        var root = Object(SpecModel.Root, defs, descriptions, root: true);
        root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
        root["title"] = "oml apply spec";
        root["description"] = "Spec file for 'oml apply'. Every key is optional. Run 'oml spec <key>' for the reference of one key.";
        var props = (JsonObject)root["properties"]!;
        props["$schema"] = new JsonObject { ["type"] = "string" };
        root["$defs"] = defs;
        AddRawDefs(defs);
        return root;
    }

    static JsonObject Object(Type record, JsonObject defs, Dictionary<string, string> descriptions, bool root = false)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in SpecModel.Props(record))
        {
            var schema = For(p.Type, p.Name, defs);
            if (root && descriptions.TryGetValue(p.Json, out var d)) schema["description"] = $"{d} (oml spec {p.Json})";
            props[p.Json] = schema;
            if (p.Required) required.Add(p.Json);
        }
        var obj = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Count > 0) obj["required"] = required;
        return obj;
    }

    static JsonObject For(Type type, string member, JsonObject defs)
    {
        type = SpecModel.Unwrap(type);
        // Code keys also take { "file": "relative/path" } (spec.md, "Code from files").
        if (type == typeof(string) && SpecFiles.Keys.Contains(member, StringComparer.OrdinalIgnoreCase))
            return new JsonObject { ["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string" }, new JsonObject { ["type"] = "object",
                ["required"] = new JsonArray("file"), ["properties"] = new JsonObject { ["file"] = new JsonObject { ["type"] = "string" } }, ["additionalProperties"] = false }) };
        if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
        if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
        if (type == typeof(int)) return new JsonObject { ["type"] = "integer" };
        if (type == typeof(JsonElement))
            return SpecShapes.Kind(member) switch
            {
                RawKind.Object => new JsonObject { ["type"] = "object" },
                RawKind.Elements => new JsonObject { ["oneOf"] = new JsonArray(new JsonObject { ["const"] = "*" }, new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }) },
                _ => new JsonObject(),
            };
        if (SpecModel.IsList(type))
        {
            var item = type.GetGenericArguments()[0];
            return new JsonObject { ["type"] = "array", ["items"] = item == typeof(JsonElement) ? RawItem(member) : For(item, member, defs) };
        }
        if (SpecModel.IsMap(type))
            return new JsonObject { ["type"] = "object", ["additionalProperties"] = For(type.GetGenericArguments()[1], member, defs) };
        if (!SpecModel.IsRecord(type)) return new JsonObject();
        if (!defs.ContainsKey(type.Name))
        {
            defs[type.Name] = new JsonObject(); // placeholder first: guards against a record that contains itself
            defs[type.Name] = Object(type, defs, new());
        }
        return new JsonObject { ["$ref"] = $"#/$defs/{type.Name}" };
    }

    static JsonObject RawItem(string member) => SpecShapes.Kind(member) switch
    {
        RawKind.Flow => Ref("FlowNode"),
        RawKind.ProcessList => Ref("Process"),
        RawKind.Named => Ref("Named"),
        RawKind.Widgets => Ref("Widget"),
        RawKind.Edits => Ref("Edit"),
        _ => new JsonObject(),
    };

    static JsonObject Ref(string name) => new() { ["$ref"] = $"#/$defs/{name}" };

    static JsonObject Str(params string[] examples) =>
        new() { ["type"] = "string", ["examples"] = new JsonArray(examples.Select(e => (JsonNode)e).ToArray()) };

    static void AddRawDefs(JsonObject defs)
    {
        defs["FlowNode"] = new JsonObject
        {
            ["type"] = "object", ["required"] = new JsonArray("kind"),
            ["properties"] = new JsonObject { ["kind"] = Str(SpecShapes.NodeKinds), ["name"] = Str(), ["next"] = Str() },
        };
        defs["Named"] = new JsonObject
        {
            ["type"] = "object", ["required"] = new JsonArray("name"), ["properties"] = new JsonObject { ["name"] = Str() },
        };
        defs["Process"] = new JsonObject
        {
            ["type"] = "object", ["required"] = new JsonArray("name"),
            ["properties"] = new JsonObject
            {
                ["name"] = Str(),
                ["flow"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["required"] = new JsonArray("kind"),
                        ["properties"] = new JsonObject { ["kind"] = Str(SpecShapes.ProcessKinds.Concat(SpecShapes.NodeKinds).ToArray()), ["name"] = Str(), ["next"] = Str() },
                    },
                },
            },
        };
        defs["Widget"] = new JsonObject
        {
            ["type"] = "object", ["required"] = new JsonArray("type"),
            ["properties"] = new JsonObject
            {
                ["type"] = Str("Container", "Expression", "Input", "Button", "Link", "Text", "If", "Form", "TableRecords", "BlockInstance"),
                ["children"] = new JsonObject { ["type"] = "array", ["items"] = Ref("Widget") },
            },
        };
        var ops = new JsonArray();
        foreach (var (op, def) in SpecShapes.EditOps)
        {
            var properties = new JsonObject { ["op"] = new JsonObject { ["const"] = op } };
            foreach (var key in def.Required.Concat(def.Optional))
                properties[key] = SpecShapes.EditValueKinds.TryGetValue(key, out var kind)
                    ? new JsonObject { ["type"] = kind switch { JsonValueKind.Array => "array", JsonValueKind.Object => "object", JsonValueKind.Number => "integer", _ => "boolean" } }
                    : new JsonObject { ["type"] = "string" };
            ops.Add(new JsonObject
            {
                ["properties"] = properties,
                ["required"] = new JsonArray(new[] { "op" }.Concat(def.Required).Select(r => (JsonNode)r).ToArray()),
            });
        }
        defs["Edit"] = new JsonObject { ["type"] = "object", ["required"] = new JsonArray("op"), ["oneOf"] = ops };
    }
}
