using System.Text.Json;
using OutSystems.Model;

namespace OmlCli;

// A screen or block data action: the action shape plus how it fetches.
record DataActionSpec(string Name, List<VarSpec>? Inputs = null, List<VarSpec>? Outputs = null, List<VarSpec>? Locals = null,
    List<JsonElement>? Flow = null, string? Description = null, string? Fetch = null, int? Timeout = null, string? OnAfterFetch = null)
    : ActionSpec(Name, Inputs, Outputs, Locals, Flow, Description: Description);

// Keys of the objects inside an aggregate's lists: required, then optional. The launcher's spec check reads this table.
static class QueryShapes
{
    internal static readonly Dictionary<string, (string[] Required, string[] Optional)> Parts = new()
    {
        ["joins"] = (new[] { "type", "left", "right", "condition" }, Array.Empty<string>()),
        ["sorts"] = (new[] { "attribute" }, new[] { "descending", "dynamic" }),
        ["groupSorts"] = (new[] { "attribute" }, new[] { "descending", "dynamic" }),
        ["groupBy"] = (new[] { "attribute" }, new[] { "as" }),
        ["aggregates"] = (new[] { "name", "function", "attribute" }, Array.Empty<string>()),
        ["calculated"] = (new[] { "name", "expression" }, Array.Empty<string>()),
        ["groupCalculated"] = (new[] { "name", "expression" }, Array.Empty<string>()),
    };

    /// The problems with the objects in an aggregate's lists. groupBy also takes plain expression strings.
    internal static List<string> Problems(JsonElement aggregate)
    {
        var problems = new List<string>();
        foreach (var (key, (required, optional)) in Parts)
        {
            if (!aggregate.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            var i = 0;
            foreach (var item in list.EnumerateArray())
            {
                var at = $"{key}[{i++}]";
                if (item.ValueKind == JsonValueKind.String && key == "groupBy") continue;
                if (item.ValueKind != JsonValueKind.Object) { problems.Add($"{at}: expected an object with {string.Join(", ", required)}"); continue; }
                foreach (var p in item.EnumerateObject().Where(p => !required.Contains(p.Name) && !optional.Contains(p.Name)))
                    problems.Add($"{at}: unknown key '{p.Name}'; use {string.Join(", ", required.Concat(optional))}");
                foreach (var missing in required.Where(r => !item.TryGetProperty(r, out _))) problems.Add($"{at}: '{missing}' is required");
            }
        }
        return problems;
    }
}

partial class Builder
{
    static IEnumerable<JsonElement> Items(JsonElement spec, string key) =>
        spec.TryGetProperty(key, out var value) ? value.EnumerateArray() : Enumerable.Empty<JsonElement>();

    static void Set(object owner, string property, object? value) =>
        (Reflect.Property(owner, property, writable: true)
            ?? throw new ModelError($"{owner.GetType().Name} has no writable {property} on this build")).SetValue(owner, value);

    static void SetEnum(object owner, string property, string value)
    {
        var p = Reflect.Property(owner, property, writable: true)
            ?? throw new ModelError($"{owner.GetType().Name} has no writable {property} on this build");
        p.SetValue(owner, Enum.Parse(p.PropertyType, value, ignoreCase: true));
    }

    void QueryAttributes(object db, JsonElement spec, string where)
    {
        foreach (var join in Items(spec, "joins"))
        {
            object? j = null;
            references.Add(($"{where}.joins", () =>
            {
                object Source(string key)
                {
                    var name = join.GetProperty(key).GetString();
                    return ((System.Collections.IEnumerable)Reflect.Get(db, "Sources")!).Cast<object>()
                        .SingleOrDefault(s => Model.NameOf(s) == name)
                        ?? throw new ModelError($"no aggregate source '{name}'");
                }
                var type = join.GetProperty("type").GetString()!.Trim().ToLowerInvariant() switch
                {
                    "only with" => "Inner",
                    "with or without" => "Left",
                    "with" => "Full",
                    var other => throw new ModelError($"unknown join type '{other}' (use only with, with or without, with)"),
                };
                var left = Source("left");
                var right = Source("right");
                j = Reflect.Call(db, "CreateJoin")!;
                Set(j, "LeftSource", left);
                Set(j, "RightSource", right);
                SetEnum(j, "JoinType", type);
            }));
            expressions.Add(($"{where}.joins.condition", () =>
            {
                if (j is not null) Reflect.Call(j, "SetCondition", join.GetProperty("condition").GetString());
            }));
        }
        foreach (var group in Items(spec, "groupBy"))
            expressions.Add(($"{where}.groupBy", () =>
            {
                var (attribute, alias) = group.ValueKind == JsonValueKind.Object
                    ? (group.TryGetProperty("attribute", out var at) ? at.GetString()! : throw new ModelError("a groupBy object needs 'attribute'"),
                       group.TryGetProperty("as", out var a) ? a.GetString() : null)
                    : (group.GetString()!, null);
                alias ??= GroupAlias(attribute);
                // Checked before creation, so the model never renames the group.
                if (!IsName(alias)) throw new ModelError($"group name '{alias}' is invalid; use ASCII letters, digits and _, not starting with a digit");
                if (OutputNames(db).Contains(alias, StringComparer.OrdinalIgnoreCase))
                    throw new ModelError($"group name '{alias}' is taken in this aggregate; give the group another name with \"as\"");
                var g = Reflect.Call(db, "CreateGroupByAttribute", alias, services.NewKey())!;
                if (Model.NameOf(g) != alias) throw new ModelError($"group {alias}: {NameClash.Taken(g, alias)}; the model named it '{Model.NameOf(g)}'");
                Reflect.Call(g, "SetAttribute", attribute);
            }));
        foreach (var attribute in Items(spec, "aggregates"))
            expressions.Add(($"{where}.aggregates", () =>
            {
                var name = attribute.GetProperty("name").GetString()!;
                var function = attribute.GetProperty("function").GetString()!;
                if (!new[] { "count", "sum", "avg", "min", "max" }.Contains(function.ToLowerInvariant()))
                    throw new ModelError($"unknown aggregate function '{function}'");
                var a = Reflect.Call(db, "CreateAggregateAttribute", name, services.NewKey())!;
                if (!Fresh(a, name, $"{where}.{name}")) return;
                SetEnum(a, "AggregationType", function);
                Reflect.Call(a, "SetAttribute", attribute.GetProperty("attribute").GetString());
            }));
        foreach (var (key, factory) in new[] { ("calculated", "CreateCalculatedAttribute"), ("groupCalculated", "CreateCalculatedAttributeInGroupBy") })
            foreach (var attribute in Items(spec, key))
                expressions.Add(($"{where}.{key}", () =>
                {
                    var name = attribute.GetProperty("name").GetString()!;
                    var a = Reflect.Call(db, factory, name, services.NewKey())!;
                    if (Fresh(a, name, $"{where}.{name}"))
                        Reflect.Call(a, "SetValue", attribute.GetProperty("expression").GetString());
                }));
        // Group-level filters and sorts run on the grouped rows (SQL HAVING and ORDER BY of the groups).
        foreach (var filter in Items(spec, "groupFilters"))
            expressions.Add(($"{where}.groupFilters", () => Reflect.Call(db, "CreateFilterInGroupBy", filter.GetString())));
        Sorts(db, spec, "groupSorts", "CreateSortInGroupBy", where);
    }

    void Sorts(object db, JsonElement spec, string key, string factory, string where)
    {
        foreach (var sort in Items(spec, key))
            Defer(db, ($"{where}.{key}", () =>
            {
                var s = Reflect.Call(db, factory)!;
                // A dynamic sort is a Text expression evaluated at run time ("Person.Name DESC"), not an attribute.
                if (sort.TryGetProperty("dynamic", out var dynamic) && dynamic.GetBoolean()) Set(s, "IsDynamic", true);
                Reflect.Call(s, "SetAttribute", sort.GetProperty("attribute").GetString());
                if (sort.TryGetProperty("descending", out var desc) && desc.GetBoolean())
                    Reflect.Property(s, "SortDirection", writable: true)!.SetValue(s, OutSystems.Model.Enumerations.Sort.Descending);
            }));
    }

    // Screen and block aggregates and data actions run at screen start (atStart) or only on a refreshData (onDemand).
    void Fetch(object owner, string? value, string where) => references.Add(($"{where}.fetch", () =>
    {
        var p = Reflect.Property(owner, "Fetch", writable: true)
                ?? throw new ModelError("fetch applies only to screen and block aggregates and data actions");
        p.SetValue(owner, Enum.Parse(p.PropertyType, value?.ToLowerInvariant() switch
        {
            "atstart" => "AtStart",
            "ondemand" => "OnDemand",
            _ => throw new ModelError($"fetch '{value}' is not atStart or onDemand"),
        }));
    }));

    // Whole numbers, checked here: through reflection "ten" would be looked up as an element name.
    void Numbers(object aggregate, JsonElement spec, string where)
    {
        foreach (var (key, property, unit) in new[] { ("timeout", "Timeout", "seconds"), ("cacheInMinutes", "CacheInMinutes", "minutes") })
            if (spec.TryGetProperty(key, out var value))
                references.Add(($"{where}.{key}", () =>
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < 0)
                        throw new ModelError($"{key} must be a whole number of {unit}, got {value.GetRawText()}");
                    if (key == "cacheInMinutes" && Reflect.Property(aggregate, "Fetch") is not null)
                        throw new ModelError("cacheInMinutes applies only to aggregates in server actions; screen and block aggregates are client aggregates");
                    Set(aggregate, property, n);
                }));
    }

    // After a fetch, run a screen action of the owning screen or block, like OnInitialize.
    void AfterFetch(object owner, string? handler, string where) => references.Add(($"{where}.onAfterFetch", () =>
    {
        var evt = Reflect.Get(owner, "OnAfterFetch")
                  ?? throw new ModelError("onAfterFetch applies only to screen and block aggregates and data actions");
        var screen = ((IObjectSignature)owner).Parent!;
        var target = Elements(Reflect.Get(screen, "ScreenActions")).FirstOrDefault(a => Model.NameOf(a) == handler)
                     ?? throw new ModelError($"onAfterFetch: no screen action '{handler}' in '{Model.NameOf(screen)}'");
        Set(evt, "Destination", target);
    }));

    // Spec keys of an aggregate that the builder reads itself; any other key is a property of the aggregate.
    static readonly HashSet<string> QueryKeys = new(StringComparer.OrdinalIgnoreCase)
        { "name", "sources", "filters", "sorts", "maxRecords", "joins", "groupBy", "aggregates", "calculated", "groupFilters", "groupSorts", "groupCalculated", "fetch", "onAfterFetch", "timeout", "cacheInMinutes" };

    // The model stores group and index names in ASCII (it turns "Função" into "Funcao"), so oml asks for ASCII names.
    internal static bool IsName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$");

    static IEnumerable<string> OutputNames(object db) =>
        new[] { "GroupByAttributes", "AggregateAttributes", "CalculatedAttributes", "CalculatedAttributesInGroupBy" }
            .SelectMany(key => Elements(Reflect.Get(db, key))).Select(o => Model.NameOf(o)).OfType<string>();

    /// The output name of a group: the attribute name of a plain path (Order.CustomerId -> CustomerId), else the
    /// first and last names in the expression, text literals left out (Year(Order.PlacedOn) -> YearPlacedOn).
    /// Accents are dropped as the model does. "as" overrides it.
    internal static string GroupAlias(string expression)
    {
        const string word = @"[\p{L}_][\p{L}\p{N}_]*";
        var code = System.Text.RegularExpressions.Regex.Replace(expression, "\"(?:[^\"]|\"\")*\"", " ").Trim();
        var words = System.Text.RegularExpressions.Regex.Matches(code, word).Select(x => Ascii(x.Value)).Where(w => w.Length > 0).ToList();
        if (words.Count == 0) return "Group";
        var alias = System.Text.RegularExpressions.Regex.IsMatch(code, $@"^{word}(\.{word})*$") || words.Count == 1
            ? words[^1] : words[0] + words[^1];
        return char.IsDigit(alias[0]) ? "_" + alias : alias;
    }

    static string Ascii(string text) => new string(text.Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_').ToArray());

    void QueryNode(object action, object node, JsonElement spec, string kind, string where)
    {
        if (kind.Equals("destination", StringComparison.OrdinalIgnoreCase) && spec.TryGetProperty("screen", out var destination))
            references.Add(($"{where}.screen", () => Set(node, "Destination",
                Refs.Find(m, destination.GetString()!, typeof(OutSystems.Model.UI.IScreenSignature)))));
        if (kind.Equals("refreshData", StringComparison.OrdinalIgnoreCase))
            references.Add(($"{where}.dataSource", () =>
            {
                var name = spec.GetProperty("dataSource").GetString();
                var screen = ((IObjectSignature)action).Parent;
                while (screen is not null && !Reflect.Has(screen, "CreateScreenAggregate")) screen = screen.Parent;
                if (screen is null) throw new ModelError("refreshData requires an owning screen or block");
                var sources = new[] { "ScreenAggregates", "DataActions" }.SelectMany(key =>
                    Reflect.Get(screen, key) is System.Collections.IEnumerable list ? list.Cast<object>() : Enumerable.Empty<object>());
                Set(node, "DataSource", sources.SingleOrDefault(s => Model.NameOf(s) == name)
                    ?? throw new ModelError($"no data source '{name}' in owning screen '{Model.NameOf(screen)}'"));
            }));
        if (!kind.Equals("execute", StringComparison.OrdinalIgnoreCase) && spec.TryGetProperty("args", out var args))
            expressions.Add(($"{where}.args", () => Arguments(node, args)));
    }

    void SwitchTargets(object node, JsonElement spec, Func<string?, object> resolve)
    {
        foreach (var c in Items(spec, "cases"))
        {
            var condition = Reflect.Call(node, "CreateCondition")!;
            Connect(condition, "Target", resolve(c.GetProperty("next").GetString()));
            expressions.Add(("switch.condition", () => Reflect.Call(condition, "SetValue", c.GetProperty("condition").GetString())));
        }
        Connect(node, "OtherwiseTarget", resolve(spec.TryGetProperty("otherwise", out var otherwise)
            ? otherwise.GetString() : throw new ModelError("switch requires 'otherwise' target")));
    }
}

